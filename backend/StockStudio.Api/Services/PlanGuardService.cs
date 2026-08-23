using Microsoft.Extensions.Options;

namespace StockStudio.Api.Services;

/// <summary>
/// Watches the free tier's daily CPU allowance and climbs to a paid level before it runs out.
///
/// On F1 the ceiling that actually bites is not the cold start, it is the 60 minutes of CPU per
/// day: once they are gone App Service answers 403 to everything until midnight, which reads like
/// an application bug rather than an exhausted quota. Changing SKU restarts the site, so the climb
/// is tied to quota pressure — the one moment where a few seconds of restart clearly beat a whole
/// day of 403 — and never to an incoming upload, which the restart would kill mid-request.
///
/// The descent stays with the evening Logic App: nothing here ever lowers the level.
/// </summary>
public class PlanGuardService : BackgroundService
{
    private readonly AppServicePlanScaler _scaler;
    private readonly PlanOptions _o;
    private readonly ILogger<PlanGuardService> _log;

    public PlanGuardService(AppServicePlanScaler scaler, IOptions<PlanOptions> o, ILogger<PlanGuardService> log)
    {
        _scaler = scaler;
        _o = o.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_o.IsConfigured || !_o.AutoScaleUp)
        {
            _log.LogInformation("Guardia del piano non attiva (configurato={Cfg}, autoScaleUp={Auto})",
                _o.IsConfigured, _o.AutoScaleUp);
            return;
        }

        var every = TimeSpan.FromMinutes(Math.Clamp(_o.CheckMinutes, 1, 120));
        _log.LogInformation("Guardia del piano attiva: controllo ogni {Min} minuti, soglia {Pct}% della quota CPU",
            every.TotalMinutes, _o.CpuThresholdPercent);

        // A first look right away would race the site's own start-up, when the quota reading is
        // still settling; one interval of grace avoids a pointless restart at boot.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(every, stoppingToken);
                await CheckOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Controllo della quota non riuscito");
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        var state = await _scaler.GetStateAsync(ct);
        if (state.Error != null || state.Sku == null) return;

        // Only the free tier has a quota to run out of.
        if (!string.Equals(state.Sku, "F1", StringComparison.OrdinalIgnoreCase)) return;
        if (state.CpuPercent is not int pct) return;
        if (pct < _o.CpuThresholdPercent) return;

        _log.LogWarning("Quota CPU al {Pct}% ({Used}s su {Quota}s): salgo a {Sku} prima dei 403",
            pct, state.CpuUsedSeconds, state.CpuQuotaSeconds, _o.UpSku);

        var after = await _scaler.ScaleAsync(_o.UpSku, ct);
        _log.LogWarning("Piano ora su {Sku}. Torna a F1 stasera con api-plan-autoscaledown-001.", after.Sku);
    }
}
