using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;
using NAudio.Wave;

namespace DMUModelScale;

internal sealed class AudioController(IGameConfig gameConfig, string cacheDirectory) : IDisposable
{
    private readonly List<(WaveOutEvent Output, AudioFileReader Reader)> effects = new();
    private WaveOutEvent? bgmOutput;
    private AudioFileReader? bgmReader;
    private AudioPhase activeBgmPhase = AudioPhase.Outside;
    private string? failedBgmAsset;
    private bool failedBonk;
    private uint? originalBgmSetting;
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
        if (bgmOutput?.PlaybackState == PlaybackState.Stopped)
        {
            StopBgm();
            failedBgmAsset = asset;
            status = "Audio output stopped. Toggle Brainrot off and on to retry.";
        }

        if (!inDuty || !replaceBgm)
        {
            StopBgm();
            failedBgmAsset = null;
            status = inDuty ? "Fight BGM replacement is off." : "Audio plays only inside Dancing Mad (Ultimate).";
            return;
        }

        if (bgmOutput is not null && activeBgmPhase == phase)
            return;

        StopBgm();
        if (failedBgmAsset == asset)
            return;

        try
        {
            var path = GetBundledPath(asset);
            StartBgm(path, phase);
        }
        catch (Exception ex)
        {
            StopBgm();
            failedBgmAsset = asset;
            status = $"Bundled BGM could not start: {ex.Message}";
        }
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

    private void StartBgm(string path, AudioPhase phase)
    {
        activeBgmPhase = phase;
        try
        {
            bgmReader = new AudioFileReader(path);
            if (bgmReader.TotalTime <= TimeSpan.Zero)
                throw new InvalidDataException("BGM file is empty.");

            bgmOutput = new WaveOutEvent();
            bgmOutput.Init(new LoopingSampleProvider(bgmReader));
            if (!gameConfig.TryGet(SystemConfigOption.IsSndBgm, out uint current))
                throw new InvalidOperationException("Could not read the game's BGM setting.");

            originalBgmSetting = current;
            gameConfig.Set(SystemConfigOption.IsSndBgm, 0u);
            bgmOutput.Play();
            var phaseName = phase switch
            {
                AudioPhase.Phase1 => "Phase 1",
                AudioPhase.Phase2 => "Phase 2",
                _ => "Phase 3+ Circus"
            };
            status = $"Playing bundled {phaseName} BGM on a loop; game BGM is muted.";
        }
        catch
        {
            StopBgm();
            throw;
        }
    }

    private void StopBgm()
    {
        bgmOutput?.Dispose();
        bgmOutput = null;
        bgmReader?.Dispose();
        bgmReader = null;
        activeBgmPhase = AudioPhase.Outside;

        if (originalBgmSetting is not { } original)
            return;
        try
        {
            // Respect a change the player made while the replacement was running.
            if (gameConfig.TryGet(SystemConfigOption.IsSndBgm, out uint current) && current == 0)
                gameConfig.Set(SystemConfigOption.IsSndBgm, original);
            originalBgmSetting = null;
        }
        catch (Exception ex)
        {
            status = $"Could not restore the game BGM setting: {ex.Message}";
        }
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
