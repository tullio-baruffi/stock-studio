namespace StockStudio.Api.Services;

public record MarketEvent(
    string Name,
    string Category,
    int Month,
    int Day,
    string[] Keywords,
    int DemandWeight,     // fallback weight, used only when live data is unavailable
    string WikiArticle,   // English Wikipedia article used to measure real public interest
    string Note = "");

public record TopicSuggestion(
    string Name,
    string Category,
    string PeakDate,
    int DaysUntilPeak,
    bool InSubmissionWindow,
    int OpportunityScore,
    string[] Keywords,
    string AdobeSearchUrl,
    string FreepikSearchUrl,
    string Advice,
    string DemandSource,          // "live" (Wikipedia pageviews) or "fallback"
    double? SeasonalityRatio,     // how strongly the topic spikes at its peak
    double? Momentum,             // >1 = interest rising right now
    long? PeakViews,
    int? MeasuredPeakMonth);

/// <summary>
/// Curated market/trend engine. Stock sites want seasonal content 30-90 days BEFORE the event,
/// so this ranks upcoming events by an opportunity score derived from demand + how well the
/// current date sits inside the ideal submission window. Live saturation (supply) is not scrapeable
/// (Adobe/Freepik return 403), so each suggestion links the real search + accepts a manual count.
/// </summary>
public class TrendService
{
    // Ideal window: submit between LeadMax and LeadMin days before the event.
    private const int LeadMax = 90;
    private const int LeadMin = 20;

    private static readonly MarketEvent[] Events =
    {
        new("Capodanno / New Year", "Festività", 1, 1, new[] { "new year", "fireworks", "celebration", "2027", "party", "countdown" }, 9, "New_Year"),
        new("San Valentino", "Festività", 2, 14, new[] { "valentine", "love", "heart", "romantic", "couple", "roses" }, 10, "Valentine's_Day"),
        new("Carnevale", "Festività", 2, 17, new[] { "carnival", "mask", "costume", "party", "venice", "confetti" }, 6, "Carnival"),
        new("Festa della Donna", "Ricorrenza", 3, 8, new[] { "womens day", "mimosa", "woman", "empowerment", "flower" }, 7, "International_Women's_Day"),
        new("San Patrizio", "Festività", 3, 17, new[] { "st patrick", "shamrock", "irish", "green", "clover", "leprechaun" }, 7, "Saint_Patrick's_Day"),
        new("Pasqua", "Festività", 4, 5, new[] { "easter", "egg", "bunny", "spring", "rabbit", "chick" }, 9, "Easter"),
        new("Festa della Terra", "Ricorrenza", 4, 22, new[] { "earth day", "eco", "green", "planet", "sustainability", "recycle" }, 7, "Earth_Day"),
        new("Festa della Mamma", "Festività", 5, 11, new[] { "mothers day", "mom", "family", "love", "flowers", "gift" }, 9, "Mother's_Day"),
        new("Festa del Papà (US)", "Festività", 6, 15, new[] { "fathers day", "dad", "family", "gift", "celebration" }, 8, "Father's_Day"),
        new("Estate / Vacanze", "Stagione", 7, 1, new[] { "summer", "beach", "vacation", "sun", "travel", "holiday" }, 9, "Summer"),
        new("Rientro a scuola", "Stagione", 9, 1, new[] { "back to school", "education", "student", "supplies", "classroom" }, 9, "Back_to_school"),
        new("Halloween", "Festività", 10, 31, new[] { "halloween", "pumpkin", "ghost", "spooky", "witch", "skeleton", "bat" }, 10, "Halloween"),
        new("Ringraziamento", "Festività", 11, 26, new[] { "thanksgiving", "turkey", "autumn", "harvest", "family", "gratitude" }, 8, "Thanksgiving"),
        new("Black Friday", "Commerciale", 11, 27, new[] { "black friday", "sale", "discount", "shopping", "offer", "promotion" }, 10, "Black_Friday"),
        new("Natale", "Festività", 12, 25, new[] { "christmas", "santa", "xmas", "tree", "gift", "snow", "reindeer" }, 10, "Christmas"),
        new("Inverno / Neve", "Stagione", 12, 15, new[] { "winter", "snow", "snowflake", "cold", "cozy", "sweater" }, 7, "Winter"),
    };

