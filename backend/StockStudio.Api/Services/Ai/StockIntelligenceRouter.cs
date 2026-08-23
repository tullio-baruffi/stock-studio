using System.Collections.Concurrent;

namespace StockStudio.Api.Services.Ai;

/// <summary>
/// Chooses the agentic engine when an AI model is configured, and falls back to the rule-based
/// one otherwise — including when the model errors out mid-request.
///
/// The fallback is deliberately kept: it is far less capable (a fixed vocabulary that only knows
/// what was written into it) but it means the app never goes dark, and every response says which
/// engine produced it so the difference is never hidden from the user.
///
/// Le risposte dell'agente restano in cache per l'intera giornata lavorativa. La scoperta dei temi
/// non si aspetta mai in linea: e' troppo lenta per starci dentro una richiesta, quindi si serve
/// subito quel che c'e' e la si aggiorna in background.
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

    /// <summary>
    /// Chiavi per cui un'analisi agentica e' gia' in corso, per non lanciarne due sulla stessa
    /// domanda: ogni giro costa denaro e minuti, e due utenti che aprono la scheda insieme non
    /// devono farne partire due.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    /// <summary>
    /// L'elenco dei temi, senza far aspettare nessuno.
    ///
    /// L'analisi agentica per questa scheda e' cara in tempo prima ancora che in denaro: sei giri di
    /// modello piu' le interrogazioni a Wikipedia, Google Trends e le notizie, su un worker gratuito.
    /// Misurata, supera i tre minuti -- oltre la pazienza di chiunque e oltre la vita della
    /// connessione, che cadeva prima della fine. E cadendo non popolava nemmeno la cache, quindi la
    /// volta dopo ricominciava da capo: lenta per sempre.
    ///
    /// Quindi non si aspetta. Se la cache ha una risposta si serve quella; altrimenti si risponde
    /// subito col catalogo deterministico e si manda l'agente a lavorare in background, cosi' che
    /// la prossima apertura trovi la sua analisi gia' pronta. E' lo stesso principio del resto del
    /// sistema: consegnare e farsi da parte, invece di tenere qualcuno fermo davanti a una clessidra.
    /// </summary>
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

            var started = StartBackgroundDiscovery(key, promptStyle, category, geo, count);
            var provisional = await _themes.RankAsync(DateTime.UtcNow, promptStyle, category, geo, true, ct);
            return new Outcome<IReadOnlyList<ThemeOpportunity>>(provisional, "rules",
                started
                    ? "Analisi dell'agente avviata: richiede qualche minuto. Intanto vedi il catalogo "
                      + "predefinito; riapri la scheda fra poco per l'analisi vera."
                    : "Analisi dell'agente gia' in corso: intanto vedi il catalogo predefinito.",
                false, DateTime.UtcNow);
        }

        var fb = await _themes.RankAsync(DateTime.UtcNow, promptStyle, category, geo, true, ct);
        return new Outcome<IReadOnlyList<ThemeOpportunity>>(fb, "rules", null, false, DateTime.UtcNow);
    }

    /// <summary>
    /// Manda l'agente a produrre l'analisi e a depositarla in cache, slegato dalla richiesta.
    ///
    /// Il token della richiesta non va usato: muore con la risposta, e l'analisi verrebbe interrotta
    /// proprio mentre la si sta pagando. Ha invece un tempo massimo suo. Se il piano gratuito
    /// scarica l'applicazione a meta' lavoro l'analisi va persa e si ripartira' da capo: e' un
    /// costo accettabile, perche' nel frattempo nessuno e' rimasto ad aspettare.
    /// </summary>
    private bool StartBackgroundDiscovery(string key, string promptStyle, string? category, string geo, int count)
    {
        if (!_inFlight.TryAdd(key, 0)) return false;

        _ = Task.Run(async () =>
        {
            using var life = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            try
            {
                _log.LogInformation("Analisi agentica avviata in background per {Key}", key);
                var r = await _agentic.DiscoverAsync(promptStyle, category, geo, count, life.Token);
                if (r.Count > 0)
                {
                    _cache.Set(key, r.ToList(), DateTime.UtcNow);
                    _log.LogInformation("Analisi agentica pronta: {Count} temi in cache per {Key}", r.Count, key);
                }
                else
                {
                    _log.LogWarning("L'agente non ha proposto temi per {Key}: resta il catalogo predefinito", key);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Analisi agentica non riuscita per {Key}", key);
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        });

        return true;
    }
}
