using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Sidechain.Features.Core;
using Sidechain.Shell;

namespace Sidechain.Features.Audio;

/// <summary>
/// NAudio.Wasapi over Core Audio. Wrappers are cached by device id and session instance id; a new enumeration gives new
/// wrappers for the same sessions, the duplicates are disposed (safe: checked in the spike), expired ones too.
/// </summary>
public sealed class CoreAudioBackend : IAudioBackend
{
    readonly MMDeviceEnumerator enumerator = new();
    readonly Dictionary<string, (MMDevice Device, string Name)> devices = [];
    readonly Dictionary<string, AudioSessionControl> sessions = [];
    readonly Dictionary<int, string> apps = [];                       // pid → app
    readonly Dictionary<string, (string, string?)> titles = [];       // app → title, exe

    public IReadOnlyList<AudioDevice> Devices()
    {
        var seen = new HashSet<string>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            seen.Add(d.ID);
            if (devices.ContainsKey(d.ID)) d.Dispose();
            else devices[d.ID] = (d, d.FriendlyName);
        }
        foreach (var id in devices.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            devices[id].Device.Dispose();
            devices.Remove(id);
        }
        return devices.Select(kv => new AudioDevice(kv.Key, kv.Value.Name)).ToList();
    }

    public IReadOnlyList<AudioSession> Enumerate()
    {
        if (devices.Count == 0) Devices();
        var found = new List<AudioSession>();
        var keys = new HashSet<string>();
        foreach (var (id, (device, _)) in devices)
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var list = manager.Sessions;
            for (var i = 0; i < list.Count; i++)
            {
                var c = list[i];
                try
                {
                    if (c.State == AudioSessionState.AudioSessionStateExpired || c.IsSystemSoundsSession) { c.Dispose(); continue; }
                    var key = c.GetSessionInstanceIdentifier;
                    if (!keys.Add(key)) { c.Dispose(); continue; }
                    if (sessions.TryGetValue(key, out var cached)) { c.Dispose(); c = cached; }
                    else sessions[key] = c;
                    var pid = (int)c.GetProcessID;
                    if (App(pid) is { } app) found.Add(new(key, app, pid, id));
                }
                catch (Exception e) when (e is not OutOfMemoryException) { c.Dispose(); } // the session went away meanwhile
            }
        }
        foreach (var key in sessions.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            sessions[key].Dispose();
            sessions.Remove(key);
        }
        var pids = found.Select(s => s.Pid).ToHashSet();
        foreach (var pid in apps.Keys.Where(p => !pids.Contains(p)).ToList()) apps.Remove(pid);
        return found;
    }

    /// <summary>Process name without OpenProcess, so protected processes work too; null when the process is gone.</summary>
    string? App(int pid)
    {
        if (apps.TryGetValue(pid, out var app)) return app;
        try { using var p = Process.GetProcessById(pid); return apps[pid] = Rules.AppName(p.ProcessName); }
        catch (ArgumentException) { return null; }
    }

    AudioSessionControl Session(AudioSession s) => sessions.TryGetValue(s.Key, out var c) ? c : throw new InvalidOperationException("session is gone");

    public float Peak(AudioSession s) => Session(s).AudioMeterInformation.MasterPeakValue;
    public float GetVolume(AudioSession s) => Session(s).SimpleAudioVolume.Volume;
    public void SetVolume(AudioSession s, float volume) => Session(s).SimpleAudioVolume.Volume = Math.Clamp(volume, 0, 1);

    public string? DefaultId() =>
        enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var d) ? Take(d) : null;

    static string Take(MMDevice d)
    {
        using (d) return d.ID;
    }

    public void SetDefault(string id, bool communications)
    {
        PolicyConfig.SetDefault(id, (int)Role.Console);
        PolicyConfig.SetDefault(id, (int)Role.Multimedia);
        if (communications) PolicyConfig.SetDefault(id, (int)Role.Communications);
    }

    public (string Title, string? ExePath) Describe(AudioSession s)
    {
        if (titles.TryGetValue(s.App, out var t)) return t;
        var exe = ProcessInfo.ExePath(s.Pid);
        string? title = null;
        try { if (exe is not null) title = FileVersionInfo.GetVersionInfo(exe).FileDescription; }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return titles[s.App] = (string.IsNullOrWhiteSpace(title) ? Title(s.App) : title.Trim(), exe);
    }

    static readonly Dictionary<string, string> Known = new()
    {
        ["msedge"] = "Microsoft Edge", ["chrome"] = "Google Chrome", ["browser"] = "Yandex Browser", ["vlc"] = "VLC",
        ["aimp"] = "AIMP", ["foobar2000"] = "foobar2000", ["telegram"] = "Telegram", ["dota2"] = "Dota 2",
    };

    /// <summary>A readable name for an app that is not running: the rule editor shows "VLC", not "vlc".</summary>
    public static string Title(string app) => Known.TryGetValue(app, out var t) ? t : app.Length == 0 ? app : char.ToUpperInvariant(app[0]) + app[1..];

    public void Dispose()
    {
        foreach (var c in sessions.Values) c.Dispose();
        foreach (var (d, _) in devices.Values) d.Dispose();
        sessions.Clear();
        devices.Clear();
        enumerator.Dispose();
    }
}
