using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using NAudio.Wave;

namespace DMUModelScale;

/// <summary>Where the player's Phase 2 media audio is, sampled on a framework update.</summary>
internal sealed record MediaClock(string Path, double Seconds, long Timestamp, bool Playing);

internal sealed unsafe class AudioController(IGameConfig gameConfig, string cacheDirectory) : IDisposable
{
    private static readonly SoundBus[] ExtraMusicBuses = [SoundBus.TimeStretchBGM, SoundBus.Zingle];

    private readonly List<(WaveOutEvent Output, AudioFileReader Reader)> effects = new();
    private readonly bool[] savedBusMuted = new bool[ExtraMusicBuses.Length];
    private WaveOutEvent? bgmOutput;
    private AudioFileReader? bgmReader;
    private AudioPhase activeBgmPhase = AudioPhase.Outside;
    private string? activeBgmKey;
    private string? activeMediaPath;
    private string? failedBgmAsset;
    private string? failedMediaPath;
    private string mediaError = string.Empty;
    private volatile MediaClock? mediaClock;
    private bool failedBonk;
    private bool gameBgmSuppressed;
    private bool lastGameBgmEnabled = true;
    private int pendingSlams;
    private string status = "Audio options are off.";

    public string Status => status;

    /// <summary>Playback position of the Phase 2 media file, or null while it is not playing.</summary>
    public MediaClock? CurrentMediaClock => mediaClock;

    public void QueueSlam() => Interlocked.Increment(ref pendingSlams);

    public void Update(bool inDuty, AudioPhase phase, bool brainrot, string? phase2MediaPath)
    {
        UpdateBgm(inDuty, phase, brainrot, phase2MediaPath);
        UpdateMediaClock();

        for (var i = effects.Count - 1; i >= 0; i--)
        {
            if (!inDuty || !brainrot || effects[i].Output.PlaybackState == PlaybackState.Stopped)
            {
                effects[i].Output.Dispose();
                effects[i].Reader.Dispose();
                effects.RemoveAt(i);
            }
        }

        var slams = Math.Min(Interlocked.Exchange(ref pendingSlams, 0), 8);
        if (!inDuty || !brainrot)
        {
            failedBonk = false;
            return;
        }
        if (slams == 0 || failedBonk)
            return;

        try
        {
            var bonkPath = GetBundledPath("Bonk.mp3");
            for (var i = 0; i < slams && effects.Count < 8; i++)
                PlayEffect(bonkPath);
        }
        catch (Exception ex)
        {
            failedBonk = true;
            status = $"Bundled Bonk sound could not load: {ex.Message}";
        }
    }

    private void UpdateBgm(bool inDuty, AudioPhase phase, bool replaceBgm, string? phase2MediaPath)
    {
        var asset = phase switch
        {
            AudioPhase.Phase1 => "Phase1.mp3",
            AudioPhase.Phase2 => "Phase2.mp3",
            _ => "Circus.mp3"
        };
        // The player's own file replaces the bundled Phase 2 track. If it fails, fall back to the bundled one.
        var media = phase == AudioPhase.Phase2 && phase2MediaPath is not null && failedMediaPath != phase2MediaPath
            ? phase2MediaPath
            : null;
        var key = media ?? asset;
        if (!inDuty || !replaceBgm)
        {
            StopBgm();
            failedBgmAsset = null;
            failedMediaPath = null;
            status = inDuty ? "Fight BGM replacement is off." : "Audio plays only inside Dancing Mad (Ultimate).";
            return;
        }

        if (!TryReadBgmSettings(out var enabled, out var volume))
        {
            StopBgm();
            status = "Could not read the game's BGM volume or mute settings.";
            return;
        }

        if (!enabled || volume <= 0f)
        {
            if (bgmOutput?.PlaybackState == PlaybackState.Playing)
                bgmOutput.Pause();
            RestoreGameBgm();
            failedBgmAsset = null;
            status = "Bundled music is paused while game BGM or master sound is off.";
            return;
        }

        if (failedBgmAsset == key)
        {
            RestoreGameBgm();
            return;
        }

        try
        {
            SuppressGameBgm();
        }
        catch (Exception ex)
        {
            StopBgm();
            status = $"Could not silence the game's BGM: {ex.Message}";
            return;
        }

        if (bgmOutput?.PlaybackState == PlaybackState.Stopped)
        {
            StopBgm();
            failedBgmAsset = key;
            status = "Audio output stopped. Toggle Brainrot off and on to retry.";
            return;
        }

        if (bgmOutput is not null && activeBgmKey == key)
        {
            bgmReader!.Volume = volume;
            if (bgmOutput.PlaybackState == PlaybackState.Paused)
                bgmOutput.Play();
            return;
        }

        StopBgm(false);
        if (media is not null)
        {
            try
            {
                StartBgm(media, key, phase, volume, media);
                return;
            }
            catch (Exception ex)
            {
                StopBgm(false);
                failedMediaPath = media;
                mediaError = $" Your Phase 2 media file could not play, so the bundled track is used: {ex.Message}";
            }
        }

        try
        {
            var path = GetBundledPath(asset);
            StartBgm(path, asset, phase, volume, null);
        }
        catch (Exception ex)
        {
            StopBgm();
            failedBgmAsset = asset;
            status = $"Bundled BGM could not start: {ex.Message}";
        }
    }