    private readonly WikipediaTrendClient _wiki;

    public TrendService(WikipediaTrendClient wiki) => _wiki = wiki;

    public async Task<IReadOnlyList<TopicSuggestion>> SuggestAsync(DateTime nowUtc, int horizonDays, string style, CancellationToken ct)
    {
        var suggestions = new List<TopicSuggestion>();
        var today = nowUtc.Date;

        // Fetch live interest for all candidate topics in parallel (results are cached for a day).
        var candidates = Events
            .Select(e => new { e, date = NextOccurrence(today, e.Month, e.Day) })
            .Where(x => (x.date - today).Days <= horizonDays)
            .ToList();

        var signals = new Dictionary<string, InterestSignal?>();
        await Task.WhenAll(candidates.Select(async c =>
        {
            var s = await _wiki.GetAsync(c.e.WikiArticle, ct);
            lock (signals) signals[c.e.WikiArticle] = s;
        }));

        foreach (var c in candidates)
        {
            var e = c.e;
            var date = c.date;
            int daysUntil = (date - today).Days;
            bool inWindow = daysUntil is <= LeadMax and >= LeadMin;

            signals.TryGetValue(e.WikiArticle, out var sig);

            // Demand: measured from real public interest when available, else the curated weight.
            // Seasonality ratio (peak/average) tells how much a topic actually spikes — a 5x spike
            // is a far stronger commercial signal than a flat, evergreen topic.
            double demand;
            string source;
            if (sig != null && sig.SeasonalityRatio > 0)
            {
                // ~1x (flat) -> 5, 3x -> 8.3, >=5x -> 10
                demand = Math.Clamp(4 + (sig.SeasonalityRatio - 1) * 1.6, 3, 10);
                // Rising interest right now is a bonus, fading interest a penalty.
                demand *= Math.Clamp(sig.Momentum, 0.85, 1.15);
                source = "live";
            }
            else
            {
                demand = e.DemandWeight;
                source = "fallback";
            }

            double windowFactor = inWindow ? 1.0 : daysUntil < LeadMin ? 0.45 : 0.7;
            int score = Math.Clamp((int)Math.Round(demand * 10 * windowFactor), 0, 100);

            var kw = e.Keywords;
            var q = string.IsNullOrWhiteSpace(style) ? kw[0] : $"{kw[0]} {style}";

            var advice = inWindow
                ? $"Finestra ottimale: crea ora, mancano {daysUntil} giorni."
                : daysUntil < LeadMin
                    ? $"In ritardo ({daysUntil} gg): valuta di puntare all'anno prossimo o a un tema meno saturo."
                    : $"Ancora presto ({daysUntil} gg): pianifica, la finestra ideale è 20-90 giorni prima.";

            if (sig != null && sig.Momentum >= 1.15)
                advice += " L'interesse è in forte crescita in questo momento.";

            suggestions.Add(new TopicSuggestion(
                e.Name, e.Category, date.ToString("yyyy-MM-dd"), daysUntil, inWindow, score, kw,
                $"https://stock.adobe.com/search?k={Uri.EscapeDataString(q)}",
                $"https://www.freepik.com/search?query={Uri.EscapeDataString(q)}&type=vector",
                advice,
                source,
                sig?.SeasonalityRatio,
                sig?.Momentum,
                sig?.PeakViews,
                sig?.PeakMonth));
        }

        return suggestions.OrderByDescending(s => s.OpportunityScore).ThenBy(s => s.DaysUntilPeak).ToList();
    }

    private static DateTime NextOccurrence(DateTime today, int month, int day)
    {
        var d = new DateTime(today.Year, month, Math.Min(day, DateTime.DaysInMonth(today.Year, month)));
        return d < today ? d.AddYears(1) : d;
    }
}
