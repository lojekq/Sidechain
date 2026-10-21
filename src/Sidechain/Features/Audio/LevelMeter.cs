using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sidechain.Shell;

namespace Sidechain.Features.Audio;

/// <summary>
/// RMS of one process tree (process loopback, Windows 10 20348+), 400 ms windows of the mono mix, in dBFS.
/// The samples stay in memory for the sum and are dropped: nothing is recorded, written or sent.
/// Captured after the session volume (AudioFacts.LoopbackIsPostVolume): the engine takes its own gain back out.
/// </summary>
public sealed class LevelMeter : ILevel
{
    const double WindowS = 0.4;

    readonly WasapiRecorder recorder;
    readonly int channels, window;
    double sum, level = double.NaN;
    int frames;
    long updated;

    public int Pid { get; }

    /// <summary>The last 400 ms in dBFS; NaN when nothing came for a second (the app is silent or paused).</summary>
    public double LevelDb => Environment.TickCount64 - Volatile.Read(ref updated) > 1000 ? double.NaN : Volatile.Read(ref level);

    LevelMeter(int pid, WasapiRecorder recorder)
    {
        Pid = pid;
        this.recorder = recorder;
        channels = Math.Max(1, recorder.WaveFormat.Channels);
        window = (int)(recorder.WaveFormat.SampleRate * WindowS);
        recorder.DataAvailable += (buffer, _, _, _) => Add(buffer);
        recorder.StartRecording();
    }

    /// <summary>Null where process loopback is missing or fails; the app then simply isn't leveled.</summary>
    public static LevelMeter? Start(int pid)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return null;
        WasapiRecorder? r = null;
        try
        {
            r = new WasapiRecorderBuilder().WithProcessLoopback((uint)pid, ProcessLoopbackMode.IncludeTargetProcessTree).BuildAsync().GetAwaiter().GetResult();
            if (r.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat && r.WaveFormat is not WaveFormatExtensible { BitsPerSample: 32 })
                throw new NotSupportedException($"capture format {r.WaveFormat}");
            return new LevelMeter(pid, r);
        }
        catch (Exception e)
        {
            Log.Warn("process loopback unavailable", e);
            r?.Dispose();
            return null;
        }
    }

    void Add(ReadOnlySpan<byte> buffer)
    {
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        for (var i = 0; i + channels <= samples.Length; i += channels)
        {
            float mono = 0;
            for (var c = 0; c < channels; c++) mono += samples[i + c];
            mono /= channels;
            sum += mono * mono;
            if (++frames < window) continue;
            Volatile.Write(ref level, 10 * Math.Log10(sum / frames + 1e-12));
            Volatile.Write(ref updated, Environment.TickCount64);
            sum = 0;
            frames = 0;
        }
    }

    public void Dispose()
    {
        try { recorder.StopRecording(); } catch { }
        recorder.Dispose();
    }
}
