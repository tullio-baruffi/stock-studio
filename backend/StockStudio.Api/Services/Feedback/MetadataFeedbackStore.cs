using System.Text.Json;
using StockStudio.Api.Domain;

namespace StockStudio.Api.Services.Feedback;

/// <summary>One correction made by the author, with the note explaining it.</summary>
public record FeedbackEntry(
    string Id,
    DateTime At,
    string JobId,
    string ItemId,
    string BaseName,
    string Mode,
    string? Note,
    MetadataSnapshot? Generated,
    MetadataSnapshot Corrected,
    IReadOnlyList<string> KeywordsAdded,
    IReadOnlyList<string> KeywordsRemoved,
    bool TitleChanged,
    bool CategoryChanged)
{
    /// <summary>True when there is something to learn from: a note, or an actual edit.</summary>
    public bool IsUseful =>
        !string.IsNullOrWhiteSpace(Note) || TitleChanged || CategoryChanged
        || KeywordsAdded.Count > 0 || KeywordsRemoved.Count > 0;

    /// <summary>
    /// True when the author refined the keyword set instead of replacing it. Wholesale
    /// replacements (bulk operations, imports, an author redoing the list from scratch) say
    /// nothing about the individual terms, so they must not feed the hard ban list.
    /// </summary>
    public bool IsSelectiveEdit =>
        Generated is not null
        && Generated.Keywords.Count > 0
        && KeywordsRemoved.Count * 2 <= Generated.Keywords.Count;
}

/// <summary>
/// Append-only journal of metadata corrections. Deliberately a plain JSON file: the review process
/// reads it in bulk, it must survive restarts, and it stays inspectable without extra tooling.
/// </summary>
public class MetadataFeedbackStore
{
    private readonly string _file;
    private readonly ILogger<MetadataFeedbackStore> _log;
    private readonly object _gate = new();
    private List<FeedbackEntry> _entries = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public MetadataFeedbackStore(ILogger<MetadataFeedbackStore> log)
    {
        _log = log;
        var dir = Path.Combine(AppContext.BaseDirectory, "metadata-feedback");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "entries.json");
        Load();
    }

    public string FilePath => _file;

    public int Count { get { lock (_gate) return _entries.Count; } }

    public IReadOnlyList<FeedbackEntry> Recent(int take = 50)
    {
        lock (_gate) return _entries.OrderByDescending(e => e.At).Take(Math.Clamp(take, 1, 500)).ToList();
    }

    /// <summary>Entries recorded after the given instant, oldest first. Null means "everything".</summary>
    public IReadOnlyList<FeedbackEntry> Since(DateTime? at)
    {
        lock (_gate)
            return _entries.Where(e => at is null || e.At > at).OrderBy(e => e.At).ToList();
    }

    /// <summary>
    /// Builds an entry by comparing what the generator produced with what the author saved.
    /// Returns null when nothing changed and no note was written: there would be nothing to learn.
    /// </summary>
    public FeedbackEntry? Record(Job job, JobItem item, MetadataSnapshot corrected, string? note)
        => Add(Build(job.Id, item.Id, item.BaseName, item.Mode, item.AiOriginal, corrected, note));

    /// <summary>
    /// Lo stesso journal, per le correzioni fatte fuori da un job: la revisione dei file che sono
    /// arrivati in SharePoint attraverso la pipeline. Non c'e' un job a cui puntare, quindi gli
    /// identificativi dicono da dove viene la voce invece di fingersi riferimenti a un job.
    /// </summary>
    public FeedbackEntry? RecordCorrection(string baseName, string mode,
                                           MetadataSnapshot? generated, MetadataSnapshot corrected,
                                           string? note)
        => Add(Build("backoffice", baseName, baseName, mode, generated, corrected, note));

    private static FeedbackEntry Build(string jobId, string itemId, string baseName, string mode,
                                       MetadataSnapshot? generated, MetadataSnapshot corrected, string? note)
    {
        var before = generated?.Keywords ?? new List<string>();

        var added = corrected.Keywords
            .Where(k => !before.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var removed = before
            .Where(k => !corrected.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return new FeedbackEntry(
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow,
            jobId,
            itemId,
            baseName,
            mode,
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            generated,
            corrected,
            added,
            removed,
            generated is not null && !string.Equals(generated.Title, corrected.Title, StringComparison.Ordinal),
            generated is not null && !string.Equals(generated.Category, corrected.Category, StringComparison.OrdinalIgnoreCase));
    }

    private FeedbackEntry? Add(FeedbackEntry entry)
    {
        if (!entry.IsUseful) return null;

        lock (_gate)
        {
            _entries.Add(entry);
            Persist();
        }
        return entry;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = new List<FeedbackEntry>();
            Persist();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            _entries = JsonSerializer.Deserialize<List<FeedbackEntry>>(File.ReadAllText(_file)) ?? new();
            _log.LogInformation("Feedback metadati caricati: {Count} voci", _entries.Count);
        }
        catch (Exception ex)
        {
            // A corrupt journal must not stop the site: start empty and keep the bad file aside.
            _log.LogWarning(ex, "Journal dei feedback illeggibile: riparto da vuoto");
            TryQuarantine();
            _entries = new List<FeedbackEntry>();
        }
    }

    private void TryQuarantine()
    {
        try
        {
            if (File.Exists(_file))
                File.Move(_file, _file + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Impossibile mettere da parte il journal corrotto");
        }
    }

    private void Persist()
    {
        try
        {
            // Write-then-replace: a crash mid-write would otherwise truncate the whole journal.
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Salvataggio dei feedback non riuscito");
        }
    }
}
