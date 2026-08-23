using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StockStudio.Api.Services;

public record NewsHeadline(string Title, string Source, DateTime? Published, string Link);

/// <summary>
/// A theme inferred from real-world news that is expected to drive demand later, with the
/// evidence (headlines) it was derived from.
/// </summary>
public record PredictedTrend(
    string Topic,
    string Category,
    int? TargetYear,
    int Mentions,
    double Confidence,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<NewsHeadline> Evidence,
    StockRelevance? Relevance = null);

/// <summary>
/// Reads public news RSS (Google News / BBC) and mines it for themes that will matter in the
/// near future — scheduled events, anniversaries, launches — so the user can create stock content
/// BEFORE demand peaks. Purely evidence-based: every prediction carries the headlines behind it.
/// </summary>
public class NewsTrendClient
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NewsTrendClient> _log;
    private readonly Dictionary<string, (DateTime at, IReadOnlyList<NewsHeadline> items)> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

    // Themes worth scanning for: each is a search phrase + the stock keywords it maps to.
    private static readonly (string Query, string Category, string[] Keywords)[] Probes =
    {
        ("world cup", "Sport", new[] { "world cup", "football", "soccer", "trophy", "stadium", "championship" }),
        ("olympics", "Sport", new[] { "olympics", "athlete", "medal", "torch", "sport", "competition" }),
        ("eclipse", "Scienza", new[] { "eclipse", "sun", "moon", "astronomy", "sky", "space" }),
        ("space mission launch", "Scienza", new[] { "space", "rocket", "astronaut", "planet", "satellite", "launch" }),
        ("election", "Attualità", new[] { "election", "vote", "ballot", "democracy", "politics", "campaign" }),
        ("festival anniversary", "Cultura", new[] { "festival", "anniversary", "celebration", "music", "crowd", "stage" }),
        ("artificial intelligence", "Tecnologia", new[] { "artificial intelligence", "ai", "robot", "technology", "future", "digital" }),
        ("climate summit", "Ambiente", new[] { "climate", "environment", "sustainability", "green", "earth", "energy" }),
    };

    public NewsTrendClient(IHttpClientFactory http, ILogger<NewsTrendClient> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Top world headlines — the general pulse of what is happening now.</summary>
    public Task<IReadOnlyList<NewsHeadline>> GetWorldHeadlinesAsync(CancellationToken ct) =>
        FetchAsync("https://news.google.com/rss/headlines/section/topic/WORLD?hl=en-US&gl=US&ceid=US:en", ct);

    /// <summary>Free-text news search — the evidence tool the agent calls to test a hunch.</summary>
    public Task<IReadOnlyList<NewsHeadline>> SearchAsync(string query, CancellationToken ct) =>
        FetchAsync("https://news.google.com/rss/search?q=" + Uri.EscapeDataString(query) +
                   "&hl=en-US&gl=US&ceid=US:en", ct);

    /// <summary>
    /// Mines news for forward-looking themes. Looks ahead <paramref name="yearsAhead"/> years and
    /// keeps only themes whose headlines actually reference a future year.
    /// </summary>
    public async Task<IReadOnlyList<PredictedTrend>> PredictAsync(int yearsAhead, CancellationToken ct) =>
        await PredictAsync(yearsAhead, null, "silhouette", ct);

    public async Task<IReadOnlyList<PredictedTrend>> PredictAsync(int yearsAhead,
        StockRelevanceService? relevance, string promptStyle, CancellationToken ct)
    {
        int thisYear = DateTime.UtcNow.Year;
        var futureYears = Enumerable.Range(thisYear, Math.Max(1, yearsAhead) + 1).ToArray();

        var results = new List<PredictedTrend>();

        await Task.WhenAll(Probes.Select(async probe =>
        {
            // Bias the query towards the future so we surface planning coverage, not retrospectives.
            var query = $"{probe.Query} {string.Join(" OR ", futureYears.Skip(1))}";
            var url = "https://news.google.com/rss/search?q=" + Uri.EscapeDataString(query) +
                      "&hl=en-US&gl=US&ceid=US:en";

            var headlines = await FetchAsync(url, ct);
            if (headlines.Count == 0) return;

            // Keep only headlines that actually mention a future year.
            var relevant = headlines
                .Where(h => futureYears.Skip(1).Any(y => h.Title.Contains(y.ToString())))
                .ToList();
            if (relevant.Count == 0) return;

            // Most-referenced future year wins.
            var yearCounts = new Dictionary<int, int>();
            foreach (var h in relevant)
                foreach (var y in futureYears.Skip(1))
                    if (h.Title.Contains(y.ToString()))
                        yearCounts[y] = yearCounts.GetValueOrDefault(y) + 1;

            int? targetYear = yearCounts.Count > 0 ? yearCounts.OrderByDescending(k => k.Value).First().Key : null;

            // Confidence: volume of forward-looking coverage, saturating around 20 headlines.
            double confidence = Math.Round(Math.Min(1.0, relevant.Count / 20.0), 2);

            var topic = ToTitleCase(probe.Query);
            lock (results)
            {
                results.Add(new PredictedTrend(
                    topic, probe.Category, targetYear, relevant.Count, confidence,
                    probe.Keywords, relevant.Take(4).ToList()));
            }
        }));

        var ordered = results
            .OrderByDescending(r => r.Confidence)
            .ThenByDescending(r => r.Mentions)
            .ToList();

        // Attach drawable concepts so a prediction turns straight into image prompts.
        if (relevance != null)
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                var rel = await relevance.ClassifyAsync(ordered[i].Topic, null, promptStyle, ct);
                ordered[i] = ordered[i] with { Relevance = rel };
            }
        }

        return ordered;
    }
    private async Task<IReadOnlyList<NewsHeadline>> FetchAsync(string url, CancellationToken ct)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.at < Ttl)
                return hit.items;
        }

        var list = new List<NewsHeadline>();
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; StockVectorStudio/1.0)");

            using var res = await client.GetAsync(url, ct);
            if (res.IsSuccessStatusCode)
            {
                var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                foreach (var item in xml.Descendants("item").Take(40))
                {
                    var title = item.Element("title")?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    DateTime? pub = DateTime.TryParse(item.Element("pubDate")?.Value, out var d) ? d.ToUniversalTime() : null;
                    var source = item.Element("source")?.Value
                                 ?? (title.Contains(" - ") ? title[(title.LastIndexOf(" - ") + 3)..] : "");

                    list.Add(new NewsHeadline(
                        Regex.Replace(title, @"\s+-\s+[^-]+$", ""),  // strip the trailing " - Source"
                        source.Trim(),
                        pub,
                        item.Element("link")?.Value ?? ""));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Feed notizie non disponibile: {Url}", url);
        }

        lock (_cache) _cache[url] = (DateTime.UtcNow, list);
        return list;
    }

    private static string ToTitleCase(string s) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);
}
