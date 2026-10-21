using System.Collections.Concurrent;
using System.Diagnostics;
using Sidechain.Features.Audio;
using Sidechain.Features.Core;
using Sidechain.Shell;

namespace Sidechain.Features;

public enum EngineState { Idle, Ducking, Paused, Error }
public enum AppRole { None, Trigger, Target }

/// <summary>An app on the Now page: all its sessions on all devices together.</summary>
public sealed record AppRow(string App, string Title, string? ExePath, float Peak, float Baseline, double GainDb, AppRole Role, string Devices, int Sessions);

/// <summary>"21:14 DELL U2723QE took over → back to Headphones".</summary>
public sealed record PinEvent(DateTime At, AudioDevice From, AudioDevice To);

/// <summary>What the UI and the tray see; immutable, replaced every tick.</summary>
public sealed record Snapshot(
    EngineState State, DateTime? PausedUntil, IReadOnlyList<AppRow> Apps, IReadOnlyList<string> Speaking,
    IReadOnlyList<AudioDevice> Devices, string? DefaultId, IReadOnlyList<PinEvent> PinLog, PinEvent? LastAuto, bool PinBroken)
{
    public static readonly Snapshot Empty = new(EngineState.Idle, null, [], [], [], null, [], null, false);
}

/// <summary>
/// The audio thread (MTA, all COM calls happen here): commands → peaks of triggers → Ducker.Step → volumes; sessions
/// once a second (4× while ducking), devices once a second. Every change starts from the user's own volume, which the
/// journal keeps until it is back. A COM error drops the backend and makes a new one after 2 / 5 / 10 s.
/// </summary>
public sealed class Engine
{
    const float ManualEpsilon = 0.005f;
    const double WriteStepDb = 0.25;

    sealed class Tracked(AudioSession s, float baseline)
    {
        public AudioSession S = s;
        public float Baseline = baseline;   // the user's volume
        public float? Written;              // what we wrote last; null = untouched
    }

    readonly Func<IAudioBackend> create;
    readonly RestoreJournal journal;
    readonly Func<double> clock;
    readonly ConcurrentQueue<Action> commands = new();
    readonly AutoResetEvent wake = new(false);
    Thread? thread;
    volatile bool stopping;
    Snapshot snapshot = Snapshot.Empty;

    public static Engine? Current { get; set; }
    public Snapshot Snapshot => Volatile.Read(ref snapshot);
    /// <summary>The window is on screen: read the peaks of all apps, not only of triggers.</summary>
    public volatile bool WindowVisible;

    // Raised on the audio thread; the UI marshals them
    public event Action<PinEvent>? AutoSwitched;
    public event Action? PinWar, PinFailed;
    public event Action<AudioDevice>? FirstDefault;

    // Audio thread state
    IAudioBackend? backend;
    List<Rule> rules = [];
    PinSettings pin = new();
    readonly Ducker ducker = new();
    readonly PinPolicy pinPolicy = new();
    readonly Dictionary<string, Tracked> tracked = [];
    readonly HashSet<(string App, string Device)> orphans = [];  // changed sessions that went away; Windows keeps their volume
    IReadOnlyList<AudioSession> sessions = [];
    readonly Dictionary<string, float> peaks = [];
    readonly Dictionary<string, double> loudAt = [];             // trigger app → last time above a threshold, for "who talks"
    IReadOnlyList<AudioDevice> devices = [];
    readonly Dictionary<string, double> activeSince = [];
    string? defaultId;
    readonly List<PinEvent> pinLog = [];
    PinEvent? lastAuto;
    bool pinBroken, firstRefresh = true, error, suspended, paused;
    DateTime? pausedUntil;
    double lastTick = double.NaN, nextEnumerate, nextDevices, resumeAt, retryAt;
    int failures;

    public Engine(Func<IAudioBackend> create, RestoreJournal journal, Func<double>? clock = null)
    {
        this.create = create;
        this.journal = journal;
        var sw = Stopwatch.StartNew();
        this.clock = clock ?? (() => sw.Elapsed.TotalMilliseconds);
        orphans.UnionWith(journal.Entries.Select(e => (e.App, e.DeviceId))); // until a session of theirs shows up
    }

    /// <summary>Runs on the audio thread before the next tick.</summary>
    public void Post(Action a)
    {
        commands.Enqueue(a);
        wake.Set();
    }

