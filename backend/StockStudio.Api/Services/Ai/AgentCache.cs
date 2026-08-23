using System.Text.Json;

namespace StockStudio.Api.Services.Ai;

/// <summary>An answer produced earlier, with the moment it was produced.</summary>
public record CachedAnswer<T>(T Value, DateTime GeneratedAt);

/// <summary>
/// Disk-backed cache for agent answers.
///
/// A model call costs money and takes seconds, and — unlike the deterministic engine — it returns
/// something slightly different every time. Caching keeps the list stable through the day, which is
/// what an author planning work actually wants, while the explicit "rigenera" action stays available.
/// Surviving restarts also means a redeploy doesn't silently re-bill every open tab.
/// </summary>
public class AgentCache
{
    private readonly ILogger<AgentCache> _log;
    private readonly string _dir;
    private readonly TimeSpan _ttl;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AgentCache(IConfiguration cfg, ILogger<AgentCache> log)
    {
        _log = log;
        _dir = Path.Combine(AppContext.BaseDirectory, "agent-cache");
        var hours = double.TryParse(cfg["Ai:CacheHours"], out var h) && h > 0 ? h : 12;
        _ttl = TimeSpan.FromHours(hours);
    }

    public TimeSpan Ttl => _ttl;

    private string PathFor(string key)
    {
        var safe = string.Concat(key.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));
        if (safe.Length > 90) safe = safe[..90];
        return Path.Combine(_dir, $"{safe}-{Hash(key)}.json");
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

    public CachedAnswer<T>? Get<T>(string key)
    {
        try
        {
            var file = PathFor(key);
            if (!System.IO.File.Exists(file)) return null;

            var entry = JsonSerializer.Deserialize<CachedAnswer<T>>(System.IO.File.ReadAllText(file), Json);
            if (entry is null) return null;

            if (DateTime.UtcNow - entry.GeneratedAt > _ttl)
            {
                System.IO.File.Delete(file);
                return null;
            }
            return entry;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cache agente non leggibile per {Key}", key);
            return null;
        }
    }

    /// <summary>
    /// Stores an answer. The caller passes the timestamp it will also report to the client, so the
    /// fresh response and every later cache hit agree on when the answer was produced.
    /// </summary>
    public void Set<T>(string key, T value, DateTime generatedAt)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var entry = new CachedAnswer<T>(value, generatedAt);
            System.IO.File.WriteAllText(PathFor(key), JsonSerializer.Serialize(entry, Json));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cache agente non scrivibile per {Key}", key);
        }
    }

    public void Invalidate(string key)
    {
        try
        {
            var file = PathFor(key);
            if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cache agente non invalidabile per {Key}", key);
        }
    }

    /// <summary>Drops every cached agent answer — used by the "rigenera tutto" action.</summary>
    public int Clear()
    {
        try
        {
            if (!Directory.Exists(_dir)) return 0;
            var files = Directory.GetFiles(_dir, "*.json");
            foreach (var f in files) System.IO.File.Delete(f);
            return files.Length;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Impossibile svuotare la cache dell'agente");
            return 0;
        }
    }
}
