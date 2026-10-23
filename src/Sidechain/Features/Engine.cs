using System.Collections.Concurrent;
using System.Diagnostics;
using Sidechain.Features.Audio;
using Sidechain.Features.Core;
using Sidechain.Shell;

namespace Sidechain.Features;

public enum EngineState { Idle, Ducking, Paused, Error }
public enum AppRole { None, Trigger, Target }

/// <summary>An app on the Now page: all its sessions on all devices together. LevelerDb is null when the app is not leveled.</summary>
/// v1.1: BoostDb is how much louder than the user's own volume it plays now, BoostRoomDb how much more is possible,
/// BoostLimited that more was asked than the device master allows.
public sealed record AppRow(string App, string Title, string? ExePath, float Peak, float Baseline, double GainDb, AppRole Role, string Devices, int Sessions,
    double? LevelerDb = null, double? LevelMedianDb = null, double BoostDb = 0, double BoostRoomDb = 0, bool BoostLimited = false);

/// <summary>"21:14 DELL U2723QE took over → back to Headphones".</summary>
public sealed record PinEvent(DateTime At, AudioDevice From, AudioDevice To);

/// <summary>What the UI and the tray see; immutable, replaced every tick.</summary>
public sealed record Snapshot(
    EngineState State, DateTime? PausedUntil, IReadOnlyList<AppRow> Apps, IReadOnlyList<string> Speaking,
    IReadOnlyList<AudioDevice> Devices, string? DefaultId, IReadOnlyList<PinEvent> PinLog, PinEvent? LastAuto, bool PinBroken,
    IReadOnlyList<AudioDevice> Mics, string? MicDefaultId)
{
    public static readonly Snapshot Empty = new(EngineState.Idle, null, [], [], [], null, [], null, false, [], null);
}

/// <summary>
/// The audio thread (MTA, all COM calls happen here): commands → peaks of triggers → Ducker.Step and the leveler →
/// volumes; sessions once a second (4× while ducking), devices once a second. Every change starts from the user's own
/// volume, which the journal keeps until it is back. A COM error drops the backend and makes a new one after 2 / 5 / 10 s.
/// v1.1 boost: a session can't play above 100 %, so for an app that should be louder the device master goes up by what is
/// missing and every other session on that device (system sounds too) goes down by as much: only that app gets louder.
/// The room ends where the master reaches the top of its range.
/// </summary>
public sealed class Engine
{
    const float ManualEpsilon = 0.005f;
    const double WriteStepDb = 0.25, MeterRetryMs = 5000, MasterEpsilonDb = 0.3;
    public const int MaxLeveled = 3;                            // one capture thread each
    public const double MaxBoostDb = 20;
    const string MasterApp = "#master";                         // journal entries of a device master we raised

    sealed class Tracked(AudioSession s, float baseline)
    {
        public AudioSession S = s;
        public float Baseline = baseline;   // the user's volume
        public float? Written;              // what we wrote last; null = untouched
    }

    sealed class Leveled
    {
        public readonly Leveler Leveler = new();
        public ILevel? Meter;
        public double TriedAt = double.NegativeInfinity;
    }

    /// <summary>v1.1: a device master we raised for a boost; Baseline is the user's own master level.</summary>
    sealed class Master(MasterVolume v)
    {
        public float BaselineDb = v.Db;
        public readonly float MinDb = v.MinDb, MaxDb = v.MaxDb;
        public float? Written;
        public double Lift;                 // last asked lift, to know when it stopped moving
    }

    /// <summary>Default device watch of one direction: output, or the microphone (v1.0).</summary>
    sealed class PinWatch(bool capture)
    {
        public readonly bool Capture = capture;
        public readonly PinPolicy Policy = new();
        public IReadOnlyList<AudioDevice> Devices = [];
        public readonly Dictionary<string, double> ActiveSince = [];
        public string? DefaultId;
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

    // Raised on the audio thread; the UI marshals them. The bool is true for the microphone.
    public event Action<PinEvent>? AutoSwitched;
    public event Action<bool>? PinWar;
    public event Action? PinFailed;
    public event Action<AudioDevice, bool>? FirstDefault;

