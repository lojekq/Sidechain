using System.IO;
using Sidechain.Features;
using Sidechain.Features.Audio;
using Sidechain.Features.Core;

namespace Sidechain.Tests;

/// <summary>Fake Core Audio: sessions with a peak and a volume, one device.</summary>
sealed class FakeBackend : IAudioBackend
{
    public readonly List<AudioSession> Sessions = [];
    public readonly Dictionary<string, float> Volume = [], PeakOf = [];
    public string? Default = "dev";

    public AudioSession Add(string app, float volume, float peak = 0)
    {
        var s = new AudioSession($"{app}#{Sessions.Count}", app, 100 + Sessions.Count, "dev");
        Sessions.Add(s);
        Volume[s.Key] = volume;
        PeakOf[s.Key] = peak;
        return s;
    }

    public IReadOnlyList<AudioSession> Enumerate() => [.. Sessions];
    public float Peak(AudioSession s) => PeakOf[s.Key];
    public float GetVolume(AudioSession s) => Volume[s.Key];
    public void SetVolume(AudioSession s, float v) => Volume[s.Key] = v;
    public readonly Dictionary<int, double> SourceDb = []; // pid → loudness before the volume, for the leveler

    public IReadOnlyList<AudioDevice> Devices(bool capture = false) => capture ? [] : [new("dev", "Headphones")];
    public string? DefaultId(bool capture = false) => capture ? null : Default;
    public void SetDefault(string id, bool communications) => Default = id;
    public (string Title, string? ExePath) Describe(AudioSession s) => (s.App, null);
    public ILevel? StartLevel(int pid) => new Level(this, pid);

    /// <summary>Like process loopback: what plays after the session volume.</summary>
    sealed class Level(FakeBackend b, int pid) : ILevel
    {
        public int Pid => pid;
        public double LevelDb => b.SourceDb.TryGetValue(pid, out var db) ? db + Db.FromScalar(b.Volume[b.Sessions.First(s => s.Pid == pid).Key]) : double.NaN;
        public void Dispose() { }
    }
    public void Dispose() { }
}

[TestClass]
public class SidechainTests
{
    public TestContext TestContext { get; set; } = null!;

    string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "Sidechain.Tests", TestContext.TestName + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static Rule Rule(string trigger, string target, double depth = 12) => new() { Triggers = [trigger], Targets = [target], DepthDb = depth };
    static RuleInput In(Rule r, float peak) => new(r, peak, r.Targets);

    // ---- Ducker ----

    [TestMethod]
    [DataRow(25.0)]
    [DataRow(31.0)]
    public void AttackAndReleaseTakeTheirTime(double dt)
    {
        var r = Rule("discord", "spotify");
        var d = new Ducker();
        double now = 0;
        var ticks = 0;
        do { d.Step(now += dt, dt, [In(r, 1)]); ticks++; } while (d.Gain("spotify") > -12);
        Assert.AreEqual(Math.Ceiling(r.AttackMs / dt), ticks, 1);

        ticks = 0;
        do { d.Step(now += dt, dt, [In(r, 0)]); ticks++; } while (d.Gain("spotify") < 0);
        Assert.AreEqual(Math.Ceiling((r.HoldMs + r.ReleaseMs) / dt), ticks, 1);
        Assert.IsEmpty(d.Targets);
    }

    [TestMethod]
    public void HoldBridgesShortPauses()
    {
        var r = Rule("discord", "spotify");
        var d = new Ducker();
        double now = 0;
        void Run(double ms, float peak) { for (var end = now + ms; now < end;) { now += 25; d.Step(now, 25, [In(r, peak)]); } }
        Run(200, 1);
        Run(300, 0); // a breath between words
        Assert.AreEqual(-12, d.Gain("spotify"));
        Run(200, 1);
        Assert.AreEqual(-12, d.Gain("spotify"));
    }

    [TestMethod]
    public void DeepestRuleWins()
    {
        var a = Rule("discord", "spotify", 6);
        var b = Rule("telegram", "spotify", 18);
        var d = new Ducker();
        for (var t = 25.0; t < 1000; t += 25) d.Step(t, 25, [In(a, 1), In(b, 1)]);
        Assert.AreEqual(-18, d.Gain("spotify"));
    }

