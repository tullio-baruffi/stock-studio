using Microsoft.AspNetCore.Mvc;
using StockStudio.Api.Domain;
using StockStudio.Api.Services;

namespace StockStudio.Api.Controllers;

/// <summary>Aggregate KPIs across recent jobs for the analytics dashboard.</summary>
[ApiController]
[Route("api/monitor")]
public class MonitorController : ControllerBase
{
    private readonly IJobStore _store;

    public MonitorController(IJobStore store) => _store = store;

    /// <summary>Flattened list of items in a given set of statuses (e.g. dispatched/published) — "my images in the pipeline".</summary>
    [HttpGet("items")]
    public IActionResult Items([FromQuery] string statuses = "dispatched,published", [FromQuery] int scan = 200)
    {
        var wanted = statuses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = _store.List(scan)
            .SelectMany(j => j.Items.Select(i => new { job = j, item = i }))
            .Where(x => wanted.Contains(x.item.Status) ||
                        (x.item.PublishStatus != null && wanted.Contains(x.item.PublishStatus)))
            .OrderByDescending(x => x.item.PublishedAt ?? x.item.DispatchedAt ?? x.job.CreatedAt)
            .Take(200)
            .Select(x => new
            {
                jobId = x.job.Id,
                itemId = x.item.Id,
                baseName = x.item.BaseName,
                title = x.item.Title,
                status = x.item.Status,
                publishStatus = x.item.PublishStatus,
                metadataSource = x.item.MetadataSource,
                dispatchedAt = x.item.DispatchedAt?.ToString("o"),
                publishedAt = x.item.PublishedAt?.ToString("o"),
                trackName = (x.item.JpgFile ?? x.item.BaseName + ".jpg"),
            })
            .ToList();

        return Ok(rows);
    }

    [HttpGet("overview")]    public IActionResult Overview([FromQuery] int scan = 200)
    {
        var jobs = _store.List(scan);
        var items = jobs.SelectMany(j => j.Items).ToList();

        var nowUtc = DateTime.UtcNow;
        var todayStart = nowUtc.Date;
        var weekStart = nowUtc.Date.AddDays(-6);

        object Window(IEnumerable<JobItem> src)
        {
            var list = src.ToList();
            return new
            {
                items = list.Count,
                completed = list.Count(i => i.Status is "completed" or "dispatched" or "published"),
                failed = list.Count(i => i.Status == "failed"),
            };
        }

        int Count(string s) => items.Count(i => i.Status == s);
        int completedOrDispatched = items.Count(i => i.Status is "completed" or "dispatched" or "published");
        int terminal = completedOrDispatched + Count("failed");

        var durations = items
            .Where(i => i.StartedAt != null && i.CompletedAt != null)
            .Select(i => (i.CompletedAt!.Value - i.StartedAt!.Value).TotalMilliseconds)
            .ToList();

        return Ok(new
        {
            totals = new
            {
                jobs = jobs.Count,
                items = items.Count,
                queued = Count("queued"),
                processing = Count("processing"),
                completed = Count("completed"),
                failed = Count("failed"),
                dispatched = Count("dispatched"),
                published = Count("published"),
            },
            today = Window(items.Where(i => (i.QueuedAt ?? DateTime.MinValue) >= todayStart)),
            week = Window(items.Where(i => (i.QueuedAt ?? DateTime.MinValue) >= weekStart)),
            successRate = terminal == 0 ? (double?)null : Math.Round((double)completedOrDispatched / terminal, 3),
            avgDurationMs = durations.Count == 0 ? (double?)null : Math.Round(durations.Average()),
            scanned = jobs.Count,
        });
    }
}