    public void Start(List<Rule> rules, PinSettings pin)
    {
        SetRules(rules);
        SetPin(pin);
        thread = new Thread(Run) { IsBackground = true, Name = "audio" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Exit, Windows session end, crash: every volume back at once, then the thread ends (2 s at most).</summary>
    public void Stop()
    {
        if (thread is null) return;
        Post(RestoreAll);
        stopping = true;
        wake.Set();
        if (!thread.Join(2000)) Log.Warn("audio thread did not stop in 2 s");
        thread = null;
    }

    void Run()
    {
        while (!stopping)
        {
            Tick(clock());
            var idle = !WindowVisible && !ducker.Ducking && !paused && !sessions.Any(s => rules.Any(r => r.Triggers.Contains(s.App)));
            wake.WaitOne(idle || suspended || backend is null ? 1000 : 25);
        }
        try { while (commands.TryDequeue(out var c)) c(); }
        catch (Exception e) { Log.Error("restore on exit failed", e); }
        try { backend?.Dispose(); } catch { }
    }

    // ---- commands (UI → audio thread) ----

    public void SetRules(List<Rule> list)
    {
        var copy = list.Select(r => r.Clone()).ToList();
        var bad = Rules.Validate(copy).Select(e => e.Rule).ToHashSet(); // hand-edited settings.json: a looping rule is skipped
        Post(() => rules = copy.Where((r, i) => r.Enabled && !bad.Contains(i)).ToList());
    }

    public void SetPin(PinSettings p)
    {
        var copy = p.Clone();
        Post(() => { pin = copy; if (copy.Enabled) pinBroken = false; });
    }

    /// <summary>Until a time, or until Resume when null.</summary>
    public void Pause(DateTime? until) => Post(() => { paused = true; pausedUntil = until; RestoreAll(); });
    public void Resume() => Post(() => { paused = false; pausedUntil = null; });
    public void RestoreAllVolumes() => Post(RestoreAll);
    public void Suspend() => Post(() => { RestoreAll(); suspended = true; resumeAt = double.MaxValue; });
    public void Resumed() => Post(() => resumeAt = clock() + 3000);

    /// <summary>The user picked a device (tray menu, "Keep as it was"): never contested.</summary>
    public void SetDefault(string id) => Post(() => SetDefaultChecked(id));
    public void UndoLastAuto() => Post(() => { if (lastAuto is { } e) { SetDefaultChecked(e.From.Id); lastAuto = null; } });
    /// <summary>--demo: a past automatic switch for the Devices page.</summary>
    public void SeedLog(PinEvent e) => Post(() => pinLog.Add(e));

    // ---- the tick ----

    internal void Tick(double now)
    {
        var dt = double.IsNaN(lastTick) ? 0 : Math.Clamp(now - lastTick, 0, 250);
        lastTick = now;
        try
        {
            while (commands.TryDequeue(out var c)) c();
            if (suspended)
            {
                if (now < resumeAt) return;
                suspended = false;
                foreach (var id in activeSince.Keys.ToList()) activeSince[id] = now; // a monitor waking up counts as plugged in
                nextEnumerate = nextDevices = 0;
            }
            if (backend is null)
            {
                if (now < retryAt) return;
                backend = create();
                nextEnumerate = nextDevices = 0;
            }
            if (paused && pausedUntil is { } until && DateTime.Now >= until) { paused = false; pausedUntil = null; }
            if (now >= nextDevices) PollDevices(now);
            if (now >= nextEnumerate) Refresh(now);
            ReadPeaks();
            ducker.Step(now, dt, paused ? [] : Inputs(now));
            Apply();
            error = false;
            failures = 0;
        }
        catch (Exception e) { Fail(e, now); }
        finally
        {
            try { Publish(); }
            catch (Exception e) { Log.Error("snapshot failed", e); }
        }
    }

    void Refresh(double now)
    {
        var list = backend!.Enumerate();
        var keys = list.Select(s => s.Key).ToHashSet();
        foreach (var (key, t) in tracked.ToList())
        {
            if (keys.Contains(key)) continue;
            if (t.Written is not null) orphans.Add((t.S.App, t.S.DeviceId));
            tracked.Remove(key);
        }
        foreach (var s in list)
        {
            if (tracked.TryGetValue(s.Key, out var t))
            {
                if (t.Written is null) t.Baseline = backend.GetVolume(s); // shown on the Now page
                continue;
            }
            tracked[s.Key] = Adopt(s);
        }
        sessions = list;
        firstRefresh = false;
        nextEnumerate = now + (ducker.Ducking ? 250 : 1000);
    }

    /// <summary>A session seen for the first time; the journal says if Windows gave it a volume we left behind.</summary>
    Tracked Adopt(AudioSession s)
    {
        var t = new Tracked(s, backend!.GetVolume(s));
        if (journal.Find(s.App, s.DeviceId) is not { } e) return t;
        var (baseline, restore) = RestoreJournal.Adopt(e.Baseline, t.Baseline);
        if (restore)
        {
            Log.Info($"{s.App}: found at {t.Baseline:0.00} after {(firstRefresh ? "a restart" : "a new stream")}, baseline {baseline:0.00}");
            t.Written = t.Baseline;
        }
        t.Baseline = baseline;
        orphans.Remove((s.App, s.DeviceId));
        return t;
    }

    void ReadPeaks()
    {
        peaks.Clear();
        var triggers = Rules.AllTriggers(rules);
        foreach (var s in sessions)
            if (WindowVisible || triggers.Contains(s.App)) peaks[s.Key] = backend!.Peak(s);
    }

    List<RuleInput> Inputs(double now)
    {
        var running = sessions.Select(s => s.App).ToHashSet();
        var triggers = Rules.AllTriggers(rules);
        var input = new List<RuleInput>();
        foreach (var r in rules)
        {
            float peak = 0;
            foreach (var s in sessions)
            {
                if (!r.Triggers.Contains(s.App)) continue;
                var p = peaks.GetValueOrDefault(s.Key);
                peak = Math.Max(peak, p);
                if (Db.FromScalar(p) >= r.ThresholdDb) loudAt[s.App] = now;
            }
            input.Add(new RuleInput(r, peak, Rules.Targets(r, running, triggers)));
        }
        return input;
    }

    void Apply()
    {
        // The user's hand first, before any write: sessions of one app may share their volume in Windows
        foreach (var t in tracked.Values)
        {
            if (t.Written is not { } w) continue;
            var v = backend!.GetVolume(t.S);
            if (Math.Abs(v - w) <= ManualEpsilon) continue;
            Log.Info($"{t.S.App}: volume changed by hand, new baseline");
            t.Baseline = v;
            t.Written = null;
            ducker.Cancel(t.S.App);
        }
        foreach (var t in tracked.Values)
        {
            var g = paused ? 0 : ducker.Gain(t.S.App);
            if (t.Written is not { } w)
            {
                if (g >= 0) continue;
                t.Baseline = backend!.GetVolume(t.S); // fresh: the user may have moved it since the last enumeration
                Write(t, Db.Apply(t.Baseline, g));
                continue;
            }
            if (g >= 0) { Write(t, t.Baseline); t.Written = null; continue; }
            var want = Db.Apply(t.Baseline, g);
            if (want != w && (Math.Abs(Db.FromScalar(want) - Db.FromScalar(w)) >= WriteStepDb || ducker.Settled(t.S.App))) Write(t, want);
        }
        SyncJournal();
    }

    void Write(Tracked t, float v)
    {
        backend!.SetVolume(t.S, v);
        t.Written = v;
    }

    /// <summary>An entry per (app, device) while any of its sessions is changed or was left changed; first baseline wins.</summary>
    void SyncJournal()
    {
        var keep = new HashSet<(string, string)>(orphans);
        foreach (var t in tracked.Values)
        {
            if (t.Written is null) continue;
            var key = (t.S.App, t.S.DeviceId);
            if (keep.Add(key) && journal.Find(t.S.App, t.S.DeviceId) is null) journal.Set(t.S.App, t.S.DeviceId, t.Baseline, DateTime.Now);
        }
        foreach (var e in journal.Entries.ToList())
            if (!keep.Contains((e.App, e.DeviceId))) journal.Remove(e.App, e.DeviceId);
    }

    /// <summary>Every changed session back to its baseline at once, no ramp.</summary>
    void RestoreAll()
    {
        ducker.Reset();
        if (backend is null) return; // nothing tracked without a backend; the journal keeps what is left
        foreach (var t in tracked.Values)
        {
            if (t.Written is null) continue;
            try { backend.SetVolume(t.S, t.Baseline); t.Written = null; }
            catch (Exception e) { Log.Warn($"{t.S.App}: not restored now, kept in the journal", e); orphans.Add((t.S.App, t.S.DeviceId)); tracked.Remove(t.S.Key); }
        }
        SyncJournal();
        Log.Info("all volumes restored");
    }

    void Fail(Exception e, double now)
    {
        Log.Warn("audio error, the backend starts again", e);
        foreach (var t in tracked.Values) if (t.Written is not null) orphans.Add((t.S.App, t.S.DeviceId)); // the journal keeps them
        tracked.Clear();
        sessions = [];
        ducker.Reset();
        try { backend?.Dispose(); } catch { }
        backend = null;
        error = true;
        retryAt = now + new[] { 2000, 5000, 10000 }[Math.Min(failures++, 2)];
    }

    // ---- devices ----

    void PollDevices(double now)
    {
        devices = backend!.Devices();
        foreach (var d in devices) activeSince.TryAdd(d.Id, defaultId is null ? double.NegativeInfinity : now); // first poll: long active
        foreach (var id in activeSince.Keys.Where(id => devices.All(d => d.Id != id)).ToList()) activeSince.Remove(id);
        var current = backend.DefaultId();
        nextDevices = now + 1000;
        if (current is null) return;
        if (pin.Priority.Count == 0 && !pin.Seeded && devices.FirstOrDefault(d => d.Id == current) is { } first)
        {
            pin.Seeded = true;
            FirstDefault?.Invoke(first);
        }
        var previous = defaultId;
        defaultId = current;
        if (previous is null || previous == current || !pin.Enabled || pinBroken) return;

        var (verdict, to) = pinPolicy.OnDefaultChanged(current, activeSince.GetValueOrDefault(current, double.NegativeInfinity), now, pin.Priority, devices);
        if (verdict == PinVerdict.War)
        {
            Log.Warn("device pinning off: another program keeps changing the default device");
            pin.Enabled = false;
            PinWar?.Invoke();
        }
        if (verdict != PinVerdict.SwitchBack || to is null) return;
        var from = devices.First(d => d.Id == current);
        if (!SetDefaultChecked(to.Id)) return;
        var ev = new PinEvent(DateTime.Now, from, to);
        Log.Info($"default device: a new device took over, switched back ({Short(from.Id)} → {Short(to.Id)})"); // ids only: names may carry a person's name
        pinLog.Insert(0, ev);
        if (pinLog.Count > 10) pinLog.RemoveAt(10);
        lastAuto = ev;
        AutoSwitched?.Invoke(ev);
    }

    static string Short(string id) => id.Length > 8 ? id[^9..^1] : id;

    /// <summary>IPolicyConfig, then the default is read back: unchanged means an update broke it, pinning goes off.</summary>
    bool SetDefaultChecked(string id)
    {
        if (backend is null) return false;
        try { backend!.SetDefault(id, pin.AlsoCommunications); }
        catch (Exception e) { Log.Warn("SetDefaultEndpoint failed", e); }
        defaultId = backend!.DefaultId();
        if (defaultId == id) return true;
        Log.Warn("the default device did not change: pinning off");
        pinBroken = true;
        PinFailed?.Invoke();
        return false;
    }

    // ---- snapshot ----

    void Publish()
    {
        var triggers = Rules.AllTriggers(rules);
        var running = sessions.Select(s => s.App).ToHashSet();
        var targets = rules.SelectMany(r => Rules.Targets(r, running, triggers)).ToHashSet();
        var names = devices.ToDictionary(d => d.Id, d => d.Name);
        var apps = new List<AppRow>();
        foreach (var g in sessions.GroupBy(s => s.App))
        {
            var first = g.First();
            var (title, exe) = backend?.Describe(first) ?? (g.Key, null);
            apps.Add(new AppRow(g.Key, title, exe,
                g.Select(s => peaks.GetValueOrDefault(s.Key)).Max(),
                tracked.TryGetValue(first.Key, out var t) ? t.Baseline : 1,
                ducker.Gain(g.Key),
                triggers.Contains(g.Key) ? AppRole.Trigger : targets.Contains(g.Key) ? AppRole.Target : AppRole.None,
                string.Join(", ", g.Select(s => names.GetValueOrDefault(s.DeviceId, "?")).Distinct()),
                g.Count()));
        }
        var speaking = rules.Where(r => ducker.ActiveRules.Contains(r.Id))
            .SelectMany(r => r.Triggers.Where(a => loudAt.TryGetValue(a, out var t) && lastTick - t <= r.HoldMs))
            .Distinct().Select(a => apps.FirstOrDefault(x => x.App == a)?.Title).OfType<string>().ToList();
        var state = error ? EngineState.Error : paused ? EngineState.Paused : ducker.Ducking ? EngineState.Ducking : EngineState.Idle;
        Volatile.Write(ref snapshot, new Snapshot(state, pausedUntil, apps, speaking, devices, defaultId, [.. pinLog], lastAuto, pinBroken));
    }
}
