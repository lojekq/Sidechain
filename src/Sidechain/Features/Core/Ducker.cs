namespace Sidechain.Features.Core;

public static class Db
{
    /// <summary>Lowest gain ever applied.</summary>
    public const double Floor = -40;

    public static double ToScalar(double db) => Math.Pow(10, db / 20);
    public static double FromScalar(double v) => 20 * Math.Log10(Math.Max(v, 1e-6));

    /// <summary>volume = baseline × 10^(gain/20), gain not below the floor, the session never above 100 %.
    /// A positive gain is a boost the user asked for; anything over 100 % goes through the device master (Engine).</summary>
    public static float Apply(float baseline, double gainDb) => (float)Math.Min(1, baseline * ToScalar(Math.Max(gainDb, Floor)));
}

/// <summary>One rule as the ducker sees it in a tick: the loudest trigger peak (linear), the apps it ducks now,
/// and its running triggers (boosted while the rule is active, v1.1).</summary>
public readonly record struct RuleInput(Rule Rule, float Peak, IReadOnlyCollection<string> Targets, IReadOnlyCollection<string>? Triggers = null);

/// <summary>
/// Gate with hold per rule and an envelope per target app, in dB (spec §5). Pure: time comes from the caller,
/// so the math uses the real dt (Thread.Sleep(25) really sleeps ~31 ms).
/// </summary>
public sealed class Ducker
{
    public sealed class Target
    {
        public double Db;                                  // current gain: < 0 ducked target, > 0 boosted trigger
        public double Want;                                // where it is heading
        public double DepthDb = 12, AttackMs = 80, ReleaseMs = 600; // of the rule that moved it away from 0 last
        public bool Canceled;                              // the user moved the volume: no ducking until the gate closes
    }

    readonly Dictionary<string, double> lastAbove = [];    // rule id → time the gate was last held open by the trigger
    readonly Dictionary<string, (double Since, double Last)> loud = []; // rule id → the current run of sound above the threshold
    const double RunGapMs = 200;                            // dips between syllables shorter than this don't end a run
    public Dictionary<string, Target> Targets { get; } = [];
    public HashSet<string> ActiveRules { get; } = [];

    public double Gain(string app) => Targets.TryGetValue(app, out var t) ? t.Db : 0;
    public bool Settled(string app) => !Targets.TryGetValue(app, out var t) || t.Db == t.Want;
    public bool Ducking => Targets.Values.Any(t => t.Db < 0);

    public void Step(double nowMs, double dtMs, IReadOnlyList<RuleInput> rules)
    {
        ActiveRules.Clear();
        var want = new Dictionary<string, (double Db, Rule R)>();
        foreach (var (r, peak, targets, triggers) in rules)
        {
            if (Db.FromScalar(peak) >= r.ThresholdDb)
            {
                var since = loud.TryGetValue(r.Id, out var run) && nowMs - run.Last <= RunGapMs ? run.Since : nowMs;
                loud[r.Id] = (since, nowMs);
                if (nowMs - since >= r.MinSignalMs || lastAbove.TryGetValue(r.Id, out var open) && nowMs - open <= r.HoldMs)
                    lastAbove[r.Id] = nowMs; // long enough, or the gate is open already
            }
            if (!lastAbove.TryGetValue(r.Id, out var t) || nowMs - t > r.HoldMs) continue;
            ActiveRules.Add(r.Id);
            foreach (var app in targets)
                if (!want.TryGetValue(app, out var w) || -r.DepthDb < w.Db) want[app] = (-r.DepthDb, r); // the deepest wins
            if (r.BoostTriggersDb > 0)
                foreach (var app in triggers ?? [])
                    if (!want.TryGetValue(app, out var w) || r.BoostTriggersDb > w.Db) want[app] = (r.BoostTriggersDb, r);
        }
        foreach (var id in lastAbove.Keys.Where(id => rules.All(r => r.Rule.Id != id)).ToList()) lastAbove.Remove(id);
        foreach (var id in loud.Keys.Where(id => rules.All(r => r.Rule.Id != id)).ToList()) loud.Remove(id);

        foreach (var app in want.Keys) Targets.TryAdd(app, new Target());
        foreach (var (app, t) in Targets.ToList())
        {
            var has = want.TryGetValue(app, out var w);
            if (!has) t.Canceled = false;
            t.Want = !has || t.Canceled ? 0 : w.Db;
            // Away from 0 (ducking deeper, boosting more) at the attack of the rule asking for it; back towards 0 at the release
            var away = Math.Abs(t.Want) > Math.Abs(t.Db) && (t.Db == 0 || Math.Sign(t.Want) == Math.Sign(t.Db));
            if (away) (t.DepthDb, t.AttackMs, t.ReleaseMs) = (Math.Abs(w.Db), w.R.AttackMs, w.R.ReleaseMs);
            var step = t.DepthDb / Math.Max(away ? t.AttackMs : t.ReleaseMs, 1) * dtMs;
            t.Db = t.Want > t.Db ? Math.Min(t.Want, t.Db + step) : Math.Max(t.Want, t.Db - step);
            t.Db = Math.Max(t.Db, Db.Floor);
            if (!has && t.Db == 0) Targets.Remove(app);
        }
    }

    /// <summary>Manual volume change wins: the app is back at 0 dB until its rules go quiet.</summary>
    public void Cancel(string app)
    {
        if (!Targets.TryGetValue(app, out var t)) return;
        t.Db = t.Want = 0;
        t.Canceled = true;
    }

    public void Reset()
    {
        Targets.Clear();
        lastAbove.Clear();
        loud.Clear();
        ActiveRules.Clear();
    }
}
