using System.Diagnostics;
using Sidechain.Features.Audio;
using Sidechain.Features.Core;
using Sidechain.Shell;

namespace Sidechain.Demo;

/// <summary>
/// --demo audio: made-up apps and devices, Discord talks by a script; no real audio is touched (spec §10).
/// Peaks are taken before the session volume, as in Windows (AudioFacts), so ducking never moves a meter.
/// </summary>
public sealed class DemoBackend : IAudioBackend
{
    /// <summary>Seconds of the script; --autoplay restarts it and makes Discord talk from 2.5 s to 6 s only.</summary>
    public static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double TalkFrom = double.NegativeInfinity, TalkTo = double.PositiveInfinity;

    static readonly (string App, string Title, float Volume, int Sessions)[] Apps =
    [
        ("discord", "Discord", 1f, 1), ("spotify", "Spotify", 0.8f, 1), ("chrome", "Google Chrome", 1f, 3),
        ("dota2", "Dota 2", 0.65f, 1), ("telegram", "Telegram Desktop", 1f, 1), ("scrcpy", "scrcpy", 0.5f, 1),
    ];

    readonly List<AudioSession> sessions = [];
    readonly Dictionary<string, float> volumes = [];
    readonly List<AudioDevice> devices = [new("demo-phones", Loc.T("Demo.Phones")), new("demo-speakers", Loc.T("Demo.Speakers")), new("demo-dell", "DELL U2723QE (NVIDIA High Definition Audio)")];
    readonly List<AudioDevice> mics = [new("demo-mic-phones", Loc.T("Demo.Mic")), new("demo-mic-camera", Loc.T("Demo.Camera"))];
    string defaultId = "demo-phones", micId = "demo-mic-phones";
    readonly Dictionary<string, float> masters = new() { ["demo-phones"] = -10, ["demo-speakers"] = -20, ["demo-dell"] = -6 };

    public DemoBackend()
    {
        var pid = 4100;
        foreach (var (app, _, volume, count) in Apps)
            for (var i = 0; i < count; i++)
            {
                var s = new AudioSession($"demo-{app}-{i}", app, pid += 12, "demo-phones");
                sessions.Add(s);
                volumes[s.Key] = volume;
            }
    }

    public IReadOnlyList<AudioSession> Enumerate() => sessions;

    public float Peak(AudioSession s)
    {
        var t = Clock.Elapsed.TotalSeconds;
        var i = s.Key[^1] - '0';
        return s.App switch
        {
            "discord" when t >= TalkFrom && t < TalkTo => (float)(0.35 + 0.3 * Math.Abs(Math.Sin(t * 7.3)) + 0.15 * Math.Sin(t * 23.1)), // syllables
            "spotify" => (float)(0.62 + 0.18 * Math.Sin(t * 5.1) + 0.08 * Math.Sin(t * 17.3)),
            "chrome" => i == 0 ? (float)(0.48 + 0.12 * Math.Sin(t * 3.7)) : 0,
            "dota2" => (float)(0.3 + 0.12 * Math.Sin(t * 9.7)),
            "scrcpy" => (float)(0.12 + 0.05 * Math.Sin(t * 4.3)),
            _ => 0,
        };
    }

    public float GetVolume(AudioSession s) => volumes[s.Key];
    public void SetVolume(AudioSession s, float volume) => volumes[s.Key] = volume;
    public IReadOnlyList<AudioDevice> Devices(bool capture = false) => capture ? mics : devices;
    public string? DefaultId(bool capture = false) => capture ? micId : defaultId;
    public void SetDefault(string id, bool communications)
    {
        if (mics.Any(m => m.Id == id)) micId = id;
        else defaultId = id;
    }

    /// <summary>Chrome plays a loud ad: −12 dBFS before the volume, the leveler's norm is −20.</summary>
    public ILevel? StartLevel(int pid) => sessions.FirstOrDefault(s => s.Pid == pid) is { } s ? new Level(this, s) : null;

    sealed class Level(DemoBackend b, AudioSession s) : ILevel
    {
        public int Pid => s.Pid;
        public double LevelDb => -12 + 1.5 * Math.Sin(Clock.Elapsed.TotalSeconds * 2.1) + Db.FromScalar(b.volumes[s.Key]); // after the session volume
        public void Dispose() { }
    }
    public (string Title, string? ExePath) Describe(AudioSession s) => (Apps.First(a => a.App == s.App).Title, null);
    public MasterVolume? Master(string deviceId) => masters.TryGetValue(deviceId, out var db) ? new MasterVolume(db, -65.25f, 0) : null;
    public void SetMaster(string deviceId, float db) => masters[deviceId] = Math.Clamp(db, -65.25f, 0);
    public void Dispose() { }
}