    private void UpdateMediaClock()
    {
        if (bgmOutput is null || bgmReader is null || activeMediaPath is null)
        {
            mediaClock = null;
            return;
        }

        // WaveOut reports what has actually been played, so the video follows what is heard.
        var total = bgmReader.TotalTime.TotalSeconds;
        var bytesPerSecond = bgmOutput.OutputWaveFormat.AverageBytesPerSecond;
        var played = bytesPerSecond > 0 ? bgmOutput.GetPosition() / (double)bytesPerSecond : 0;
        var position = total > 0 ? played % total : played;
        mediaClock = new MediaClock(activeMediaPath, position, Stopwatch.GetTimestamp(),
            bgmOutput.PlaybackState == PlaybackState.Playing);
    }

    private bool TryReadBgmSettings(out bool enabled, out float volume)
    {
        enabled = false;
        volume = 0f;
        if (!gameConfig.TryGet(SystemConfigOption.IsSndBgm, out uint bgmMuted) ||
            !gameConfig.TryGet(SystemConfigOption.IsSndMaster, out uint masterMuted) ||
            !gameConfig.TryGet(SystemConfigOption.SoundBgm, out uint bgmVolume) ||
            !gameConfig.TryGet(SystemConfigOption.SoundMaster, out uint masterVolume))
            return false;

        lastGameBgmEnabled = bgmMuted == 0;
        enabled = lastGameBgmEnabled && masterMuted == 0;
        volume = Math.Clamp(bgmVolume / 100f, 0f, 1f) *
            Math.Clamp(masterVolume / 100f, 0f, 1f);
        return true;
    }

    private void SuppressGameBgm()
    {
        var sound = SoundManager.Instance();
        if (sound == null)
            throw new InvalidOperationException("The game's sound manager is unavailable.");

        // Mute the native BGM bus without changing the player's BGM checkbox.
        // Repeat on framework updates because the game can reapply its setting.
        sound->SetBgmEnabled(false);

        // Fights can also play music on the time-stretched BGM and short stinger buses,
        // which the BGM switch does not cover. Remember their state once and mute them.
        var busMuted = sound->BusMuted;
        for (var i = 0; i < ExtraMusicBuses.Length; i++)
        {
            var index = (int)ExtraMusicBuses[i];
            if (index >= busMuted.Length)
                continue;
            if (!gameBgmSuppressed)
                savedBusMuted[i] = busMuted[index];
            busMuted[index] = true;
        }
        gameBgmSuppressed = true;
    }

