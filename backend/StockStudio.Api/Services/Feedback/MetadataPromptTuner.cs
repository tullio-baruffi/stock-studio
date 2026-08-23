using System.Text;
using StockStudio.Api.Services.Ai;

namespace StockStudio.Api.Services.Feedback;

public record TuneResult(GuidanceState Guidance, string Engine, int EntriesUsed, string? Warning);

/// <summary>
/// The review process. It reads the corrections the author made to the generated metadata,
/// works out what they have in common, and rewrites the extra instructions that will be appended
/// to the metadata generation prompt.
///
/// Agentic when a model is configured: the model reads generated-vs-corrected pairs plus the
/// author's notes and writes the rules itself. Otherwise a deterministic pass still extracts the
/// recurring signals, so the loop keeps working without any AI.
/// </summary>
public class MetadataPromptTuner
{
    private const int MaxEntriesToModel = 40;
    private const int MinOccurrencesForRule = 2;

    private readonly MetadataFeedbackStore _store;
    private readonly MetadataGuidance _guidance;
    private readonly LlmClient _llm;
    private readonly ILogger<MetadataPromptTuner> _log;

    public MetadataPromptTuner(MetadataFeedbackStore store, MetadataGuidance guidance,
                               LlmClient llm, ILogger<MetadataPromptTuner> log)
    {
        _store = store;
        _guidance = guidance;
        _llm = llm;
        _log = log;
    }

    /// <summary>Feedback recorded since the guidance was last rebuilt.</summary>
    public int PendingCount => _store.Since(_guidance.Current.LastEntryAt).Count;

    public async Task<TuneResult> RebuildAsync(CancellationToken ct)
    {
        // Always rebuild from the whole journal, not just the new entries: a rule derived from
        // three corrections must not be silently dropped when a fourth one arrives.
        var all = _store.Since(null);
        if (all.Count == 0)
        {
            var cleared = _guidance.Reset();
            return new TuneResult(cleared, "none", 0, "Nessun feedback registrato: la guida e' stata riportata al prompt predefinito.");
        }

        var used = all.OrderByDescending(e => e.At).Take(MaxEntriesToModel).OrderBy(e => e.At).ToList();
        var lastAt = all.Max(e => e.At);

        // Computed from the journal, never from the model's prose: this list is enforced in code,
        // so it must be exact rather than paraphrased.
        var banned = BannedFrom(all);

        if (_llm.IsConfigured)
        {
            try
            {
                var text = await AskModelAsync(used, ct);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var state = _guidance.Update(text, used.Count, lastAt, "agentic", banned);
                    _log.LogInformation("Guida metadati rigenerata dall'agente: versione {V} da {N} feedback", state.Version, used.Count);
                    return new TuneResult(state, "agentic", used.Count, null);
                }
                _log.LogWarning("L'agente non ha prodotto regole utilizzabili: uso la sintesi deterministica");
            }
            catch (Exception ex)
            {
                // A model outage must not block the loop: fall back instead of failing the request.
                _log.LogWarning(ex, "Revisione agentica non riuscita: uso la sintesi deterministica");
                var fb = _guidance.Update(Deterministic(used), used.Count, lastAt, "rules", banned);
                return new TuneResult(fb, "rules", used.Count,
                    $"Motore AI non raggiungibile ({ex.Message}). Regole ricavate in modo deterministico dai feedback.");
            }
        }