    [TestMethod]
    public void SilenceDucksNothing()
    {
        var d = new Ducker();
        d.Step(25, 25, [In(Rule("discord", "spotify"), 0)]);
        Assert.AreEqual(0, d.Gain("spotify"));
        Assert.IsFalse(d.Ducking);
    }

    // ---- Rules ----

    [TestMethod]
    public void AllOthersLeavesTriggersOut()
    {
        var rules = new[] { new Rule { Triggers = ["discord"], Targets = [Rules.AllOthers] }, Rule("telegram", "vlc") };
        var targets = Rules.Targets(rules[0], ["discord", "spotify", "chrome", "telegram"], Rules.AllTriggers(rules));
        CollectionAssert.AreEquivalent(new[] { "spotify", "chrome" }, targets.ToList());
    }

    [TestMethod]
    public void TriggerTargetLoopIsRejected()
    {
        var errors = Rules.Validate([Rule("discord", "spotify"), Rule("spotify", "chrome")]);
        Assert.Contains(new RuleError(0, RuleProblem.TriggerIsTarget, "spotify", 2), errors);
        Assert.IsEmpty(Rules.Validate([Rule("discord", "spotify"), new Rule { Triggers = ["telegram"], Targets = [Rules.AllOthers] }]));
    }

    [TestMethod]
    [DataRow("Discord.EXE", "discord")]
    [DataRow(" Spotify ", "spotify")]
    [DataRow("foobar2000.exe", "foobar2000")]
    public void AppNamesAreLowerCaseWithoutExe(string name, string app) => Assert.AreEqual(app, Rules.AppName(name));

    // ---- Db ----

    [TestMethod]
    public void DbMath()
    {
        Assert.AreEqual(0.251, Db.ToScalar(-12), 0.001);
        Assert.AreEqual(-7.5, Db.FromScalar(Db.ToScalar(-7.5)), 1e-9);
        Assert.AreEqual(0.6f, Db.Apply(0.6f, +6), 1e-6f);          // never louder than the baseline
        Assert.AreEqual(0.6f * 0.01f, Db.Apply(0.6f, -60), 1e-6f); // floor −40 dB
    }

    // ---- Engine with a fake backend ----

    static (Engine Engine, FakeBackend Audio, Func<double, double> Run) Start(RestoreJournal journal, FakeBackend audio)
    {
        double now = 0;
        var e = new Engine(() => audio, journal, () => now);
        e.SetRules([Rule("discord", "spotify")]);
        e.SetPin(new PinSettings());
        return (e, audio, ms => { for (var end = now + ms; now < end;) { now += 31; e.Tick(now); } return now; });
    }

    [TestMethod]
    public void ManualChangeBecomesTheBaseline()
    {
        var journal = RestoreJournal.Load(Path.Combine(NewDir(), "restore.json"), DateTime.Now);
        var audio = new FakeBackend();
        var discord = audio.Add("discord", 1, peak: 0.5f);
        var spotify = audio.Add("spotify", 0.8f);
        var (_, _, run) = Start(journal, audio);
        run(500);
        Assert.AreEqual(0.8 * 0.251, audio.Volume[spotify.Key], 0.002);
        Assert.HasCount(1, journal.Entries);

        audio.Volume[spotify.Key] = 0.5f; // the user moves Spotify in the mixer while Discord talks
        run(500);
        Assert.AreEqual(0.5f, audio.Volume[spotify.Key]);
        audio.PeakOf[discord.Key] = 0;
        run(2000);
        Assert.AreEqual(0.5f, audio.Volume[spotify.Key]);
        Assert.IsEmpty(journal.Entries);
    }

