using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Sound;
using NAudio.Wave;

namespace DMUModelScale;

internal sealed unsafe class AudioController(IGameConfig gameConfig, string cacheDirectory) : IDisposable
{
    private readonly List<(WaveOutEvent Output, AudioFileReader Reader)> effects = new();
    private WaveOutEvent? bgmOutput;
    private AudioFileReader? bgmReader;
    private AudioPhase activeBgmPhase = AudioPhase.Outside;
    private string? failedBgmAsset;
    private bool failedBonk;
    private bool gameBgmSuppressed;
    private bool lastGameBgmEnabled = true;
    private int pendingSlams;
    private string status = "Audio options are off.";

    public string Status => status;

    public void QueueSlam() => Interlocked.Increment(ref pendingSlams);

    public void Update(bool inDuty, AudioPhase phase, bool brainrot)
    {
        UpdateBgm(inDuty, phase, brainrot);

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

    private void UpdateBgm(bool inDuty, AudioPhase phase, bool replaceBgm)
    {
        var asset = phase switch
        {
            AudioPhase.Phase1 => "Phase1.mp3",
            AudioPhase.Phase2 => "Phase2.mp3",
            _ => "Circus.mp3"
        };
        if (!inDuty || !replaceBgm)
        {
            StopBgm();
            failedBgmAsset = null;
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

        if (failedBgmAsset == asset)
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
            failedBgmAsset = asset;
            status = "Audio output stopped. Toggle Brainrot off and on to retry.";
            return;
        }

        if (bgmOutput is not null && activeBgmPhase == phase)
        {
            bgmReader!.Volume = volume;
            if (bgmOutput.PlaybackState == PlaybackState.Paused)
                bgmOutput.Play();
            return;
        }

        StopBgm(false);
        try
        {
            var path = GetBundledPath(asset);
            StartBgm(path, phase, volume);
        }
        catch (Exception ex)
        {
            StopBgm();
            failedBgmAsset = asset;
            status = $"Bundled BGM could not start: {ex.Message}";
        }
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

    private void StartBgm(string path, AudioPhase phase, float volume)
    {
        activeBgmPhase = phase;
        try
        {
            bgmReader = new AudioFileReader(path);
            if (bgmReader.TotalTime <= TimeSpan.Zero)
                throw new InvalidDataException("BGM file is empty.");
            bgmReader.Volume = volume;

            bgmOutput = new WaveOutEvent();
            bgmOutput.Init(new LoopingSampleProvider(bgmReader));
            bgmOutput.Play();
            var phaseName = phase switch
            {
                AudioPhase.Phase1 => "Phase 1",
                AudioPhase.Phase2 => "Phase 2",
                _ => "Phase 3+ Circus"
            };
            status = $"Playing bundled {phaseName} BGM at the game's BGM volume.";
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
