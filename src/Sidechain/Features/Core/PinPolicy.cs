namespace Sidechain.Features.Core;

/// <summary>An output device: active one from the backend, or an entry of the priority list (id may be stale, then the name matches).</summary>
public sealed record AudioDevice(string Id, string Name);

public enum PinVerdict { Keep, SwitchBack, War }

/// <summary>
/// Should the default output be switched back? Only when the new default just appeared (plugged in, woke up, a monitor
/// over HDMI): then the best available device of the priority list wins. A user's own choice is never contested,
/// and more than 3 switches a minute means another program fights us: give up.
/// </summary>
public sealed class PinPolicy
{
    public const double FreshMs = 10_000, WarWindowMs = 60_000;
    public const int WarMax = 3;

    readonly Queue<double> switches = new();

    /// <summary>The first device of the priority list that is active: by id, else by name (driver reinstalled, other HDMI port).</summary>
    public static AudioDevice? Best(IEnumerable<AudioDevice> priority, IReadOnlyList<AudioDevice> active)
    {
        foreach (var p in priority)
            if ((active.FirstOrDefault(a => a.Id == p.Id) ?? active.FirstOrDefault(a => a.Name == p.Name)) is { } d) return d;
        return null;
    }

    /// <summary>The default changed to <paramref name="newDefault"/>, which became active at <paramref name="activeSinceMs"/>.</summary>
    public (PinVerdict Verdict, AudioDevice? To) OnDefaultChanged(string newDefault, double activeSinceMs, double nowMs,
        IEnumerable<AudioDevice> priority, IReadOnlyList<AudioDevice> active)
    {
        if (nowMs - activeSinceMs > FreshMs) return (PinVerdict.Keep, null); // the user picked it
        var best = Best(priority, active);
        if (best is null || best.Id == newDefault) return (PinVerdict.Keep, null);
        while (switches.Count > 0 && nowMs - switches.Peek() > WarWindowMs) switches.Dequeue();
        if (switches.Count >= WarMax) return (PinVerdict.War, null);
        switches.Enqueue(nowMs);
        return (PinVerdict.SwitchBack, best);
    }
}