    // Audio thread state
    IAudioBackend? backend;
    List<Rule> rules = [];
    List<LevelerEntry> leveler = [];
    List<BoostEntry> boosts = [];
    readonly Dictionary<string, Master> masters = [];
    readonly Dictionary<string, MasterVolume?> masterInfo = [];  // output device → its master, read once a second for the room
    PinSettings pin = new();
    readonly Ducker ducker = new();
    readonly Dictionary<string, Leveled> leveled = [];
    readonly PinWatch output = new(false), mic = new(true);
    readonly Dictionary<string, Tracked> tracked = [];
    readonly HashSet<(string App, string Device)> orphans = [];  // changed sessions that went away; Windows keeps their volume
    IReadOnlyList<AudioSession> sessions = [];
    readonly Dictionary<string, float> peaks = [];
    readonly Dictionary<string, double> loudAt = [];             // trigger app → last time above a threshold, for "who talks"
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

    public void Start(List<Rule> rules, PinSettings pin, List<LevelerEntry> leveler, List<BoostEntry> boosts)
    {
        SetRules(rules);
        SetPin(pin);
        SetLeveler(leveler);
        SetBoosts(boosts);
        thread = new Thread(Run) { IsBackground = true, Name = "audio" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Exit, Windows session end, crash: every volume back at once, then the thread ends (2 s at most).</summary>
    public void Stop()
    {
        if (thread is null) return;
        stopping = true; // the audio thread restores after its last tick: a restore queued as a command could run inside a tick
        wake.Set();       // that then ducks or boosts again before the thread notices it has to stop
        if (!thread.Join(2000)) Log.Warn("audio thread did not stop in 2 s");
        thread = null;
    }

    void Run()
    {
        while (!stopping)
        {
            Tick(clock());
            var idle = !WindowVisible && !ducker.Ducking && !paused && leveled.Count == 0 && boosts.Count == 0 && masters.Count == 0 &&
                       !sessions.Any(s => rules.Any(r => r.Triggers.Contains(s.App)));
            wake.WaitOne(idle || suspended || backend is null ? 1000 : 25);
        }
        try
        {
            while (commands.TryDequeue(out var c)) c();
            RestoreAll();
        }
        catch (Exception e) { Log.Error("restore on exit failed", e); }
        StopMeters();
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
        Post(() => { pin = copy; if (copy.Enabled || copy.Mic.Enabled) pinBroken = false; });
    }

    public void SetLeveler(List<LevelerEntry> list)
    {
        var copy = list.Take(MaxLeveled).ToList();
        Post(() => leveler = copy);
    }

    /// <summary>v1.1: manual boosts, 0–20 dB each.</summary>
    public void SetBoosts(List<BoostEntry> list)
    {
        var copy = list.Where(b => b.Db > 0).Select(b => b with { Db = Math.Min(b.Db, MaxBoostDb) }).ToList();
        Post(() => boosts = copy);
    }

    /// <summary>Until a time, or until Resume when null.</summary>
    public void Pause(DateTime? until) => Post(() => { paused = true; pausedUntil = until; RestoreAll(); });
    public void Resume() => Post(() => { paused = false; pausedUntil = null; });
    public void RestoreAllVolumes() => Post(RestoreAll);
    public void Suspend() => Post(() => { RestoreAll(); suspended = true; resumeAt = double.MaxValue; });
    public void Resumed() => Post(() => resumeAt = clock() + 3000);

    /// <summary>The user picked a device (tray menu, "Keep as it was"): never contested.</summary>
    public void SetDefault(string id) => Post(() => SetDefaultChecked(output, id));
    public void UndoLastAuto() => Post(() =>
    {
        if (lastAuto is not { } e) return;
        SetDefaultChecked(mic.Devices.Any(d => d.Id == e.From.Id) ? mic : output, e.From.Id);
        lastAuto = null;
    });
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
                foreach (var w in new[] { output, mic })
                    foreach (var id in w.ActiveSince.Keys.ToList()) w.ActiveSince[id] = now; // a monitor waking up counts as plugged in
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
            Level(now, dt);
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
        nextEnumerate = now + (ducker.Ducking || masters.Count > 0 ? 250 : 1000); // a new app on a lifted device must go down fast
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
            if (s.App != AudioSession.SystemSounds && (WindowVisible || triggers.Contains(s.App))) peaks[s.Key] = backend!.Peak(s);
    }

    HashSet<string> Running() => sessions.Select(s => s.App).Where(a => a != AudioSession.SystemSounds).ToHashSet();

    List<RuleInput> Inputs(double now)
    {
        var running = Running();
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
            // Boosted: only the triggers that sound now, so a Telegram ping during a Discord call does not get louder too
            var sounding = r.Triggers.Where(a => running.Contains(a) && loudAt.TryGetValue(a, out var t) && now - t <= r.HoldMs).ToList();
            input.Add(new RuleInput(r, peak, Rules.Targets(r, running, triggers), sounding));
        }
        return input;
    }

