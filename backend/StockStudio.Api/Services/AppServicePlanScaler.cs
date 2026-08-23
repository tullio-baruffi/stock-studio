using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace StockStudio.Api.Services;

/// <summary>Current level of the plan plus how much of the free CPU quota today is already gone.</summary>
public record PlanState(
    bool Configured,
    string? Sku,
    string? Tier,
    bool AlwaysOn,
    long CpuUsedSeconds,
    long CpuQuotaSeconds,
    int? CpuPercent,
    bool QuotaAssumed,
    string? Error);

/// <summary>
/// Reads and changes the SKU of the API's own App Service plan through ARM, authenticating with the
/// site's managed identity. Deliberately talks raw ARM over HttpClient: the app already carries
/// Azure.Identity for Key Vault, so this needs no extra dependency.
/// </summary>
public class AppServicePlanScaler
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ApiVersion = "2022-03-01";

    private static readonly Dictionary<string, (string tier, string family)> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["F1"] = ("Free", "F"),
            ["B1"] = ("Basic", "B"),
            ["B2"] = ("Basic", "B"),
            ["B3"] = ("Basic", "B"),
        };

    private readonly PlanOptions _o;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AppServicePlanScaler> _log;
    private readonly TokenCredential _credential = new DefaultAzureCredential();
    private AccessToken _token;

    public AppServicePlanScaler(IOptions<PlanOptions> o, IHttpClientFactory http, ILogger<AppServicePlanScaler> log)
    {
        _o = o.Value;
        _http = http;
        _log = log;
    }

    public PlanOptions Options => _o;

    private string PlanUri =>
        $"https://management.azure.com/subscriptions/{_o.SubscriptionId}/resourceGroups/{_o.ResourceGroup}" +
        $"/providers/Microsoft.Web/serverfarms/{_o.PlanName}?api-version={ApiVersion}";

    private string SiteUri(string suffix) =>
        $"https://management.azure.com/subscriptions/{_o.SubscriptionId}/resourceGroups/{_o.ResourceGroup}" +
        $"/providers/Microsoft.Web/sites/{_o.SiteName}{suffix}?api-version={ApiVersion}";

    private async Task<HttpClient> ClientAsync(CancellationToken ct)
    {
        // Refresh a minute early: a token that expires mid-flight would surface as a bare 401.
        if (_token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(1))
            _token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { ArmScope }), ct);

        var c = _http.CreateClient();
        c.Timeout = TimeSpan.FromSeconds(60);
        c.DefaultRequestHeaders.Authorization = new("Bearer", _token.Token);
        return c;
    }

    /// <summary>Never throws: this feeds a dashboard tile that must not take the page down with it.</summary>
    public async Task<PlanState> GetStateAsync(CancellationToken ct = default)
    {
        if (!_o.IsConfigured)
            return new PlanState(false, null, null, false, 0, 0, null, false, "Autoscale del piano non configurato.");

        try
        {
            var c = await ClientAsync(ct);

            using var planDoc = JsonDocument.Parse(await c.GetStringAsync(PlanUri, ct));
            var sku = planDoc.RootElement.GetProperty("sku");
            var name = sku.TryGetProperty("name", out var n) ? n.GetString() : null;
            var tier = sku.TryGetProperty("tier", out var t) ? t.GetString() : null;

            var (used, reported) = await ReadCpuAsync(c, ct);

            // ARM answers limit = -1 for CpuTime on this site, so the reported quota is unusable.
            // On the free tier the allowance is real all the same, and assuming the documented 60
            // minutes is what makes the watchdog able to act at all — flagged so the UI can say so.
            var free = string.Equals(name, "F1", StringComparison.OrdinalIgnoreCase);
            var assumed = reported <= 0 && free;
            var quota = assumed ? _o.FreeQuotaSeconds : reported;
            var pct = quota > 0 ? (int)Math.Round(100.0 * used / quota) : (int?)null;

            return new PlanState(true, name, tier, await ReadAlwaysOnAsync(c, ct), used, quota, pct, assumed, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Lettura dello stato del piano non riuscita");
            return new PlanState(true, null, null, false, 0, 0, null, false, ex.Message);
        }
    }

    /// <summary>
    /// CPU consumed today and the allowance ARM claims, both in seconds. ARM reports milliseconds,
    /// and returns limit = -1 when it does not expose the allowance at all; that is passed through
    /// as zero so the caller decides what to assume.
    /// </summary>
    private async Task<(long used, long quota)> ReadCpuAsync(HttpClient c, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await c.GetStringAsync(SiteUri("/usages"), ct));
        foreach (var u in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var key = u.TryGetProperty("name", out var nm) && nm.TryGetProperty("value", out var nv)
                ? nv.GetString() : null;
            if (!string.Equals(key, "CpuTime", StringComparison.OrdinalIgnoreCase)) continue;

            var current = u.TryGetProperty("currentValue", out var cv) ? cv.GetInt64() : 0;
            var limit = u.TryGetProperty("limit", out var lv) ? lv.GetInt64() : 0;
            return (current / 1000, limit > 0 ? limit / 1000 : 0);
        }
        return (0, 0);
    }

    private async Task<bool> ReadAlwaysOnAsync(HttpClient c, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await c.GetStringAsync(SiteUri("/config/web"), ct));
            return doc.RootElement.GetProperty("properties").TryGetProperty("alwaysOn", out var a) && a.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Moves the plan to <paramref name="sku"/>. Always On only exists from Basic up, so it is
    /// switched off before going down to F1 and on after climbing — the same order api-plan.ps1
    /// uses, because a plan left on F1 with alwaysOn set is a configuration F1 does not allow.
    /// </summary>
    public async Task<PlanState> ScaleAsync(string sku, CancellationToken ct = default)
    {
        if (!_o.IsConfigured) throw new InvalidOperationException("Autoscale del piano non configurato.");
        if (!Known.TryGetValue(sku, out var shape)) throw new ArgumentException($"Livello non gestito: '{sku}'.");

        var before = await GetStateAsync(ct);
        if (string.Equals(before.Sku, sku, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation("Piano già su {Sku}: nessun cambio", sku);
            return before;
        }

        var c = await ClientAsync(ct);
        var free = string.Equals(sku, "F1", StringComparison.OrdinalIgnoreCase);

        if (free) await SetAlwaysOnAsync(c, false, ct);

        var body = JsonSerializer.Serialize(new
        {
            location = await ReadLocationAsync(c, ct),
            sku = new { name = sku, tier = shape.tier, size = sku, family = shape.family, capacity = 1 },
            properties = new { },
        });

        using var res = await c.PutAsync(PlanUri, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        if (!res.IsSuccessStatusCode)
        {
            var detail = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Cambio di livello rifiutato da Azure ({(int)res.StatusCode}): {detail}");
        }

        if (!free) await SetAlwaysOnAsync(c, true, ct);

        _log.LogWarning("Piano {Plan}: livello portato da {From} a {To}", _o.PlanName, before.Sku ?? "?", sku);
        return await GetStateAsync(ct);
    }

    private async Task<string> ReadLocationAsync(HttpClient c, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await c.GetStringAsync(PlanUri, ct));
        return doc.RootElement.GetProperty("location").GetString() ?? "westeurope";
    }

    private async Task SetAlwaysOnAsync(HttpClient c, bool on, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { properties = new { alwaysOn = on } });
            using var req = new HttpRequestMessage(HttpMethod.Patch, SiteUri("/config/web"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var res = await c.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                _log.LogWarning("Always On non impostato a {On}: {Status}", on, res.StatusCode);
        }
        catch (Exception ex)
        {
            // A failed Always On toggle must not abort the SKU change: the level is what matters.
            _log.LogWarning(ex, "Always On non impostato a {On}", on);
        }
    }
}
