namespace Sidechain.Features.Core;

/// <summary>Triggers → targets. Stored in settings.json; apps are process names in lower case without ".exe".</summary>
public sealed class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public bool Enabled { get; set; } = true;
    public List<string> Triggers { get; set; } = [];
    public List<string> Targets { get; set; } = [];        // "*" = all other apps
    public double ThresholdDb { get; set; } = -45;
    public double DepthDb { get; set; } = 12;               // 3–30
    public double AttackMs { get; set; } = 80;
    public double HoldMs { get; set; } = 400;
    public double ReleaseMs { get; set; } = 600;
    public double MinSignalMs { get; set; }                  // v1.0: shorter sounds (a Discord notification) don't duck
    public double BoostTriggersDb { get; set; }              // v1.1: the triggers get louder while they sound, 0–12 dB

    public Rule Clone()
    {
        var c = (Rule)MemberwiseClone();
        c.Triggers = [.. Triggers];
        c.Targets = [.. Targets];
        return c;
    }
}

public enum RuleProblem { NoTriggers, NoTargets, TriggerIsTarget }

/// <summary>A validation error: <see cref="App"/> is a trigger in rule <see cref="OtherRule"/> (1-based) and a target here.</summary>
public readonly record struct RuleError(int Rule, RuleProblem Problem, string? App = null, int OtherRule = 0);

public static class Rules
{
    public const string AllOthers = "*";
    public const double MinDepth = 3, MaxDepth = 30;

    /// <summary>"Discord.EXE " → "discord".</summary>
    public static string AppName(string name)
    {
        name = name.Trim().ToLowerInvariant();
        return name.EndsWith(".exe") ? name[..^4] : name;
    }

    /// <summary>First start: calls duck music and browsers. Games are not ducked by default.</summary>
    public static List<Rule> Default() =>
    [
        new()
        {
            Triggers = ["discord", "telegram"],
            Targets = ["spotify", "chrome", "msedge", "firefox", "browser", "opera", "brave", "vlc", "aimp", "foobar2000"],
        },
    ];

    /// <summary>
    /// Enabled rules only. An app can't be a trigger in one rule and a target in another (or the same): ducking the trigger
    /// would lower its level and release the gate, a loop. "*" excludes every trigger by itself, so it never loops.
    /// </summary>
    public static List<RuleError> Validate(IReadOnlyList<Rule> rules)
    {
        var errors = new List<RuleError>();
        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (!r.Enabled) continue;
            if (r.Triggers.Count == 0) errors.Add(new(i, RuleProblem.NoTriggers));
            if (r.Targets.Count == 0) errors.Add(new(i, RuleProblem.NoTargets));
            foreach (var app in r.Targets)
                for (var j = 0; j < rules.Count; j++)
                    if (rules[j].Enabled && rules[j].Triggers.Contains(app))
                        errors.Add(new(i, RuleProblem.TriggerIsTarget, app, j + 1));
        }
        return errors;
    }

    /// <summary>Apps the rule ducks now: its targets that are running; "*" = every running app except all triggers.
    /// System sounds never get here: the backend leaves them out of <paramref name="running"/>.</summary>
    public static IReadOnlyCollection<string> Targets(Rule r, IReadOnlyCollection<string> running, ISet<string> allTriggers)
    {
        if (!r.Targets.Contains(AllOthers)) return r.Targets.Where(running.Contains).ToList();
        return running.Where(a => !allTriggers.Contains(a)).ToList();
    }

    public static HashSet<string> AllTriggers(IEnumerable<Rule> rules) => rules.Where(r => r.Enabled).SelectMany(r => r.Triggers).ToHashSet();
}
