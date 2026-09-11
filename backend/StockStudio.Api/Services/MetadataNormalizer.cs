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
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
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
        list = AdobeStockRules.Enforce(list, cleanTitle, Medium(list), maxKeywords);
        if (list.Count != raw)
            _log.LogDebug("Keyword normalizzate secondo la guida Adobe: {Before} -> {After}", raw, list.Count);

        return new MetadataResult(cleanTitle, cleanDescription, list, cleanCategory);
    }

    /// <summary>
    /// Che tipo di immagine ha descritto il modello, letto da quello che ha scritto lui.
    ///
    /// Qui c'era scritto "vector" per tutti, e le regole di Adobe inserivano d'ufficio 'vector' e
    /// 'graphic' in quarta e quinta posizione anche sulla fotografia di una partita di polo. Su una
    /// foto quelle due parole sono keyword irrilevanti — uno dei motivi di rifiuto dichiarati — e
    /// occupavano per giunta due delle sette posizioni che pesano.
    ///
    /// Il modello il mestiere lo sa fare: al prompt che gli chiede di riconoscere il supporto
    /// rispondeva già "photograph", mentre il codice lo contraddiceva subito dopo. Quindi non
    /// serve indovinare: basta leggere la risposta.
    /// </summary>
    private static string Medium(IReadOnlyList<string> keywords)
    {
        bool Ha(params string[] parole) => parole.Any(p => keywords.Contains(p, StringComparer.OrdinalIgnoreCase));

        // La fotografia si dichiara per prima: se il modello l'ha riconosciuta, non c'è altro da
        // decidere, e nessun termine di supporto va aggiunto.
        if (Ha("photograph", "photo", "photography")) return "raster";
        if (Ha("vector", "icon")) return "vector";
        return "raster";
    }
}