    /// <summary>
    /// v1.0 leveler: the level of each leveled app's process tree, measured after the session volume
    /// (AudioFacts.LoopbackIsPostVolume), so our own gain and the user's volume are taken back out first.
    /// </summary>
    void Level(double now, double dt)
    {
        foreach (var app in leveled.Keys.Where(a => leveler.All(e => e.App != a)).ToList())
        {
            leveled[app].Meter?.Dispose();
            leveled.Remove(app);
        }
        foreach (var e in leveler)
        {
            var l = leveled.TryGetValue(e.App, out var x) ? x : leveled[e.App] = new Leveled();
            var s = sessions.Where(s => s.App == e.App).MinBy(s => s.Key, StringComparer.Ordinal);
            if (s is null) { l.Meter?.Dispose(); l.Meter = null; continue; }
            if (l.Meter?.Pid != s.Pid && now - l.TriedAt >= MeterRetryMs) // new app, or its audio process started again
            {
                l.Meter?.Dispose();
                l.Meter = backend!.StartLevel(s.Pid);
                l.TriedAt = now;
            }
            // Measured after the session volume and before the master (AudioFacts): take out what the session plays at now
            var sessionDb = tracked.TryGetValue(s.Key, out var t) ? Db.FromScalar(t.Written ?? t.Baseline) : 0;
            l.Leveler.Step(now, dt, (l.Meter?.LevelDb ?? double.NaN) - sessionDb, e.TargetDb, e.MaxBoostDb);
        }
    }

    /// <summary>What the user hears for an app against their own volume: ducking, leveling, boosts; never below the floor.</summary>
    double Gain(string app)
    {
        if (paused || app == AudioSession.SystemSounds) return 0;
        var g = ducker.Gain(app) + (leveled.TryGetValue(app, out var l) ? l.Leveler.GainDb : 0) + (boosts.FirstOrDefault(b => b.App == app)?.Db ?? 0);
        return Math.Max(g, Db.Floor);
    }

    double Lift(string device) => masters.TryGetValue(device, out var m) && m.Written is { } w ? w - m.BaselineDb : 0;