        var rules = Deterministic(used);
        var det = _guidance.Update(rules, used.Count, lastAt, "rules", banned);
        return new TuneResult(det, "rules", used.Count,
            _llm.IsConfigured ? "L'agente non ha prodotto regole: usata la sintesi deterministica."
                              : "Nessun motore AI configurato: regole ricavate in modo deterministico dai feedback.");
    }

    /// <summary>
    /// Keywords the author took out and never put back. Removing one is an explicit act, so a
    /// single removal is enough; adding it again later clears the ban by itself.
    ///
    /// Only selective edits count. A wholesale replacement of the keyword set would otherwise
    /// ban every term the generator had proposed, including the correct ones.
    /// </summary>
    private static IReadOnlyList<string> BannedFrom(IReadOnlyList<FeedbackEntry> entries)
    {
        var selective = entries.Where(e => e.IsSelectiveEdit).ToList();

        var added = entries.SelectMany(e => e.KeywordsAdded)
            .Select(k => k.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return selective.SelectMany(e => e.KeywordsRemoved)
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => k.Length > 1 && !added.Contains(k))
            .GroupBy(k => k)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(120)
            .ToList();
    }

    // ------------------------------------------------------------------ agentic

    private async Task<string?> AskModelAsync(IReadOnlyList<FeedbackEntry> entries, CancellationToken ct)
    {
        var system =
            "Sei un revisore di metadati per microstock (Adobe Stock e Freepik). " +
            "Ricevi le correzioni che l'autore ha applicato ai metadati generati automaticamente, " +
            "ognuna con l'eventuale spiegazione scritta da lui. " +
            "Il tuo compito e' dedurre le regole ricorrenti e riscrivere le istruzioni aggiuntive " +
            "da appendere al prompt di generazione, in modo che le prossime immagini non richiedano " +
            "le stesse correzioni.\n\n" +
            "Vincoli:\n" +
            "- Scrivi le regole IN INGLESE: finiscono dentro un prompt in inglese.\n" +
            "- Massimo 12 regole, una per riga, ciascuna preceduta da '- '.\n" +
            "- Solo regole generalizzabili. Scarta cio' che vale per una singola immagine.\n" +
            "- Non inventare regole non supportate dalle correzioni ricevute.\n" +
            "- Sii specifico e verificabile: 'avoid the word X', 'always include Y when Z', " +
            "'titles must not exceed N words'. Evita consigli vaghi come 'be accurate'.\n" +
            "- Se le correzioni non mostrano alcuno schema ricorrente, rispondi esattamente: NESSUNA REGOLA\n\n" +
            "Rispondi SOLO con l'elenco delle regole, senza introduzione ne' commento finale.";

        var sb = new StringBuilder();
        sb.AppendLine($"Correzioni raccolte ({entries.Count}), dalla piu' vecchia alla piu' recente:");
        sb.AppendLine();
        int n = 0;
        foreach (var e in entries)
        {
            n++;
            sb.AppendLine($"### Correzione {n} - file '{e.BaseName}' ({e.At:yyyy-MM-dd})");
            if (e.Generated is not null)
            {
                sb.AppendLine($"Titolo generato:  {e.Generated.Title}");
                sb.AppendLine($"Titolo corretto:  {e.Corrected.Title}");
                if (e.CategoryChanged)
                    sb.AppendLine($"Categoria: da '{e.Generated.Category}' a '{e.Corrected.Category}'");
            }
            else
            {
                sb.AppendLine($"Titolo finale:    {e.Corrected.Title}");
            }
            if (e.IsSelectiveEdit)
            {
                if (e.KeywordsRemoved.Count > 0)
                    sb.AppendLine("Keyword rimosse:  " + string.Join(", ", e.KeywordsRemoved.Take(25)));
                if (e.KeywordsAdded.Count > 0)
                    sb.AppendLine("Keyword aggiunte: " + string.Join(", ", e.KeywordsAdded.Take(25)));
            }
            else
            {
                // Listing every term of a full rewrite would make the model infer bans that the
                // author never intended: report the fact, not the list.
                sb.AppendLine($"L'autore ha riscritto da zero l'intera lista di keyword " +
                              $"({e.Corrected.Keywords.Count} termini). Non dedurre divieti sui singoli termini rimossi.");
                if (e.KeywordsAdded.Count > 0)
                    sb.AppendLine("Lista finale (estratto): " + string.Join(", ", e.Corrected.Keywords.Take(20)));
            }
            if (!string.IsNullOrWhiteSpace(e.Note))
                sb.AppendLine($"Spiegazione dell'autore: {e.Note}");
            sb.AppendLine();
        }

        var messages = new List<object>
        {
            new { role = "system", content = system },
            new { role = "user", content = sb.ToString() },
        };

        var reply = await _llm.ChatAsync(messages, null, 0.2, jsonMode: false, ct);
        var text = (reply.Content ?? "").Trim();

        if (text.Length == 0) return null;
        if (text.Contains("NESSUNA REGOLA", StringComparison.OrdinalIgnoreCase)) return null;

        // Keep only the bullet lines: models like to add a preamble despite being told not to.
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ") || l.StartsWith("* "))
            .Select(l => "- " + l[2..].Trim())
            .Where(l => l.Length > 4)
            .Take(12)
            .ToList();

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    // ------------------------------------------------------------ deterministic

    /// <summary>
    /// Fallback synthesis: no model involved. Only signals seen at least twice become rules,
    /// so a single unusual correction cannot poison the prompt.
    /// </summary>
    private static string Deterministic(IReadOnlyList<FeedbackEntry> entries)
    {
        var rules = new List<string>();

        // Same reasoning as the ban list: a wholesale rewrite is not evidence about single terms.
        var selective = entries.Where(e => e.IsSelectiveEdit).ToList();

        var removed = selective.SelectMany(e => e.KeywordsRemoved)
            .GroupBy(k => k.ToLowerInvariant())
            .Where(g => g.Count() >= MinOccurrencesForRule)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(15)
            .ToList();
        if (removed.Count > 0)
            rules.Add("- Never use these keywords, the author consistently removes them: " + string.Join(", ", removed) + ".");

        var added = selective.SelectMany(e => e.KeywordsAdded)
            .GroupBy(k => k.ToLowerInvariant())
            .Where(g => g.Count() >= MinOccurrencesForRule)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(15)
            .ToList();
        if (added.Count > 0)
            rules.Add("- Prefer these keywords when relevant, the author keeps adding them: " + string.Join(", ", added) + ".");

        var categories = entries.Where(e => e.CategoryChanged)
            .GroupBy(e => e.Corrected.Category, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= MinOccurrencesForRule)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();
        if (categories.Count > 0)
            rules.Add("- The author usually reassigns the category to: " + string.Join(", ", categories) + ".");

        // Title length is the correction that shows up most often and is trivially measurable.
        var titlePairs = entries.Where(e => e.TitleChanged && e.Generated is not null).ToList();
        if (titlePairs.Count >= MinOccurrencesForRule)
        {
            double before = titlePairs.Average(e => Words(e.Generated!.Title));
            double after = titlePairs.Average(e => Words(e.Corrected.Title));
            if (after < before - 0.75)
                rules.Add($"- Keep titles short: the author shortens them to about {Math.Round(after)} words.");
            else if (after > before + 0.75)
                rules.Add($"- Write richer titles: the author expands them to about {Math.Round(after)} words.");
        }

        var notes = entries.Where(e => !string.IsNullOrWhiteSpace(e.Note))
            .Select(e => e.Note!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(8)
            .ToList();
        if (notes.Count > 0)
        {
            rules.Add("- Notes written by the author about previous corrections, honour them:");
            rules.AddRange(notes.Select(nt => "  - " + nt.Replace("\r", " ").Replace("\n", " ")));
        }

        return rules.Count > 0
            ? string.Join("\n", rules)
            : "";
    }

    private static int Words(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
