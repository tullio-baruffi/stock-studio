using Microsoft.AspNetCore.Mvc;
using StockStudio.Api.Services;
using StockStudio.Api.Services.Ai;

namespace StockStudio.Api.Controllers;

/// <summary>Market/trend opportunity suggestions for planning new creations.</summary>
[ApiController]
[Route("api/trends")]
public class TrendsController : ControllerBase
{
    private readonly TrendService _trends;
    private readonly LiveTrendService _live;
    private readonly NewsTrendClient _news;
    private readonly StockRelevanceService _relevance;
    private readonly ThemeOpportunityService _themes;
    private readonly StockIntelligenceRouter _router;

    public TrendsController(TrendService trends, LiveTrendService live, NewsTrendClient news,
                            StockRelevanceService relevance, ThemeOpportunityService themes,
                            StockIntelligenceRouter router)
    {
        _trends = trends;
        _live = live;
        _news = news;
        _relevance = relevance;
        _themes = themes;
        _router = router;
    }

    /// <summary>
    /// The generalized view: themes worth creating today, reasoned by the AI agent when configured
    /// (it decides what is sellable and why) and by the built-in catalogue otherwise.
    /// </summary>
    [HttpGet("themes")]
    public async Task<IActionResult> Themes([FromQuery] string style = "silhouette",
                                            [FromQuery] string? category = null,
                                            [FromQuery] string geo = "US",
                                            [FromQuery] int count = 12,
                                            [FromQuery] bool refresh = false,
                                            [FromQuery] bool withTrajectory = true,
                                            CancellationToken ct = default)
    {
        var outcome = await _router.DiscoverAsync(style ?? "silhouette", category,
                                                  geo, Math.Clamp(count, 3, 25), refresh, ct);
        return Ok(new
        {
            generatedAt = outcome.GeneratedAt.ToString("o"),
            engine = outcome.Engine,
            engineLabel = _router.Describe(),
            warning = outcome.Warning,
            fromCache = outcome.FromCache,
            cacheHours = _router.CacheHours,
            source = outcome.Engine == "agentic"
                ? "Agente AI con strumenti: Google Trends, notizie reali e misurazione dell'interesse su Wikipedia"
                : "Wikipedia pageviews (stagionalità 14 mesi + traiettoria 15 gg) + Google Trends come segnale 'caldo adesso'",
            note = outcome.Engine == "agentic"
                ? "L'agente sceglie i temi e li motiva; i numeri sono sempre riletti dalla fonte, mai generati dal modello."
                : "Analisi generalizzata dal catalogo predefinito. Configura un motore AI per un'analisi agentica.",
            // In agentic mode the agent invents its own categories, so the filter must reflect what
            // it actually proposed — plus the built-in ones, which stay useful to steer it.
            categories = outcome.Value.Select(t => t.Category)
                .Concat(StockThemeCatalog.All.Select(t => t.Category))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c),
            count = outcome.Value.Count,
            themes = outcome.Value,
        });
    }

    /// <summary>What people are searching for right now, with their 15-day trajectory.</summary>
    [HttpGet("live")]
    public async Task<IActionResult> Live([FromQuery] string geo = "US", [FromQuery] int take = 12,
                                          [FromQuery] string style = "silhouette",
                                          [FromQuery] bool onlyUsable = false,
                                          [FromQuery] string promptStyle = "silhouette",
                                          CancellationToken ct = default)
    {
        var trends = await _live.GetAsync(geo, take, style ?? "", onlyUsable, promptStyle ?? "silhouette", ct);
        return Ok(new
        {
            generatedAt = DateTime.UtcNow.ToString("o"),
            geo,
            onlyUsable,
            source = "Google Trends (trending now) + Wikipedia daily pageviews (15 gg)",
            filter = onlyUsable
                ? "Solo temi utilizzabili per stock (esclusi marchi, persone e cronaca)"
                : "Tutti i trend, con verdetto di utilizzabilità per ciascuno",
            trends,
        });
    }

    /// <summary>Themes predicted from real-world news coverage of upcoming events.</summary>
    [HttpGet("predicted")]
    public async Task<IActionResult> Predicted([FromQuery] int yearsAhead = 2,
                                               [FromQuery] string promptStyle = "silhouette",
                                               CancellationToken ct = default)
    {
        var predictions = await _news.PredictAsync(yearsAhead, _relevance, promptStyle ?? "silhouette", ct);
        return Ok(new
        {
            generatedAt = DateTime.UtcNow.ToString("o"),
            source = "Google News RSS (copertura di eventi futuri)",
            predictions,
        });
    }

    /// <summary>Judges an arbitrary idea and returns ready-to-use image prompts.</summary>
    [HttpGet("relevance")]
    public async Task<IActionResult> Relevance([FromQuery] string topic,
                                               [FromQuery] string promptStyle = "silhouette",
                                               [FromQuery] bool refresh = false,
                                               CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return BadRequest(new { error = "Indica un tema da valutare." });

        var outcome = await _router.ClassifyAsync(topic.Trim(), promptStyle ?? "silhouette", refresh, ct);
        return Ok(new
        {
            topic,
            engine = outcome.Engine,
            engineLabel = _router.Describe(),
            warning = outcome.Warning,
            fromCache = outcome.FromCache,
            generatedAt = outcome.GeneratedAt.ToString("o"),
            relevance = outcome.Value,
        });
    }

    /// <summary>Which intelligence engine is active, for the Sistema panel.</summary>
    [HttpGet("engine")]
    public IActionResult Engine() => Ok(new
    {
        agentic = _router.AgenticAvailable,
        label = _router.Describe(),
        cacheHours = _router.CacheHours,
    });

    /// <summary>Drops every cached agent answer, so the next request asks the model again.</summary>
    [HttpPost("cache/clear")]
    public IActionResult ClearCache()
    {
        var removed = _router.ClearCache();
        return Ok(new { ok = true, removed, message = $"{removed} risposte in cache rimosse." });
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int horizonDays = 150, [FromQuery] string style = "silhouette", CancellationToken ct = default)
    {
        var suggestions = await _trends.SuggestAsync(DateTime.UtcNow, Math.Clamp(horizonDays, 30, 365), style ?? "", ct);
        var live = suggestions.Count(s => s.DemandSource == "live");
        return Ok(new
        {
            generatedAt = DateTime.UtcNow.ToString("o"),
            horizonDays,
            style,
            demandSource = $"Wikipedia pageviews ({live}/{suggestions.Count} temi con dati live)",
            note = "La domanda è misurata sull'interesse reale del pubblico. La saturazione sui siti stock non è leggibile in automatico (403): usa i link per vedere il conteggio reale.",
            suggestions,
        });
    }
}
