using System.Collections.Concurrent;
using System.Text.Json;

namespace StockStudio.Api.Services;

/// <summary>Live interest signal for one topic, derived from Wikipedia pageviews.</summary>
public record InterestSignal(
    int PeakMonth,
    long PeakViews,
    long AverageViews,
    double SeasonalityRatio,   // peak / average — how strongly the topic spikes
    double Momentum,           // latest month vs 3 months earlier — is interest rising now?
    IReadOnlyList<long> Series);

/// <summary>Wikipedia's short description and lead paragraph for a topic.</summary>
public record WikiSummary(string? Description, string? Extract);

/// <summary>
/// Fetches monthly Wikipedia pageviews as a proxy for real public interest in a topic.
/// Free, keyless and reliable (Google Trends is blocked from this environment). Results are cached
/// in memory for a day: the underlying data is monthly, so more frequent calls add nothing.
/// </summary>
public class WikipediaTrendClient
{
    private const string Endpoint =
        "https://wikimedia.org/api/rest_v1/metrics/pageviews/per-article/en.wikipedia/all-access/all-agents";

    private const string ActionApi = "https://en.wikipedia.org/w/api.php";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<WikipediaTrendClient> _log;
    private readonly ConcurrentDictionary<string, (DateTime at, InterestSignal? sig)> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    // Wikimedia throttles bursts with 429 — the search endpoint especially. Requests are serialised,
    // paced, and retried with growing backoff so a page of trends classifies reliably.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastCall = DateTime.MinValue;
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan[] Backoff =
        { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };

    private async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        // Wikimedia requires a descriptive UA; anonymous calls get throttled.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StockVectorStudio/1.0 (trend analysis)");

        for (int attempt = 0; attempt <= Backoff.Length; attempt++)
        {
            await Gate.WaitAsync(ct);
            try
            {
                var wait = MinGap - (DateTime.UtcNow - _lastCall);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _lastCall = DateTime.UtcNow;
            }
            finally
            {
                Gate.Release();
            }

            var res = await client.GetAsync(url, ct);
            if ((int)res.StatusCode != 429) return res;

            res.Dispose();
            if (attempt < Backoff.Length) await Task.Delay(Backoff[attempt], ct);
        }

