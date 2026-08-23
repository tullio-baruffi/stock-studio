using StockStudio.Api.Services.Feedback;

namespace StockStudio.Api.Services;

/// <summary>
/// Turns whatever a model produced into metadata the marketplaces will accept.
///
/// Kept apart from the provider that made the call, because the guard rails must hold whoever
/// generated the values: asking politely in the prompt leaves subjective adjectives in, drops the
/// mandatory "graphic", and keeps "no people" on pictures of people often enough to matter. The
/// prompt asks; this enforces.
/// </summary>
public class MetadataNormalizer
{
    private readonly MetadataGuidance _guidance;
    private readonly ILogger<MetadataNormalizer> _log;

    public MetadataNormalizer(MetadataGuidance guidance, ILogger<MetadataNormalizer> log)
    {
        _guidance = guidance;
        _log = log;
    }

    public MetadataResult Normalize(string? title, string? description, IEnumerable<string>? keywords,
                                    string? category, int maxKeywords)
    {
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? "Vector Silhouette" : title.Trim();
        var cleanDescription = string.IsNullOrWhiteSpace(description) ? cleanTitle : description.Trim();
        var cleanCategory = string.IsNullOrWhiteSpace(category) ? "Graphic Resources" : category.Trim();

        var list = (keywords ?? Enumerable.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        // Hard guard rail. The written ban is respected most of the time, and "most of the time"
        // would still push terms the author rejected onto Adobe Stock and Freepik.
        var banned = _guidance.BannedKeywords;
        if (banned.Count > 0)
        {
            var block = banned.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var before = list.Count;
            list = list.Where(k => !block.Contains(k)).ToList();
            if (list.Count < before)
                _log.LogInformation("Rimosse {N} keyword vietate dai feedback dell'autore", before - list.Count);
        }

        var raw = list.Count;
        list = AdobeStockRules.Enforce(list, cleanTitle, "vector", maxKeywords);
        if (list.Count != raw)
            _log.LogDebug("Keyword normalizzate secondo la guida Adobe: {Before} -> {After}", raw, list.Count);

        return new MetadataResult(cleanTitle, cleanDescription, list, cleanCategory);
    }
}