    private void RestoreGameBgm()
    {
        if (!gameBgmSuppressed)
            return;

        var sound = SoundManager.Instance();
        if (sound != null)
        {
            try
            {
                var enabled = gameConfig.TryGet(SystemConfigOption.IsSndBgm, out uint muted)
                    ? muted == 0
                    : lastGameBgmEnabled;
                sound->SetBgmEnabled(enabled);
                var busMuted = sound->BusMuted;
                for (var i = 0; i < ExtraMusicBuses.Length; i++)
                    if ((int)ExtraMusicBuses[i] < busMuted.Length)
                        busMuted[(int)ExtraMusicBuses[i]] = savedBusMuted[i];
            }
            catch (Exception ex)
            {
                status = $"Could not restore the game's BGM: {ex.Message}";
                return;
            }
        }
        gameBgmSuppressed = false;
    }

    private string GetBundledPath(string fileName)
    {
        Directory.CreateDirectory(cacheDirectory);
        var path = Path.Combine(cacheDirectory, fileName);
        var resourceName = $"DMUModelScale.Assets.Audio.{fileName}";
        using var source = typeof(AudioController).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Missing embedded audio: {fileName}");
        if (File.Exists(path) && new FileInfo(path).Length == source.Length)
            return path;

        var temporaryPath = path + ".tmp";
        using (var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            source.CopyTo(destination);
        File.Move(temporaryPath, path, true);
        return path;
    }

    private void StartBgm(string path, string key, AudioPhase phase, float volume, string? mediaPath)
    {
        activeBgmPhase = phase;
        activeBgmKey = key;
        activeMediaPath = mediaPath;
        try
        {
            // Formats other than WAV, MP3 and AIFF (MP4, M4A, FLAC, ...) are decoded by Windows Media Foundation.
            bgmReader = new AudioFileReader(path);
            if (bgmReader.TotalTime <= TimeSpan.Zero)
                throw new InvalidDataException("BGM file is empty.");
            bgmReader.Volume = volume;

            bgmOutput = new WaveOutEvent();
            bgmOutput.Init(new LoopingSampleProvider(bgmReader));
            bgmOutput.Play();
            if (mediaPath is not null)
            {
                status = "Playing your Phase 2 media file at the game's BGM volume.";
                return;
            }
            var phaseName = phase switch
            {
                AudioPhase.Phase1 => "Phase 1",
                AudioPhase.Phase2 => "Phase 2",
                _ => "Phase 3+ Circus"
            };
            status = $"Playing bundled {phaseName} BGM at the game's BGM volume.";
            if (phase == AudioPhase.Phase2 && failedMediaPath is not null)
                status += mediaError;
        }
        catch
        {
            StopBgm();
            throw;
        }
    }

    private void StopBgm(bool restoreGameBgm = true)
    {
        bgmOutput?.Dispose();
        bgmOutput = null;
        bgmReader?.Dispose();
        bgmReader = null;
        activeBgmPhase = AudioPhase.Outside;
        activeBgmKey = null;
        activeMediaPath = null;
        mediaClock = null;

        if (restoreGameBgm)
            RestoreGameBgm();
    }

    private void PlayEffect(string path)
    {
        AudioFileReader? reader = null;
        WaveOutEvent? output = null;
        try
        {
            reader = new AudioFileReader(path);
            output = new WaveOutEvent();
            output.Init(reader);
            output.Play();
            effects.Add((output, reader));
        }
        catch (Exception ex)
        {
            output?.Dispose();
            reader?.Dispose();
            failedBonk = true;
            status = $"Bundled Bonk sound could not play: {ex.Message}";
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref pendingSlams, 0);
        foreach (var effect in effects)
        {
            effect.Output.Dispose();
            effect.Reader.Dispose();
        }
        effects.Clear();
        StopBgm();
    }

    private sealed class LoopingSampleProvider(AudioFileReader reader) : ISampleProvider
    {
        public WaveFormat WaveFormat => reader.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = reader.Read(buffer, offset + total, count - total);
                if (read > 0)
                {
                    total += read;
                    continue;
                }
                if (reader.Position == 0)
                    break;
                reader.Position = 0;
            }
            return total;
        }
    }
}
