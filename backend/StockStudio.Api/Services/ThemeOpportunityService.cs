namespace StockStudio.Api.Services;

/// <summary>One theme ranked by measured public demand, with everything needed to start creating.</summary>
public record ThemeOpportunity(
    string Name,
    string Category,
    int OpportunityScore,
    string Timing,                 // now | soon | plan | evergreen | off-season
    string Advice,
    long AverageViews,             // baseline monthly interest
    double? SeasonalityRatio,      // peak / average — how hard the theme spikes
    int? PeakMonth,                // month of the yearly peak
    int? DaysToPeak,               // days until that peak comes round again
    bool InSubmissionWindow,       // inside the 20-90 day pre-peak sweet spot
    double? Momentum,              // latest month vs 3 months earlier
    double? TrendRatio,            // last 7 days vs previous 7
    string Trajectory,             // rising | peaking | fading | unknown
    IReadOnlyList<long> Series,    // 15-day daily pageviews
    IReadOnlyList<string> HotNow,  // currently-trending topics that map onto this theme
    IReadOnlyList<string> Concepts,
    IReadOnlyList<string> Prompts,
    string AdobeSearchUrl,
    string FreepikSearchUrl,
    string WikiArticle);

/// <summary>
/// The generalized opportunity engine.
///
/// Instead of taking whatever is trending and discarding the ~70% that can never become stock art,
/// this starts from the catalogue of themes that are sellable by construction and measures the real
/// demand for each one: yearly seasonality, current momentum and short-term trajectory from
/// Wikipedia pageviews. Live trending topics are folded in as a *signal* on a theme ("baseball is
/// hot right now because X is trending"), never as the unit of analysis.
/// </summary>
public class ThemeOpportunityService
{
    // Stock sites reward content published between 90 and 20 days before a peak.
    private const int LeadMax = 90;
    private const int LeadMin = 20;

    private readonly WikipediaTrendClient _wiki;
    private readonly GoogleTrendsClient _google;
    private readonly ILogger<ThemeOpportunityService> _log;

    public ThemeOpportunityService(WikipediaTrendClient wiki, GoogleTrendsClient google,
                                   ILogger<ThemeOpportunityService> log)
    {
        _wiki = wiki;
        _google = google;
        _log = log;
    }