    void Apply()
    {
        // The user's hand first, before any write: sessions of one app may share their volume in Windows
        foreach (var t in tracked.Values)
        {
            if (t.Written is not { } w) continue;
            var v = backend!.GetVolume(t.S);
            if (Math.Abs(v - w) <= ManualEpsilon) continue;
            Log.Info($"{(t.S.App == AudioSession.SystemSounds ? "system sounds" : t.S.App)}: volume changed by hand, new baseline");
            t.Baseline = v;
            t.Written = null;
            ducker.Cancel(t.S.App);
            if (leveled.TryGetValue(t.S.App, out var l)) l.Leveler.Reset();
        }
        // ...and on the master: a move by hand shifts the user's own master level by as much, so it is never fought
        foreach (var (dev, m) in masters)
        {
            if (m.Written is not { } w || backend!.Master(dev) is not { } now || Math.Abs(now.Db - w) <= MasterEpsilonDb) continue;
            Log.Info("master volume moved by hand, the boost keeps its room");
            m.BaselineDb = Math.Clamp(m.BaselineDb + (now.Db - w), m.MinDb, m.MaxDb);
            m.Written = now.Db;
        }

        // How far each master goes up: the most any app on that device needs beyond 100 % of its session, within the room
        var lift = new Dictionary<string, double>();
        foreach (var t in tracked.Values)
        {
            if (t.S.App == AudioSession.SystemSounds) continue;
            var over = Gain(t.S.App) + Db.FromScalar(t.Baseline);
            if (over <= 0.01) continue;
            if (!masters.TryGetValue(t.S.DeviceId, out var m))
            {
                if (backend!.Master(t.S.DeviceId) is not { } mv || mv.MaxDb - mv.Db < 0.1) continue; // no master or at 100 %: no room
                masters[t.S.DeviceId] = m = new Master(mv);
            }
            lift[t.S.DeviceId] = Math.Max(lift.GetValueOrDefault(t.S.DeviceId), Math.Min(over, m.MaxDb - m.BaselineDb));
        }
        // Others go down before a master goes up, and up after it goes down: never a loud moment in between
        var rising = masters.Keys.Where(d => lift.GetValueOrDefault(d) > Lift(d)).ToList();
        foreach (var d in masters.Keys.Except(rising).ToList()) WriteMaster(d, lift.GetValueOrDefault(d));
        foreach (var t in tracked.Values)
        {
            var e = lift.GetValueOrDefault(t.S.DeviceId);
            var g = Gain(t.S.App) - e;
            var steady = ducker.Settled(t.S.App) && !leveled.ContainsKey(t.S.App) && (!masters.TryGetValue(t.S.DeviceId, out var m) || m.Lift == e);
            if (t.Written is not { } w)
            {
                if (Math.Abs(g) < 0.01) continue;
                t.Baseline = backend!.GetVolume(t.S); // fresh: the user may have moved it since the last enumeration
                Write(t, Db.Apply(t.Baseline, g));
                continue;
            }
            if (Math.Abs(g) < 0.01) { Write(t, t.Baseline); t.Written = null; continue; }
            var want = Db.Apply(t.Baseline, g);
            if (want != w && (Math.Abs(Db.FromScalar(want) - Db.FromScalar(w)) >= WriteStepDb || steady)) Write(t, want);
        }
        foreach (var d in rising) WriteMaster(d, lift[d]);
        SyncJournal();
    }

    /// <summary>The master at the user's level plus the lift; at no lift back exactly to the user's level and forgotten.</summary>
    void WriteMaster(string device, double lift)
    {
        var m = masters[device];
        if (lift <= 0.01)
        {
            if (m.Written is not null) backend!.SetMaster(device, m.BaselineDb);
            masters.Remove(device);
            return;
        }
        var target = (float)(m.BaselineDb + lift);
        var steady = m.Lift == lift;
        m.Lift = lift;
        if (m.Written is { } w && (Math.Abs(target - w) < WriteStepDb && !steady || target == w)) return;
        backend!.SetMaster(device, target);
        m.Written = target;
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
        foreach (var (dev, m) in masters)
        {
            if (m.Written is null) continue;
            keep.Add((MasterApp, dev));
            journal.Set(MasterApp, dev, m.BaselineDb, DateTime.Now); // the user's master level, in dB; follows a move by hand
        }
        foreach (var e in journal.Entries.ToList())
            if (!keep.Contains((e.App, e.DeviceId))) journal.Remove(e.App, e.DeviceId);
    }

    /// <summary>Every changed session back to its baseline at once, no ramp.</summary>
    void RestoreAll()
    {
        ducker.Reset();
        foreach (var l in leveled.Values) l.Leveler.Reset();
        if (backend is null) return; // nothing tracked without a backend; the journal keeps what is left
        foreach (var (dev, m) in masters.ToList()) // the master down first, the others up after: quieter for a moment, never louder
        {
            try { if (m.Written is not null) backend.SetMaster(dev, m.BaselineDb); masters.Remove(dev); }
            catch (Exception e) { Log.Warn("master not restored now, kept in the journal", e); orphans.Add((MasterApp, dev)); masters.Remove(dev); }
        }
        foreach (var t in tracked.Values)
        {
            if (t.Written is null) continue;
            try { backend.SetVolume(t.S, t.Baseline); t.Written = null; }
            catch (Exception e) { Log.Warn($"{t.S.App}: not restored now, kept in the journal", e); orphans.Add((t.S.App, t.S.DeviceId)); tracked.Remove(t.S.Key); }
        }
        SyncJournal();
        Log.Info("all volumes restored");
    }