        _log.LogWarning("Wikimedia continua a limitare le richieste: {Url}", url);
        return null;
    }

    public WikipediaTrendClient(IHttpClientFactory http, ILogger<WikipediaTrendClient> log)
    {
        _http = http;
        _log = log;
        _cacheDir = Path.Combine(AppContext.BaseDirectory, "wiki-cache");
        LoadFromDisk();
    }

    private readonly string _cacheDir;

    private sealed record DiskEntry(string Key, DateTime At, InterestSignal? Signal);

    private static TimeSpan TtlFor(string key) =>
        key.StartsWith("daily:", StringComparison.Ordinal) ? TimeSpan.FromHours(6) : Ttl;

    private string PathFor(string key)
    {
        // Article names and composite keys contain characters Windows forbids in filenames,
        // so the file name is sanitised and the real key travels inside the payload.
        var safe = string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))
                         .Replace(':', '_');
        if (safe.Length > 100) safe = safe[..100];
        return Path.Combine(_cacheDir, $"{safe}-{Hash(key)}.json");
    }

    private static string Hash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }
    }

    /// <summary>
    /// Pageview data changes at most daily, and Wikimedia rate-limits hard when the whole theme
    /// catalogue is refreshed at once. Persisting it means the cost is paid once, not per restart.
    /// </summary>
    private void LoadFromDisk()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) { Directory.CreateDirectory(_cacheDir); return; }

            foreach (var file in Directory.EnumerateFiles(_cacheDir, "*.json"))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<DiskEntry>(System.IO.File.ReadAllText(file));
                    if (entry?.Key is null || entry.Signal is null) continue;

                    if (DateTime.UtcNow - entry.At < TtlFor(entry.Key))
                        _cache[entry.Key] = (entry.At, entry.Signal);
                    else
                        System.IO.File.Delete(file);
                }
                catch { /* a corrupt entry just means a refetch */ }
            }

            _log.LogInformation("Cache Wikipedia caricata: {Count} voci", _cache.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cache Wikipedia su disco non disponibile");
        }
    }

    private void SaveToDisk(string key, DateTime at, InterestSignal? signal)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            System.IO.File.WriteAllText(PathFor(key), JsonSerializer.Serialize(new DiskEntry(key, at, signal)));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Impossibile persistere la cache per {Key}", key);
        }
    }

    public async Task<InterestSignal?> GetAsync(string article, CancellationToken ct)
    {
        if (_cache.TryGetValue(article, out var hit) && DateTime.UtcNow - hit.at < Ttl)
            return hit.sig;

        InterestSignal? signal = null;
        try
        {
            var end = DateTime.UtcNow.Date;
            var start = end.AddMonths(-14);
            var url = $"{Endpoint}/{Uri.EscapeDataString(article)}/monthly/" +
                      $"{start:yyyyMM}0100/{end:yyyyMM}0100";

            using var res = await SendAsync(url, ct);
            if (res is { IsSuccessStatusCode: true })
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var items = doc.RootElement.GetProperty("items");
                var views = new List<long>();
                var months = new List<int>();
                var nowMonth = DateTime.UtcNow.ToString("yyyyMM");
                foreach (var it in items.EnumerateArray())
                {
                    var ts = it.GetProperty("timestamp").GetString() ?? "";
                    // The current month is still accruing: including it would wreck the momentum ratio.
                    if (ts.Length >= 6 && ts.Substring(0, 6) == nowMonth) continue;

                    views.Add(it.GetProperty("views").GetInt64());
                    months.Add(ts.Length >= 6 ? int.Parse(ts.Substring(4, 2)) : 0);
                }

                if (views.Count >= 4)
                {
                    long peak = views.Max();
                    int peakIdx = views.IndexOf(peak);
                    double avg = views.Average();
                    double momentum = views.Count >= 4 && views[^4] > 0
                        ? (double)views[^1] / views[^4]
                        : 1.0;

                    signal = new InterestSignal(
                        months[peakIdx], peak, (long)avg,
                        avg > 0 ? Math.Round(peak / avg, 2) : 1,
                        Math.Round(momentum, 2),
                        views);
                }
            }
            else
            {
                _log.LogWarning("Wikipedia pageviews {Article} -> {Status}", article,
                    res is null ? "throttled" : ((int)res.StatusCode).ToString());
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Wikipedia pageviews non disponibili per {Article}", article);
        }

        // A throttled or failed call must not be cached for a day — that would leave the theme
        // permanently blank. Only a real measurement is worth remembering.
        if (signal is not null)
        {
            _cache[article] = (DateTime.UtcNow, signal);
            SaveToDisk(article, DateTime.UtcNow, signal);
        }
        return signal;
    }

    /// <summary>
    /// Daily pageviews over the last <paramref name="days"/> complete days — the short-horizon
    /// trajectory used to tell a rising trend from one that already peaked.
    /// </summary>
    public async Task<IReadOnlyList<long>?> GetDailySeriesAsync(string article, int days, CancellationToken ct)
    {
        var key = $"daily:{article}:{days}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.at < TimeSpan.FromHours(6))
            return hit.sig?.Series;

        IReadOnlyList<long>? series = null;
        try
        {
            // Wikimedia lags ~1 day; end two days back so every bucket is complete.
            var end = DateTime.UtcNow.Date.AddDays(-2);
            var start = end.AddDays(-(days - 1));
            var url = $"{Endpoint}/{Uri.EscapeDataString(article)}/daily/{start:yyyyMMdd}/{end:yyyyMMdd}";

            using var res = await SendAsync(url, ct);
            if (res is { IsSuccessStatusCode: true })
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                series = doc.RootElement.GetProperty("items").EnumerateArray()
                    .Select(i => i.GetProperty("views").GetInt64()).ToList();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Serie giornaliera non disponibile per {Article}", article);
        }

        if (series is not null)
        {
            var wrapped = new InterestSignal(0, 0, 0, 0, 0, series);
            _cache[key] = (DateTime.UtcNow, wrapped);
            SaveToDisk(key, DateTime.UtcNow, wrapped);
        }
        return series;
    }

    /// <summary>
    /// Wikipedia's one-line description and lead paragraph — the signal used to tell what a topic
    /// actually *is* (a company, a person, a natural event) before deciding if it can become stock art.
    /// Uses the Action API: the REST summary endpoint rate-limits hard when classifying a whole page
    /// of trends, while this one answers reliably and can batch many titles per call.
    /// </summary>
    public async Task<WikiSummary> GetSummaryAsync(string article, CancellationToken ct)
    {
        if (_summaries.TryGetValue(article, out var hit) && DateTime.UtcNow - hit.at < Ttl)
            return hit.sum;

        var summary = new WikiSummary(null, null);
        try
        {
            var url = ActionApi + "?action=query&format=json&formatversion=2&redirects=1" +
                      "&prop=description|extracts&exintro=1&explaintext=1&exsentences=2" +
                      $"&titles={Uri.EscapeDataString(article.Replace('_', ' '))}";

            using var res = await SendAsync(url, ct);
            if (res is not { IsSuccessStatusCode: true })
            {
                // Don't cache a miss: a later call should still get the chance to classify this topic.
                _log.LogWarning("Wikipedia summary non ottenuto per {Article}", article);
                return summary;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            summary = ReadPage(doc) ?? summary;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Wikipedia summary non disponibile per {Article}", article);
        }

        _summaries[article] = (DateTime.UtcNow, summary);
        return summary;
    }

    /// <summary>
    /// Resolves a phrase to an article AND fetches its description in a single request.
    /// Halves the number of calls when classifying a page of trends.
    /// </summary>
    public async Task<(string? Article, WikiSummary Summary)> LookupAsync(string phrase, CancellationToken ct)
    {
        if (_resolved.TryGetValue(phrase, out var known) &&
            _summaries.TryGetValue(known, out var cached) && DateTime.UtcNow - cached.at < Ttl)
            return (known, cached.sum);

        try
        {
            var url = ActionApi + "?action=query&format=json&formatversion=2" +
                      "&generator=search&gsrlimit=1&prop=description|extracts" +
                      "&exintro=1&explaintext=1&exsentences=2" +
                      $"&gsrsearch={Uri.EscapeDataString(phrase)}";

            using var res = await SendAsync(url, ct);
            if (res is { IsSuccessStatusCode: true })
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("query", out var q) &&
                    q.TryGetProperty("pages", out var pages) && pages.GetArrayLength() > 0)
                {
                    var page = pages[0];
                    var title = page.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var summary = new WikiSummary(
                        page.TryGetProperty("description", out var d) ? d.GetString() : null,
                        page.TryGetProperty("extract", out var e) ? e.GetString() : null);

                    if (title != null)
                    {
                        var article = title.Replace(' ', '_');
                        _resolved[phrase] = article;
                        _summaries[article] = (DateTime.UtcNow, summary);
                        return (article, summary);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Lookup Wikipedia fallito per {Phrase}", phrase);
        }

        return (null, new WikiSummary(null, null));
    }

    /// <summary>
    /// Resolves and describes many phrases with as few requests as possible.
    /// Most trend phrases already match an article title ("china" -> China), so one batched
    /// title lookup covers them; only the leftovers fall back to the heavily rate-limited search.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, (string? Article, WikiSummary Summary)>> LookupBatchAsync(
        IEnumerable<string> phrases, CancellationToken ct)
    {
        var wanted = phrases.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        var result = new Dictionary<string, (string?, WikiSummary)>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>();

        foreach (var p in wanted)
        {
            if (_resolved.TryGetValue(p, out var art) &&
                _summaries.TryGetValue(art, out var c) && DateTime.UtcNow - c.at < Ttl)
                result[p] = (art, c.sum);
            else
                pending.Add(p);
        }

        // Wikipedia accepts up to 50 titles per call; extracts are capped at 20, so chunk by 20.
        foreach (var chunk in Chunk(pending, 20))
        {
            try
            {
                var url = ActionApi + "?action=query&format=json&formatversion=2&redirects=1" +
                          "&prop=description|extracts&exintro=1&explaintext=1&exsentences=2" +
                          $"&titles={Uri.EscapeDataString(string.Join("|", chunk))}";

                using var res = await SendAsync(url, ct);
                if (res is not { IsSuccessStatusCode: true }) continue;

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (!doc.RootElement.TryGetProperty("query", out var q)) continue;

                // The API rewrites what we asked for; follow the trail back to the original phrase.
                var alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "normalized", "redirects" })
                    if (q.TryGetProperty(name, out var maps))
                        foreach (var m in maps.EnumerateArray())
                        {
                            var from = m.GetProperty("from").GetString();
                            var to = m.GetProperty("to").GetString();
                            if (from != null && to != null) alias[to] = alias.GetValueOrDefault(from, from);
                        }

                if (!q.TryGetProperty("pages", out var pages)) continue;
                foreach (var page in pages.EnumerateArray())
                {
                    var title = page.TryGetProperty("title", out var t) ? t.GetString() : null;
                    if (title == null) continue;

                    var original = alias.GetValueOrDefault(title, title);
                    if (page.TryGetProperty("missing", out _)) continue;

                    var summary = new WikiSummary(
                        page.TryGetProperty("description", out var d) ? d.GetString() : null,
                        page.TryGetProperty("extract", out var e) ? e.GetString() : null);

                    var article = title.Replace(' ', '_');
                    _resolved[original] = article;
                    _summaries[article] = (DateTime.UtcNow, summary);
                    result[original] = (article, summary);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Lookup batch Wikipedia fallito");
            }
        }

        // Whatever the title lookup could not match gets one search each.
        foreach (var p in pending.Where(p => !result.ContainsKey(p)))
            result[p] = await LookupAsync(p, ct);

        return result;
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    private static WikiSummary? ReadPage(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("query", out var q) ||
            !q.TryGetProperty("pages", out var pages) || pages.GetArrayLength() == 0)
            return null;

        var page = pages[0];
        if (page.TryGetProperty("missing", out _)) return null;

        return new WikiSummary(
            page.TryGetProperty("description", out var d) ? d.GetString() : null,
            page.TryGetProperty("extract", out var e) ? e.GetString() : null);
    }

    /// <summary>Resolves a free-text trend phrase to the best-matching English Wikipedia article.</summary>
    public async Task<string?> ResolveArticleAsync(string phrase, CancellationToken ct)
    {
        var key = $"resolve:{phrase}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.at < Ttl)
            return hit.sig?.Series is null ? null : _resolved.GetValueOrDefault(phrase);

        string? title = null;
        try
        {
            var url = "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=1" +
                      $"&srsearch={Uri.EscapeDataString(phrase)}";

            using var res = await SendAsync(url, ct);
            if (res is { IsSuccessStatusCode: true })
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var results = doc.RootElement.GetProperty("query").GetProperty("search");
                if (results.GetArrayLength() > 0)
                    title = results[0].GetProperty("title").GetString()?.Replace(' ', '_');
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Risoluzione articolo fallita per {Phrase}", phrase);
        }

        if (title != null) _resolved[phrase] = title;
        _cache[key] = (DateTime.UtcNow, title == null ? null : new InterestSignal(0, 0, 0, 0, 0, Array.Empty<long>()));
        return title;
    }

    private readonly ConcurrentDictionary<string, string> _resolved = new();
    private readonly ConcurrentDictionary<string, (DateTime at, WikiSummary sum)> _summaries = new();
}
