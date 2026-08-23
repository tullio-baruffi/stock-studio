using Microsoft.AspNetCore.Mvc;
using StockStudio.Api.Services;

namespace StockStudio.Api.Controllers;

/// <summary>
/// Control over the API's own App Service plan. The evening Logic App only brings the level down,
/// so this is the other half: see where the plan is, and move it up deliberately before a heavy
/// batch instead of discovering the free tier's CPU quota half-way through one.
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    private readonly AppServicePlanScaler _scaler;
    private readonly ILogger<SystemController> _log;

    public SystemController(AppServicePlanScaler scaler, ILogger<SystemController> log)
    {
        _scaler = scaler;
        _log = log;
    }

    [HttpGet("plan")]
    public async Task<IActionResult> Plan(CancellationToken ct)
    {
        var s = await _scaler.GetStateAsync(ct);
        return Ok(new
        {
            ok = s.Error == null,
            configured = s.Configured,
            sku = s.Sku,
            tier = s.Tier,
            alwaysOn = s.AlwaysOn,
            cpuUsedSeconds = s.CpuUsedSeconds,
            cpuQuotaSeconds = s.CpuQuotaSeconds,
            cpuPercent = s.CpuPercent,
            quotaAssumed = s.QuotaAssumed,
            autoScaleUp = _scaler.Options.AutoScaleUp,
            upSku = _scaler.Options.UpSku,
            thresholdPercent = _scaler.Options.CpuThresholdPercent,
            error = s.Error,
        });
    }

    /// <summary>
    /// Changes the level. Azure recycles the site to move it between workers, so the caller loses
    /// this connection for a few seconds: that is why it is an explicit action, never a side effect
    /// of an upload.
    /// </summary>
    [HttpPost("plan")]
    public async Task<IActionResult> SetPlan([FromQuery] string sku, CancellationToken ct)
    {
        try
        {
            var s = await _scaler.ScaleAsync(sku, ct);
            _log.LogWarning("Livello del piano richiesto manualmente: {Sku}", sku);
            return Ok(new { ok = true, sku = s.Sku, tier = s.Tier, alwaysOn = s.AlwaysOn });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cambio di livello non riuscito verso {Sku}", sku);
            return Ok(new { ok = false, error = ex.Message });
        }
    }
}
