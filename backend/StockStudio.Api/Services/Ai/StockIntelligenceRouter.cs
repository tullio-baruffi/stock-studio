namespace StockStudio.Api.Services.Ai;

/// <summary>
/// Chooses the agentic engine when an AI model is configured, and falls back to the rule-based
/// one otherwise — including when the model errors out mid-request.
///
/// The fallback is deliberately kept: it is far less capable (a fixed vocabulary that only knows
/// what was written into it) but it means the app never goes dark, and every response says which
/// engine produced it so the difference is never hidden from the user.
///
/// Agent answers are cached on disk so the list stays stable through the working day; passing
/// <c>refresh</c> asks for a genuinely new opinion.
/// </summary>
public class StockIntelligenceRouter
{
    private readonly AgenticStockIntelligence _agentic;
    private readonly StockRelevanceService _rules;
    private readonly ThemeOpportunityService _themes;
    private readonly AgentCache _cache;
    private readonly ILogger<StockIntelligenceRouter> _log;

    public StockIntelligenceRouter(AgenticStockIntelligence agentic, StockRelevanceService rules,
                                   ThemeOpportunityService themes, AgentCache cache,
                                   ILogger<StockIntelligenceRouter> log)
    {
        _agentic = agentic;
        _rules = rules;
        _themes = themes;
        _cache = cache;
        _log = log;
    }

    public bool AgenticAvailable => _agentic.IsAgentic;

    public string Describe() => AgenticAvailable
        ? $"agentico ({_agentic.Describe()})"
        : "deterministico (vocabolario predefinito) — nessun motore AI configurato";

    public double CacheHours => _cache.Ttl.TotalHours;

    public int ClearCache() => _cache.Clear();

    /// <param name="GeneratedAt">When the answer was actually produced — may predate this request.</param>
    public record Outcome<T>(T Value, string Engine, string? Warning, bool FromCache, DateTime GeneratedAt);

    public async Task<Outcome<StockRelevance>> ClassifyAsync(string topic, string promptStyle,
                                                             bool refresh, CancellationToken ct)
    {
        if (_agentic.IsAgentic)
        {
            var key = $"classify|{_agentic.Describe()}|{promptStyle}|{topic.Trim().ToLowerInvariant()}";
            if (!refresh)
            {
                var hit = _cache.Get<StockRelevance>(key);
                if (hit is not null)
                    return new Outcome<StockRelevance>(hit.Value, "agentic", null, true, hit.GeneratedAt);
            }

            try
            {
                var r = await _agentic.ClassifyAsync(topic, promptStyle, ct);
                var now = DateTime.UtcNow;
                _cache.Set(key, r, now);
                return new Outcome<StockRelevance>(r, "agentic", null, false, now);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Agente non disponibile, uso le regole");
                var fb = await _rules.ClassifyAsync(topic, null, promptStyle, ct);
                return new Outcome<StockRelevance>(fb, "rules",
                    $"Motore AI non raggiungibile ({ex.Message}). Risposta prodotta dal vocabolario predefinito.",
                    false, DateTime.UtcNow);
            }
        }

        var deterministic = await _rules.ClassifyAsync(topic, null, promptStyle, ct);
        return new Outcome<StockRelevance>(deterministic, "rules", null, false, DateTime.UtcNow);
    }

    public async Task<Outcome<IReadOnlyList<ThemeOpportunity>>> DiscoverAsync(
        string promptStyle, string? category, string geo, int count, bool refresh, CancellationToken ct)
    {
        if (_agentic.IsAgentic)
        {
            var key = $"discover|{_agentic.Describe()}|{promptStyle}|{category ?? "all"}|{geo}|{count}";
            if (!refresh)
            {
                var hit = _cache.Get<List<ThemeOpportunity>>(key);
                if (hit is { Value.Count: > 0 })
                    return new Outcome<IReadOnlyList<ThemeOpportunity>>(hit.Value, "agentic", null, true, hit.GeneratedAt);
            }

            try
            {
                var r = await _agentic.DiscoverAsync(promptStyle, category, geo, count, ct);
                if (r.Count > 0)
                {
                    var now = DateTime.UtcNow;
                    _cache.Set(key, r.ToList(), now);
                    return new Outcome<IReadOnlyList<ThemeOpportunity>>(r, "agentic", null, false, now);
                }
                _log.LogWarning("L'agente non ha proposto temi, uso le regole");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Agente non disponibile, uso le regole");
                var fb0 = await _themes.RankAsync(DateTime.UtcNow, promptStyle, category, geo, true, ct);
                return new Outcome<IReadOnlyList<ThemeOpportunity>>(fb0, "rules",
                    $"Motore AI non raggiungibile ({ex.Message}). Elenco prodotto dal catalogo predefinito.",
                    false, DateTime.UtcNow);
            }
        }

        var fb = await _themes.RankAsync(DateTime.UtcNow, promptStyle, category, geo, true, ct);
        return new Outcome<IReadOnlyList<ThemeOpportunity>>(fb, "rules", null, false, DateTime.UtcNow);
    }
}