    void StopMeters()
    {
        foreach (var l in leveled.Values)
        {
            try { l.Meter?.Dispose(); } catch { }
            l.Meter = null;
        }
    }

    void Fail(Exception e, double now)
    {
        Log.Warn("audio error, the backend starts again", e);
        foreach (var t in tracked.Values) if (t.Written is not null) orphans.Add((t.S.App, t.S.DeviceId)); // the journal keeps them
        foreach (var (dev, m) in masters) if (m.Written is not null) orphans.Add((MasterApp, dev));
        masters.Clear();
        tracked.Clear();
        sessions = [];
        ducker.Reset();
        StopMeters();
        leveled.Clear();
        try { backend?.Dispose(); } catch { }
        backend = null;
        error = true;
        retryAt = now + new[] { 2000, 5000, 10000 }[Math.Min(failures++, 2)];
    }

    // ---- devices ----

    void PollDevices(double now)
    {
        Poll(output, now, pin.Enabled, pin.Priority, pin.Seeded, () => pin.Seeded = true, () => pin.Enabled = false);
        Poll(mic, now, pin.Mic.Enabled, pin.Mic.Priority, pin.Mic.Seeded, () => pin.Mic.Seeded = true, () => pin.Mic.Enabled = false);
        AdoptMasters(); // after the device list is read: a master is looked up among the active devices
        masterInfo.Clear();
        foreach (var d in output.Devices) masterInfo[d.Id] = backend!.Master(d.Id);
        nextDevices = now + 1000;
    }

    /// <summary>
    /// A master we raised before a crash or a kill: still higher than the user's level means it is ours, back it goes (before
    /// the other sessions come back up, so it is quieter for a moment, never louder). Lower: the user set it since, keep theirs.
    /// </summary>
    void AdoptMasters()
    {
        foreach (var e in journal.Entries.Where(e => e.App == MasterApp && !masters.ContainsKey(e.DeviceId)).ToList())
        {
            if (backend!.Master(e.DeviceId) is not { } mv) continue; // the device is not here now: keep the entry
            if (mv.Db > e.Baseline + 0.1)
            {
                Log.Info($"master found {mv.Db - e.Baseline:0.0} dB above the user's level after a restart, put back");
                backend.SetMaster(e.DeviceId, e.Baseline);
            }
            orphans.Remove((MasterApp, e.DeviceId));
            journal.Remove(MasterApp, e.DeviceId);
        }
    }

    void Poll(PinWatch w, double now, bool enabled, List<AudioDevice> priority, bool seeded, Action seed, Action off)
    {
        w.Devices = backend!.Devices(w.Capture);
        foreach (var d in w.Devices) w.ActiveSince.TryAdd(d.Id, w.DefaultId is null ? double.NegativeInfinity : now); // first poll: long active
        foreach (var id in w.ActiveSince.Keys.Where(id => w.Devices.All(d => d.Id != id)).ToList()) w.ActiveSince.Remove(id);
        var current = backend.DefaultId(w.Capture);
        if (current is null) return;
        if (priority.Count == 0 && !seeded && w.Devices.FirstOrDefault(d => d.Id == current) is { } first)
        {
            seed();
            FirstDefault?.Invoke(first, w.Capture);
        }
        var previous = w.DefaultId;
        w.DefaultId = current;
        if (previous is null || previous == current || !enabled || pinBroken) return;

        var (verdict, to) = w.Policy.OnDefaultChanged(current, w.ActiveSince.GetValueOrDefault(current, double.NegativeInfinity), now, priority, w.Devices);
        if (verdict == PinVerdict.War)
        {
            Log.Warn($"{(w.Capture ? "microphone" : "device")} pinning off: another program keeps changing the default device");
            off();
            PinWar?.Invoke(w.Capture);
        }
        if (verdict != PinVerdict.SwitchBack || to is null) return;
        var from = w.Devices.First(d => d.Id == current);
        if (!SetDefaultChecked(w, to.Id)) return;
        var ev = new PinEvent(DateTime.Now, from, to);
        Log.Info($"default {(w.Capture ? "microphone" : "device")}: a new device took over, switched back ({Short(from.Id)} → {Short(to.Id)})"); // ids only: names may carry a person's name
        pinLog.Insert(0, ev);
        if (pinLog.Count > 10) pinLog.RemoveAt(10);
        lastAuto = ev;
        AutoSwitched?.Invoke(ev);
    }

