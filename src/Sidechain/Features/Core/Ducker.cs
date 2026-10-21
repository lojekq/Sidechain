namespace Sidechain.Features.Core;

public static class Db
{
    /// <summary>Lowest gain ever applied.</summary>
    public const double Floor = -40;

    public static double ToScalar(double db) => Math.Pow(10, db / 20);
    public static double FromScalar(double v) => 20 * Math.Log10(Math.Max(v, 1e-6));

    /// <summary>volume = baseline × 10^(gain/20) with gain clamped to [Floor, 0]: never louder than the user's own volume.</summary>
    public static float Apply(float baseline, double gainDb) => (float)(baseline * ToScalar(Math.Clamp(gainDb, Floor, 0)));
}

/// <summary>One rule as the ducker sees it in a tick: the loudest trigger peak (linear) and the apps it ducks now.</summary>
public readonly record struct RuleInput(Rule Rule, float Peak, IReadOnlyCollection<string> Targets);

/// <summary>
/// Gate with hold per rule and an envelope per target app, in dB (spec §5). Pure: time comes from the caller,
/// so the math uses the real dt (Thread.Sleep(25) really sleeps ~31 ms).
/// </summary>
public sealed class Ducker
{
    public sealed class Target
    {
        public double Db;                                  // current gain, ≤ 0
        public double Want;                                // where it is heading
        public double DepthDb = 12, AttackMs = 80, ReleaseMs = 600; // of the rule that ducked it last
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
        var want = new Dictionary<string, Rule>();
        foreach (var (r, peak, targets) in rules)
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
                if (!want.TryGetValue(app, out var w) || r.DepthDb > w.DepthDb) want[app] = r; // the deepest wins
        }
        foreach (var id in lastAbove.Keys.Where(id => rules.All(r => r.Rule.Id != id)).ToList()) lastAbove.Remove(id);
        foreach (var id in loud.Keys.Where(id => rules.All(r => r.Rule.Id != id)).ToList()) loud.Remove(id);

        foreach (var app in want.Keys) Targets.TryAdd(app, new Target());
        foreach (var (app, t) in Targets.ToList())
        {
            var r = want.GetValueOrDefault(app);
            if (r is null) t.Canceled = false;
            t.Want = r is null || t.Canceled ? 0 : -r.DepthDb;
            if (t.Want < t.Db)
            {
                (t.DepthDb, t.AttackMs, t.ReleaseMs) = (r!.DepthDb, r.AttackMs, r.ReleaseMs);
                t.Db = Math.Max(t.Want, t.Db - t.DepthDb / Math.Max(t.AttackMs, 1) * dtMs);
            }
            else if (t.Want > t.Db) t.Db = Math.Min(t.Want, t.Db + t.DepthDb / Math.Max(t.ReleaseMs, 1) * dtMs);
            t.Db = Math.Max(t.Db, Db.Floor);
            if (r is null && t.Db == 0) Targets.Remove(app);
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
