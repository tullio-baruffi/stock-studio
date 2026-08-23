namespace StockStudio.Api.Services;

/// <summary>A currently-trending topic enriched with its 15-day trajectory.</summary>
public record LiveTrend(
    string Title,
    long SearchTraffic,
    string? WikiArticle,
    double? TrendRatio,        // last week vs previous week — >1 means still rising
    string Trajectory,         // rising | peaking | fading | unknown
    int OpportunityScore,
    IReadOnlyList<long> Series,
    IReadOnlyList<string> RelatedNews,
    string AdobeSearchUrl,
    string FreepikSearchUrl,
    string Advice,
    StockRelevance? Relevance);

/// <summary>
/// Combines Google Trends "trending now" with Wikipedia daily pageviews to judge whether a hot
/// topic is still climbing (worth creating for) or already past its peak (too late to sell).
/// </summary>
public class LiveTrendService
{
    private readonly GoogleTrendsClient _google;
    private readonly WikipediaTrendClient _wiki;
    private readonly StockRelevanceService _relevance;
    private readonly ILogger<LiveTrendService> _log;

    public LiveTrendService(GoogleTrendsClient google, WikipediaTrendClient wiki,
                            StockRelevanceService relevance, ILogger<LiveTrendService> log)
    {
        _google = google;
        _wiki = wiki;
        _relevance = relevance;
        _log = log;
    }

    public async Task<IReadOnlyList<LiveTrend>> GetAsync(string geo, int take, string style,
                                                         bool onlyUsable, string promptStyle, CancellationToken ct)
    {
        var trending = await _google.GetTrendingAsync(geo, ct);
        // When filtering we start from a wider pool, otherwise the usable ones get cut off early.
        int pool = onlyUsable ? Math.Clamp(take * 3, take, 30) : Math.Clamp(take, 1, 25);
        var shortlist = trending.OrderByDescending(t => t.ApproxTraffic).Take(pool).ToList();

        var results = new List<LiveTrend>();

        foreach (var t in shortlist)
        {
            ct.ThrowIfCancellationRequested();

            var newsContext = string.Join(" ", t.RelatedNews.Take(3));
            var relevance = _relevance.ClassifyFromContext(t.Title, newsContext, promptStyle);
            if (onlyUsable && relevance.Verdict is "reject" or "unknown")
                continue;

            // Measure the generic stock theme, not the protected name that happened to trend.
            var theme = StockThemeCatalog.Find(relevance.Theme);
            string? article = theme?.WikiArticle;

            // Measuring 15 daily buckets is the expensive part. Do it only after the topic has
            // passed the stock-suitability filter, otherwise a rejected person/brand delays the UI.
            IReadOnlyList<long>? series = null;
            if (article != null)
            {
                try
                {
                    series = await _wiki.GetDailySeriesAsync(article, 15, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Traiettoria non disponibile per {Topic}", t.Title);
                }
            }

            double? ratio = null;
            string trajectory = "unknown";
            if (series is { Count: >= 14 })
            {
                double prev = series.Take(7).Average();
                double last = series.Skip(series.Count - 7).Average();
                if (prev > 0)
                {
                    ratio = Math.Round(last / prev, 2);
                    trajectory = ratio >= 1.25 ? "rising" : ratio >= 0.9 ? "peaking" : "fading";
                }
            }

            // Score: search volume (demand) shaped by where the topic sits in its lifecycle.
            double volumeScore = Math.Clamp(Math.Log10(Math.Max(t.ApproxTraffic, 10)) / 5.0, 0, 1) * 70;
            double trajectoryBonus = trajectory switch
            {
                "rising" => 30,
                "peaking" => 18,
                "fading" => 4,
                _ => 12,
            };
            int score = (int)Math.Round(Math.Clamp(volumeScore + trajectoryBonus, 0, 100));

            var advice = trajectory switch
            {
                "rising" => "In crescita: è il momento migliore per creare contenuti su questo tema.",
                "peaking" => "Al picco: puoi ancora sfruttarlo, ma agisci subito.",
                "fading" => "In calo: probabilmente troppo tardi, valuta un tema più fresco.",
                _ => "Traiettoria non misurabile: valuta manualmente la concorrenza.",
            };

            var searchSubject = relevance.Theme ?? t.Title;
            var q = string.IsNullOrWhiteSpace(style) ? searchSubject : $"{searchSubject} {style}";

            // A topic nobody can legally draw is worth less, however hot it is.
            score = relevance.Verdict switch
            {
                "reject" => (int)Math.Round(score * 0.3),
                "unknown" => (int)Math.Round(score * 0.5),
                "adapt" => (int)Math.Round(score * 0.8),
                _ => score,
            };

            results.Add(new LiveTrend(
                t.Title, t.ApproxTraffic, article, ratio, trajectory, score,
                series ?? Array.Empty<long>(), t.RelatedNews,
                $"https://stock.adobe.com/search?k={Uri.EscapeDataString(q)}",
                $"https://www.freepik.com/search?query={Uri.EscapeDataString(q)}&type=vector",
                advice, relevance));

            if (results.Count >= take) break;
        }

        return results.OrderByDescending(r => r.OpportunityScore).ToList();
    }
}