    public async Task<IReadOnlyList<ThemeOpportunity>> RankAsync(
        DateTime nowUtc, string style, string? category, string geo, bool withTrajectory, CancellationToken ct)
    {
        var themes = StockThemeCatalog.All
            .Where(t => string.IsNullOrWhiteSpace(category) ||
                        t.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hotByTheme = await MapTrendingToThemesAsync(geo, ct);

        // Phase 1 — the yearly signal for every theme: this is what drives the ranking.
        var signals = new Dictionary<string, InterestSignal?>();
        foreach (var theme in themes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                signals[theme.Name] = await _wiki.GetAsync(theme.WikiArticle, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Segnale non disponibile per il tema {Theme}", theme.Name);
                signals[theme.Name] = null;
            }
        }

        var ranked = themes
            .Select(t => (Theme: t, Signal: signals.GetValueOrDefault(t.Name),
                          Hot: hotByTheme.GetValueOrDefault(t.Name) ?? new List<string>()))
            .Select(x => (x.Theme, x.Signal, x.Hot, Pre: Score(x.Signal, x.Hot, "unknown", nowUtc).Score))
            .OrderByDescending(x => x.Pre)
            .ToList();

        // Phase 2 — the 15-day trajectory costs a second call per theme, so it is spent only on the
        // candidates that could actually be worth acting on today.
        var trajectories = new Dictionary<string, (IReadOnlyList<long> Series, double? Ratio, string Label)>();
        if (withTrajectory)
        {
            foreach (var x in ranked.Take(TrajectoryDepth))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var daily = await _wiki.GetDailySeriesAsync(x.Theme.WikiArticle, 15, ct);
                    if (daily is { Count: >= 14 })
                    {
                        double prev = daily.Take(7).Average();
                        double last = daily.Skip(daily.Count - 7).Average();
                        if (prev > 0)
                        {
                            var ratio = Math.Round(last / prev, 2);
                            var label = ratio >= 1.15 ? "rising" : ratio >= 0.92 ? "peaking" : "fading";
                            trajectories[x.Theme.Name] = (daily, ratio, label);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Traiettoria non disponibile per il tema {Theme}", x.Theme.Name);
                }
            }
        }

        var results = ranked.Select(x =>
        {
            var tr = trajectories.GetValueOrDefault(x.Theme.Name);
            return Build(x.Theme, x.Signal, tr.Series ?? Array.Empty<long>(), tr.Ratio,
                         tr.Label ?? "unknown", x.Hot, style, nowUtc);
        });

        return results.OrderByDescending(r => r.OpportunityScore).ToList();
    }

    private const int TrajectoryDepth = 14;

    private record Scored(int Score, bool Seasonal, int? DaysToPeak, bool InWindow);

    private static Scored Score(InterestSignal? signal, IReadOnlyList<string> hot, string trajectory, DateTime nowUtc)
    {
        int? peakMonth = signal?.PeakMonth is > 0 ? signal.PeakMonth : null;
        double seasonality = signal?.SeasonalityRatio ?? 1;
        long avg = signal?.AverageViews ?? 0;

        int? daysToPeak = null;
        if (peakMonth is int pm)
        {
            // Peaks repeat yearly: measure to the 15th of the peak month, next occurrence.
            var next = new DateTime(nowUtc.Year, pm, 15);
            if (next < nowUtc.Date) next = next.AddYears(1);
            daysToPeak = (int)(next - nowUtc.Date).TotalDays;
        }

        // A theme is "seasonal" only if it genuinely spikes; otherwise timing is irrelevant.
        bool seasonal = seasonality >= 1.6;
        bool inWindow = seasonal && daysToPeak is >= LeadMin and <= LeadMax;

        // Without a measurement there is nothing to rank: say so rather than invent a score.
        if (signal is null)
            return new Scored(hot.Count > 0 ? 20 : 0, false, null, false);

        // Demand: baseline audience, compressed so a huge article doesn't dwarf everything else.
        double demand = Math.Clamp(Math.Log10(Math.Max(avg, 100)) / 6.5, 0, 1) * 45;

        // Timing: only rewards themes that actually have a season, and only near the sweet spot.
        double timing = 0;
        if (seasonal && daysToPeak is int d)
        {
            double strength = Math.Clamp((seasonality - 1.6) / 6.0, 0, 1);      // how sharp the spike is
            double closeness =
                d >= LeadMin && d <= LeadMax ? 1                                 // inside the window
                : d < LeadMin ? 0.35                                             // too late to publish well
                : d <= LeadMax + 60 ? 0.55                                       // approaching, start planning
                : 0.1;                                                           // far off
            timing = strength * closeness * 30;

            // Being inside the publish window is the whole point of the tool: it decides what to
            // create today, so it outranks a slightly bigger audience that peaks months from now.
            if (closeness >= 1) timing += 10;
        }

        double momentumScore = 0;
        if (signal.Momentum is double m)
            momentumScore = Math.Clamp((m - 0.8) / 1.2, 0, 1) * 12;

        double trajectoryScore = trajectory switch
        {
            "rising" => 8,
            "peaking" => 5,
            "fading" => 1,
            _ => 3,
        };

        // Something trending right now maps onto this theme: a real, time-limited opening.
        double hotScore = hot.Count > 0 ? Math.Min(10, 4 + hot.Count * 2) : 0;

        int score = (int)Math.Round(Math.Clamp(demand + timing + momentumScore + trajectoryScore + hotScore, 0, 100));
        return new Scored(score, seasonal, daysToPeak, inWindow);
    }

    private ThemeOpportunity Build(StockTheme theme, InterestSignal? signal, IReadOnlyList<long> series,
                                   double? trendRatio, string trajectory, IReadOnlyList<string> hot,
                                   string style, DateTime nowUtc)
    {
        var s = Score(signal, hot, trajectory, nowUtc);
        var (timingLabel, advice) = signal is null
            ? ("nodata", "Dato di interesse non disponibile in questo momento (Wikipedia ha limitato le richieste). Riprova fra poco: il valore viene messo in cache e poi resta immediato.")
            : Describe(s.Seasonal, s.DaysToPeak, s.InWindow, trajectory, hot, signal.SeasonalityRatio);

        var q = string.IsNullOrWhiteSpace(style) ? theme.Name : $"{theme.Name} {style}";
        return new ThemeOpportunity(
            theme.Name, theme.Category, s.Score, timingLabel, advice,
            signal?.AverageViews ?? 0,
            signal is null ? null : Math.Round(signal.SeasonalityRatio, 2),
            signal?.PeakMonth is > 0 ? signal.PeakMonth : null,
            s.DaysToPeak, s.InWindow,
            signal?.Momentum, trendRatio, trajectory, series, hot,
            theme.Subjects, StockThemeCatalog.BuildPrompts(theme, style),
            $"https://stock.adobe.com/search?k={Uri.EscapeDataString(q)}",
            $"https://www.freepik.com/search?query={Uri.EscapeDataString(q)}&type=vector",
            theme.WikiArticle);
    }

    private static (string Timing, string Advice) Describe(
        bool seasonal, int? daysToPeak, bool inWindow, string trajectory,
        IReadOnlyList<string> hot, double seasonality)
    {
        if (hot.Count > 0)
            return ("now", $"Caldo adesso: {hot.Count} tema/i di tendenza ricadono qui ({string.Join(", ", hot.Take(2))}). Finestra breve, conviene muoversi subito.");

        if (seasonal && daysToPeak is int d)
        {
            if (inWindow)
                return ("now", $"Momento ideale: il picco arriva fra {d} giorni ed è la finestra in cui i siti stock premiano chi pubblica.");
            if (d < LeadMin)
                return ("late", $"Picco fra soli {d} giorni: probabilmente tardi per indicizzare bene, ma resta valido per l'anno prossimo.");
            if (d <= LeadMax + 60)
                return ("soon", $"Si avvicina: picco fra {d} giorni, inizia a produrre così sei pronto quando si apre la finestra ({LeadMax}-{LeadMin} giorni prima).");
            return ("plan", $"Fuori stagione: picco fra {d} giorni. Da mettere in calendario, non è la priorità di oggi.");
        }

        var trend = trajectory switch
        {
            "rising" => " L'interesse è in crescita in questi giorni.",
            "fading" => " L'interesse sta calando leggermente.",
            _ => "",
        };
        return ("evergreen",
            $"Tema sempreverde (varia poco durante l'anno, {seasonality:0.0}x): vende tutto l'anno, nessuna fretta.{trend}");
    }

    /// <summary>
    /// Folds what is trending right now onto the themes it belongs to, so a hot news cycle becomes
    /// a signal on "baseball" rather than an unusable suggestion to draw a specific player.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> MapTrendingToThemesAsync(string geo, CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>();
        try
        {
            var trending = await _google.GetTrendingAsync(geo, ct);
            if (trending.Count == 0) return map;

            // Only the phrase is inspected here: matching a theme's own vocabulary is enough to
            // attribute the buzz, and it costs no extra Wikipedia calls.
            foreach (var t in trending.Take(25))
            {
                var haystack = $"{t.Title} {string.Join(" ", t.RelatedNews.Take(2))}";
                foreach (var theme in StockThemeCatalog.All)
                {
                    bool hit = theme.Keys.Concat(theme.Brands)
                        .Any(k => k.Length >= 4 && StockRelevanceService.Mentions(haystack, k));
                    if (!hit) continue;

                    if (!map.TryGetValue(theme.Name, out var list))
                        map[theme.Name] = list = new List<string>();
                    if (list.Count < 4 && !list.Contains(t.Title)) list.Add(t.Title);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Segnale 'caldo adesso' non disponibile");
        }
        return map;
    }
}