    [TestMethod]
    public void KilledWhileDuckingIsRestoredOnNextStart()
    {
        var file = Path.Combine(NewDir(), "restore.json");
        var audio = new FakeBackend();
        audio.Add("discord", 1, peak: 0.5f);
        var spotify = audio.Add("spotify", 0.8f);
        Start(RestoreJournal.Load(file, DateTime.Now), audio).Run(500); // then killed: nothing restored

        var ducked = audio.Volume[spotify.Key];
        Assert.IsLessThan(0.3f, ducked);
        var after = new FakeBackend(); // Discord is gone, Spotify got the volume Windows remembered
        var again = after.Add("spotify", ducked);
        var journal = RestoreJournal.Load(file, DateTime.Now);
        Start(journal, after).Run(100);
        Assert.AreEqual(0.8f, after.Volume[again.Key]);
        Assert.IsEmpty(journal.Entries);
    }

    [TestMethod]
    public void RestoreAllPutsBaselinesBack()
    {
        var journal = RestoreJournal.Load(Path.Combine(NewDir(), "restore.json"), DateTime.Now);
        var audio = new FakeBackend();
        audio.Add("discord", 1, peak: 0.5f);
        var chrome = Enumerable.Range(0, 3).Select(_ => audio.Add("spotify", 0.6f)).ToList(); // several sessions, one app
        var (engine, _, run) = Start(journal, audio);
        run(500);
        Assert.IsTrue(chrome.All(s => audio.Volume[s.Key] < 0.2f));
        engine.Pause(null);
        run(31);
        Assert.IsTrue(chrome.All(s => audio.Volume[s.Key] == 0.6f));
        Assert.IsEmpty(journal.Entries);
        run(500); // paused: Discord still talks, nothing changes
        Assert.IsTrue(chrome.All(s => audio.Volume[s.Key] == 0.6f));
    }

    [TestMethod]
    public void ShortSoundsDontDuckWithMinSignal()
    {
        var r = Rule("discord", "spotify");
        r.MinSignalMs = 300;
        var d = new Ducker();
        double now = 0;
        void Run(double ms, float peak) { for (var end = now + ms; now < end;) { now += 25; d.Step(now, 25, [In(r, peak)]); } }
        Run(150, 1);   // a notification ping
        Run(1000, 0);
        Assert.AreEqual(0, d.Gain("spotify"));
        Run(250, 1);   // speech with a short dip
        Run(100, 0);
        Run(250, 1);
        Assert.AreEqual(-12, d.Gain("spotify"));
    }

    // ---- Leveler (v1.0) ----

    [TestMethod]
    public void LevelerTurnsDownSlowlyAndComesBackSlower()
    {
        var l = new Leveler();
        double now = 0;
        void Run(double s, double db) { for (var end = now + s * 1000; now < end;) { now += 25; l.Step(now, 25, db, -20); } }
        Run(1, -10);                       // an ad 10 dB over the norm: 6 dB/s down
        Assert.AreEqual(-6, l.GainDb, 0.01);
        Run(1, -10);
        Assert.AreEqual(-10, l.GainDb, 0.01);
        Run(2, double.NaN);                // a pause keeps the gain
        Assert.AreEqual(-10, l.GainDb, 0.01);
        Run(5, -20);                       // back to normal: 1 dB/s up
        Assert.AreEqual(-5, l.GainDb, 0.01);
        Run(3, 0);                         // far too loud: 12 dB at most
        Assert.AreEqual(-12, l.GainDb, 0.01);
    }

    [TestMethod]
    public void NormIsTheMedianOfThirtySeconds()
    {
        var l = new Leveler();
        for (var t = 0; t < 40_000; t += 100) l.Step(t, 100, t < 10_000 ? -5 : t % 3000 == 0 ? -60 : -22, -20); // first 10 s fall out, silence is skipped
        Assert.AreEqual(-22, l.Median());
    }

