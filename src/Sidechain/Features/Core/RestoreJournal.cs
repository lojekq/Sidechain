using System.IO;
using System.Text.Json;
using Sidechain.Shell;

namespace Sidechain.Features.Core;

public sealed record JournalEntry(string App, string DeviceId, float Baseline, DateTime SavedAt);

/// <summary>
/// restore.json: the user's own volume of every (app, device) before Sidechain changed it. Windows remembers a session's
/// volume for the app forever, so a crash or a kill would leave Spotify quiet for good without this file.
/// Written on transitions only (start and end of a change, new baseline); atomic (tmp + replace). Null path = memory only (--demo).
/// </summary>
public sealed class RestoreJournal
{
    public const double MaxAgeDays = 30;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly string? path;
    readonly List<JournalEntry> entries;

    RestoreJournal(string? path, List<JournalEntry> entries) => (this.path, this.entries) = (path, entries);

    public IReadOnlyList<JournalEntry> Entries => entries;

    /// <summary>Entries older than 30 days are dropped. A file that can't be read gives an empty journal (logged).</summary>
    public static RestoreJournal Load(string? path, DateTime now)
    {
        List<JournalEntry>? list = null;
        if (path is not null)
            try { list = AtomicFile.Read(path, s => JsonSerializer.Deserialize<List<JournalEntry>>(s, Json)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("restore.json not read", e); }
        list ??= [];
        var fresh = list.Where(e => (now - e.SavedAt).TotalDays <= MaxAgeDays).ToList();
        var j = new RestoreJournal(path, fresh);
        if (fresh.Count != list.Count) j.Save();
        return j;
    }

    public JournalEntry? Find(string app, string deviceId) => entries.Find(e => e.App == app && e.DeviceId == deviceId);

    public void Set(string app, string deviceId, float baseline, DateTime now)
    {
        var old = Find(app, deviceId);
        if (old is not null && old.Baseline == baseline) return;
        if (old is not null) entries.Remove(old);
        entries.Add(new(app, deviceId, baseline, now));
        Save();
    }

    public void Remove(string app, string deviceId)
    {
        if (entries.RemoveAll(e => e.App == app && e.DeviceId == deviceId) > 0) Save();
    }

    void Save()
    {
        if (path is null) return;
        try { AtomicFile.WriteAllText(path, JsonSerializer.Serialize(entries, Json)); }
        catch (Exception e) { Log.Error("restore.json not saved", e); }
    }

    /// <summary>
    /// A session of (app, device) appears and the journal has its baseline. Quieter than the baseline: Windows gave it the
    /// ducked volume we left behind (crash, kill, the app recreated its stream), so the baseline comes back.
    /// Otherwise the user set it by hand meanwhile: their volume is the new baseline and the entry can go.
    /// </summary>
    // ponytail: if the user turned the app down by hand while Sidechain was closed, the old baseline comes back; never louder than they once set
    public static (float Baseline, bool Restore) Adopt(float journalBaseline, float volume) =>
        volume < journalBaseline - 0.01f ? (journalBaseline, true) : (volume, false);
}
