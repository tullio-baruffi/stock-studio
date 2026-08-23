namespace StockStudio.Api.Services;

/// <summary>
/// Self-scaling of the API's own App Service plan. The evening Logic App
/// (api-plan-autoscaledown-001) only ever brings the plan back down to F1, so without this the
/// climb back up depended on somebody remembering to run api-plan.ps1.
/// </summary>
public class PlanOptions
{
    /// <summary>Off by default: a local run has no managed identity and no plan to scale.</summary>
    public bool Enabled { get; set; }

    public string? SubscriptionId { get; set; }
    public string? ResourceGroup { get; set; }

    /// <summary>The serverfarm whose SKU is changed.</summary>
    public string? PlanName { get; set; }

    /// <summary>The site whose CPU quota is watched. On F1 the quota is what actually bites.</summary>
    public string? SiteName { get; set; }

    /// <summary>Level to climb to when the free quota runs short.</summary>
    public string UpSku { get; set; } = "B1";

    /// <summary>
    /// Percentage of the daily F1 CPU quota that triggers the climb. Below 100 on purpose: once the
    /// quota is spent the site answers 403 for the rest of the day, so the move has to happen first.
    /// </summary>
    public int CpuThresholdPercent { get; set; } = 80;

    /// <summary>How often the watchdog looks at the quota.</summary>
    public int CheckMinutes { get; set; } = 10;

    /// <summary>
    /// The free tier's daily CPU allowance, in seconds. ARM reports CpuTime with limit = -1 on this
    /// site — the allowance exists but is simply not exposed — so without a documented fallback the
    /// percentage would always be unknown and the watchdog would never fire. F1 grants 60 minutes.
    /// </summary>
    public int FreeQuotaSeconds { get; set; } = 3600;

    /// <summary>
    /// Automatic climb. Turning it off leaves the manual endpoint working, which is useful when the
    /// restart a SKU change causes would be worse than a slow API.
    /// </summary>
    public bool AutoScaleUp { get; set; } = true;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(SubscriptionId)
        && !string.IsNullOrWhiteSpace(ResourceGroup)
        && !string.IsNullOrWhiteSpace(PlanName)
        && !string.IsNullOrWhiteSpace(SiteName);
}