    [TestMethod]
    public void EngineLevelsByTheSourceNotByItsOwnGain()
    {
        var journal = RestoreJournal.Load(Path.Combine(NewDir(), "restore.json"), DateTime.Now);
        var audio = new FakeBackend();
        var chrome = audio.Add("chrome", 0.5f);
        audio.SourceDb[chrome.Pid] = -12;  // 8 dB over the −20 norm
        var (engine, _, run) = Start(journal, audio);
        engine.SetLeveler([new LevelerEntry("chrome", -20)]);
        run(4000);
        Assert.AreEqual(Db.FromScalar(0.5) - 8, Db.FromScalar(audio.Volume[chrome.Key]), 0.3); // not 8 dB more each round
        audio.SourceDb.Remove(chrome.Pid);
        run(1000);                          // silent: stays
        Assert.AreEqual(Db.FromScalar(0.5) - 8, Db.FromScalar(audio.Volume[chrome.Key]), 0.3);
        engine.Pause(null);
        run(100);
        Assert.AreEqual(0.5f, audio.Volume[chrome.Key]);
        Assert.IsEmpty(journal.Entries);
    }

    // ---- RestoreJournal ----

    [TestMethod]
    public void AdoptRestoresOnlyWhatIsQuieter()
    {
        Assert.AreEqual((0.8f, true), RestoreJournal.Adopt(0.8f, 0.2f));
        Assert.AreEqual((0.8f, false), RestoreJournal.Adopt(0.8f, 0.8f));
        Assert.AreEqual((0.9f, false), RestoreJournal.Adopt(0.8f, 0.9f));
    }

    [TestMethod]
    public void OldEntriesAreDropped()
    {
        var file = Path.Combine(NewDir(), "restore.json");
        var j = RestoreJournal.Load(file, DateTime.Now);
        j.Set("spotify", "dev", 0.8f, DateTime.Now.AddDays(-31));
        j.Set("chrome", "dev", 1f, DateTime.Now.AddDays(-1));
        Assert.AreEqual("chrome", RestoreJournal.Load(file, DateTime.Now).Entries.Single().App);
    }

    [TestMethod]
    public void WriteCutHalfwayKeepsTheJournal()
    {
        var file = Path.Combine(NewDir(), "restore.json");
        RestoreJournal.Load(file, DateTime.Now).Set("spotify", "dev", 0.8f, DateTime.Now);
        File.WriteAllText(file + ".tmp", "[{\"app\":\"spo"); // power cut while writing the next version
        Assert.AreEqual(0.8f, RestoreJournal.Load(file, DateTime.Now).Find("spotify", "dev")!.Baseline);
    }

    // ---- PinPolicy ----

    static readonly AudioDevice Phones = new("h", "Headphones"), Speakers = new("s", "Speakers"), Dell = new("d", "DELL U2723QE");

    [TestMethod]
    public void NewDeviceTakingOverIsSwitchedBack()
    {
        var (verdict, to) = new PinPolicy().OnDefaultChanged("d", activeSinceMs: 98_000, nowMs: 100_000, [Phones, Speakers], [Phones, Speakers, Dell]);
        Assert.AreEqual(PinVerdict.SwitchBack, verdict);
        Assert.AreEqual(Phones, to);
    }

    [TestMethod]
    public void UsersOwnChoiceIsKept() =>
        Assert.AreEqual(PinVerdict.Keep, new PinPolicy().OnDefaultChanged("s", activeSinceMs: 0, nowMs: 100_000, [Phones, Speakers], [Phones, Speakers]).Verdict);

    [TestMethod]
    public void FourthSwitchInAMinuteGivesUp()
    {
        var p = new PinPolicy();
        for (var i = 0; i < 3; i++)
            Assert.AreEqual(PinVerdict.SwitchBack, p.OnDefaultChanged("d", i * 10_000, i * 10_000 + 1, [Phones], [Phones, Dell]).Verdict);
        Assert.AreEqual(PinVerdict.War, p.OnDefaultChanged("d", 30_000, 30_001, [Phones], [Phones, Dell]).Verdict);
    }

    [TestMethod]
    public void BestDeviceByPriorityThenByName()
    {
        var newId = new AudioDevice("h2", "Headphones"); // driver reinstalled: same name, new id
        Assert.AreEqual(newId, PinPolicy.Best([new("h", "Headphones"), Speakers], [Speakers, newId]));
        Assert.AreEqual(Speakers, PinPolicy.Best([new("x", "Gone"), Speakers], [Phones, Speakers]));
        Assert.IsNull(PinPolicy.Best([Dell], [Phones]));
    }
}
