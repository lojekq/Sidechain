namespace Sidechain.Features.Core;

/// <summary>A leveled app from settings: its normal loudness (RMS dBFS before the session volume).</summary>
public sealed record LevelerEntry(string App, double TargetDb);

/// <summary>
/// Slow automatic volume that only turns down (spec §5): the source louder than the target gets cut, up to 12 dB,
/// 6 dB/s down and 1 dB/s up; silence keeps the gain. Not a limiter: the first ~0.5 s of a loud ad is still loud.
/// </summary>
// ponytail: plain RMS, no K-weighting (RMS ≈ LUFS for speech and music); add a K-filter if ads slip through
public sealed class Leveler
{
    public const double SilenceDb = -50, MaxCutDb = 12, DownDbPerS = 6, UpDbPerS = 1, DefaultTargetDb = -20, HistoryMs = 30_000;

    readonly Queue<(double At, double Db)> history = new();
    public double GainDb { get; private set; }

    /// <param name="levelDb">The source level before the session volume; NaN = no measurement.</param>
    public void Step(double nowMs, double dtMs, double levelDb, double targetDb)
    {
        while (history.Count > 0 && nowMs - history.Peek().At > HistoryMs) history.Dequeue();
        if (double.IsNaN(levelDb) || levelDb < SilenceDb) return; // silence or a pause: keep the gain
        history.Enqueue((nowMs, levelDb));
        var want = Math.Clamp(targetDb - levelDb, -MaxCutDb, 0);
        GainDb = want < GainDb
            ? Math.Max(want, GainDb - DownDbPerS * dtMs / 1000)
            : Math.Min(want, GainDb + UpDbPerS * dtMs / 1000);
    }

    /// <summary>The user moved the volume by hand: start again from it.</summary>
    public void Reset() => GainDb = 0;

    /// <summary>"Make the current level the norm": the median of the last 30 s of sound; null before any sound.</summary>
    public double? Median()
    {
        if (history.Count == 0) return null;
        var sorted = history.Select(h => h.Db).Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
