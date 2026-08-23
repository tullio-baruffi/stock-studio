using System.Xml.Linq;

namespace StockStudio.Api.Services;

/// <summary>One entry from Google's "trending now" feed.</summary>
public record TrendingTopic(string Title, long ApproxTraffic, string? PictureSource, IReadOnlyList<string> RelatedNews);

/// <summary>
/// Reads Google Trends' public "trending now" RSS feed — the only Google Trends surface reachable
/// without the private widget-token flow (the JSON endpoints return 404/403 here).
/// Gives what people are searching for RIGHT NOW, per country.
/// </summary>
public class GoogleTrendsClient
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GoogleTrendsClient> _log;
    private readonly Dictionary<string, (DateTime at, IReadOnlyList<TrendingTopic> topics)> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public GoogleTrendsClient(IHttpClientFactory http, ILogger<GoogleTrendsClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<IReadOnlyList<TrendingTopic>> GetTrendingAsync(string geo, CancellationToken ct)
    {
        geo = string.IsNullOrWhiteSpace(geo) ? "US" : geo.ToUpperInvariant();

        lock (_cache)
        {
            if (_cache.TryGetValue(geo, out var hit) && DateTime.UtcNow - hit.at < Ttl)
                return hit.topics;
        }

        var topics = new List<TrendingTopic>();
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; StockVectorStudio/1.0)");

            using var res = await client.GetAsync($"https://trends.google.com/trending/rss?geo={geo}", ct);
            if (res.IsSuccessStatusCode)
            {
                var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                XNamespace ht = "https://trends.google.com/trending/rss";

                foreach (var item in xml.Descendants("item"))
                {
                    var title = item.Element("title")?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    var trafficRaw = item.Element(ht + "approx_traffic")?.Value ?? "0";
                    long traffic = ParseTraffic(trafficRaw);

                    var news = item.Elements(ht + "news_item")
                        .Select(n => n.Element(ht + "news_item_title")?.Value ?? "")
                        .Where(s => s.Length > 0)
                        .Take(3)
                        .ToList();

                    topics.Add(new TrendingTopic(title, traffic, item.Element(ht + "picture")?.Value, news));
                }
            }
            else
            {
                _log.LogWarning("Google Trends RSS {Geo} -> {Status}", geo, (int)res.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Google Trends RSS non disponibile per {Geo}", geo);
        }

        lock (_cache) _cache[geo] = (DateTime.UtcNow, topics);
        return topics;
    }

    /// <summary>"2000+" / "1,000+" -> 2000 / 1000.</summary>
    private static long ParseTraffic(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var n) ? n : 0;
    }
}