    static string Short(string id) => id.Length > 8 ? id[^9..^1] : id;

    /// <summary>IPolicyConfig, then the default is read back: unchanged means an update broke it, pinning goes off.</summary>
    bool SetDefaultChecked(PinWatch w, string id)
    {
        if (backend is null) return false;
        try { backend.SetDefault(id, pin.AlsoCommunications); }
        catch (Exception e) { Log.Warn("SetDefaultEndpoint failed", e); }
        w.DefaultId = backend.DefaultId(w.Capture);
        if (w.DefaultId == id) return true;
        Log.Warn("the default device did not change: pinning off");
        pinBroken = true;
        PinFailed?.Invoke();
        return false;
    }

    // ---- snapshot ----

    void Publish()
    {
        var triggers = Rules.AllTriggers(rules);
        var running = Running();
        var targets = rules.SelectMany(r => Rules.Targets(r, running, triggers)).ToHashSet();
        var names = output.Devices.ToDictionary(d => d.Id, d => d.Name);
        var apps = new List<AppRow>();
        foreach (var g in sessions.Where(s => s.App != AudioSession.SystemSounds).GroupBy(s => s.App))
        {
            var first = g.First();
            var (title, exe) = backend?.Describe(first) ?? (g.Key, null);
            var l = leveled.GetValueOrDefault(g.Key);
            var baseline = tracked.TryGetValue(first.Key, out var t) ? t.Baseline : 1;
            // Room above the user's volume: what the session has left to 100 %, plus what the master has left to the top
            var master = masters.TryGetValue(first.DeviceId, out var m) ? m.MaxDb - m.BaselineDb
                       : masterInfo.GetValueOrDefault(first.DeviceId) is { } mv ? mv.MaxDb - mv.Db : 0;
            var room = Math.Max(0, -Db.FromScalar(baseline)) + Math.Max(0, master);
            var gain = Gain(g.Key);
            var boost = Math.Min(Math.Max(0, gain), room);
            apps.Add(new AppRow(g.Key, title, exe,
                g.Select(s => peaks.GetValueOrDefault(s.Key)).Max(),
                baseline,
                Math.Min(gain, room),
                triggers.Contains(g.Key) ? AppRole.Trigger : targets.Contains(g.Key) ? AppRole.Target : AppRole.None,
                string.Join(", ", g.Select(s => names.GetValueOrDefault(s.DeviceId, "?")).Distinct()),
                g.Count(),
                l?.Leveler.GainDb, l?.Leveler.Median(),
                boost, room - boost, gain > room + 0.1));
        }
        var speaking = rules.Where(r => ducker.ActiveRules.Contains(r.Id))
            .SelectMany(r => r.Triggers.Where(a => loudAt.TryGetValue(a, out var t) && lastTick - t <= r.HoldMs))
            .Distinct().Select(a => apps.FirstOrDefault(x => x.App == a)?.Title).OfType<string>().ToList();
        var state = error ? EngineState.Error : paused ? EngineState.Paused : ducker.Ducking ? EngineState.Ducking : EngineState.Idle;
        Volatile.Write(ref snapshot, new Snapshot(state, pausedUntil, apps, speaking, output.Devices, output.DefaultId, [.. pinLog], lastAuto, pinBroken,
            mic.Devices, mic.DefaultId));
    }
}
