using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using StockStudio.Api.Domain;
using StockStudio.Api.Models;
using StockStudio.Api.Services;
using StockStudio.Api.Services.Feedback;
using StockStudio.Api.Services.Integration;

namespace StockStudio.Api.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private readonly PipelineService _pipeline;
    private readonly IJobStore _store;
    private readonly CsvExporter _csv;
    private readonly StockPipelineDispatcher _dispatcher;
    private readonly JobQueue _queue;
    private readonly StockValidator _validator;
    private readonly MetadataFeedbackStore _feedback;
    private readonly PipelineHandoff _handoff;

    public JobsController(PipelineService pipeline, IJobStore store, CsvExporter csv, StockPipelineDispatcher dispatcher,
                          JobQueue queue, StockValidator validator, MetadataFeedbackStore feedback,
                          PipelineHandoff handoff)
    {
        _pipeline = pipeline;
        _store = store;
        _csv = csv;
        _dispatcher = dispatcher;
        _queue = queue;
        _validator = validator;
        _feedback = feedback;
        _handoff = handoff;
    }

    [HttpPost]
    [RequestSizeLimit(500_000_000)]
    public async Task<ActionResult<JobDto>> Create([FromForm] List<IFormFile> files, [FromForm] string? mode, CancellationToken ct)
    {
        if (files == null || files.Count == 0) return BadRequest("Nessun file caricato.");

        var inputs = new List<(string, Stream)>();
        foreach (var f in files) inputs.Add((f.FileName, f.OpenReadStream()));

        var job = await _pipeline.CreateJob(inputs, mode ?? "vector", ct);
        await _queue.EnqueueAsync(job.Id, ct);
        return Ok(ToDto(job));
    }

    /// <summary>
    /// Hands the uploaded pictures straight to the durable pipeline: the originals go to blob
    /// storage, one message per picture goes on the queue, and this API is done.
    ///
    /// The difference with the endpoint above is where the work happens. There it runs here, on an
    /// in-memory queue, so the batch stops whenever the site restarts; here it runs in a Function
    /// driven by a storage queue, so the batch survives a restart, a plan change or the site being
    /// switched off entirely. Tracing, classification and delivery all continue without this API.
    /// </summary>
    [HttpPost("handoff")]
    [RequestSizeLimit(500_000_000)]
    public async Task<IActionResult> Handoff([FromForm] List<IFormFile> files, [FromForm] string? mode, CancellationToken ct)
    {
        if (files == null || files.Count == 0) return BadRequest("Nessun file caricato.");
        if (!_handoff.Enabled)
            return BadRequest("Storage della pipeline non configurato (Pipeline:StorageConnectionString).");

        var accepted = new List<object>();
        var rejected = new List<object>();

        foreach (var f in files)
        {
            try
            {
                await using var stream = f.OpenReadStream();
                var r = await _handoff.HandOffAsync(f.FileName, stream, mode ?? "vector", ct);
                accepted.Add(new { file = r.OriginalFileName, blob = r.BlobName });
            }
            catch (Exception ex)
            {
                // One bad file must not sink the whole batch: the rest is already on its way.
                rejected.Add(new { file = f.FileName, error = ex.Message });
            }
        }

        return Ok(new
        {
            ok = rejected.Count == 0,
            accepted = accepted.Count,
            rejected = rejected.Count,
            items = accepted,
            errors = rejected,
            message = $"{accepted.Count} immagini consegnate alla pipeline. "
                    + "Da qui in poi procede da sola: puoi chiudere o spegnere l'applicazione.",
        });
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<JobSummaryDto>> List([FromQuery] int limit = 50)
    {
        var summaries = _store.List(limit).Select(j =>
        {
            int Count(string s) => j.Items.Count(i => i.Status == s);
            return new JobSummaryDto(
                j.Id, j.CreatedAt.ToString("o"), j.Items.Count,
                Count("queued"), Count("processing"), Count("completed"), Count("failed"), Count("dispatched"), Count("published"));
        }).ToList();
        return Ok(summaries);
    }

    [HttpGet("{id}")]
    public ActionResult<JobDto> Get(string id)
    {
        var job = _store.Get(id);
        return job == null ? NotFound() : Ok(ToDto(job));
    }

    [HttpPost("{id}/items/{itemId}/retry")]
    public async Task<IActionResult> Retry(string id, string itemId, CancellationToken ct)
    {
        var job = _store.Get(id);
        var item = job?.Items.FirstOrDefault(i => i.Id == itemId);
        if (job == null || item == null) return NotFound();
        if (item.Status is not ("failed" or "processing"))
            return BadRequest($"Si possono ritentare solo item falliti o bloccati (status={item.Status}).");

        item.Status = "queued";
        item.Error = null;
        item.QueuedAt = DateTime.UtcNow;
        item.StartedAt = item.VectorizedAt = item.CompletedAt = null;
        _store.Save(job);
        await _queue.EnqueueAsync(job.Id, ct);
        return Ok(ToItemDto(job.Id, item));
    }

    [HttpDelete("{id}")]
    public IActionResult DeleteJob(string id)
    {
        if (_store.Get(id) == null) return NotFound();
        _store.Delete(id);
        return NoContent();
    }

    /// <summary>Re-runs vectorization for one item with an optional threshold override, keeping metadata.</summary>
    [HttpPost("{id}/items/{itemId}/revectorize")]
    public async Task<ActionResult<ItemDto>> Revectorize(string id, string itemId, [FromBody] RevectorizeRequest req, CancellationToken ct)
    {
        var job = _store.Get(id);
        var item = job?.Items.FirstOrDefault(i => i.Id == itemId);
        if (job == null || item == null) return NotFound();
        // Avoid racing the background processor over the same output files.
        if (item.Status is "queued" or "processing")
            return BadRequest("Elaborazione in corso: attendi il completamento prima di rigenerare.");
        if (item.Mode == "raster")
            return BadRequest("Questo job è in modalità immagine: non c'è alcun tracciato da rigenerare.");

        try
        {
            await _pipeline.RevectorizeItemAsync(job, item, req.AutoThreshold, req.Threshold, ct);
            _store.Save(job);
            return Ok(ToItemDto(job.Id, item));
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Ad-hoc validation of arbitrary metadata (used by the create form live).</summary>
    [HttpPost("validate")]
    public ActionResult<ValidationDto> Validate([FromBody] ValidateRequest req)
    {
        var v = _validator.Validate(req.Title ?? "", req.Description ?? "", req.Keywords ?? new List<string>());
        return Ok(new ValidationDto(
            v.Score, v.BlocksDispatch,
            v.Sites.Select(s => new SiteScoreDto(s.Site, s.Score)).ToList(),
            v.Issues.Select(x => new ValidationIssueDto(x.Severity, x.Field, x.Message, x.Site)).ToList()));
    }

    /// <summary>
    /// Saves the reviewed metadata. When the author also writes a note, or simply changes the
    /// generated values, the correction is journalled so the review process can learn from it.
    /// </summary>
    [HttpPut("{id}/items/{itemId}")]
    public ActionResult<ItemDto> UpdateItem(string id, string itemId, [FromBody] UpdateItemRequest req)
    {
        var job = _store.Get(id);
        var item = job?.Items.FirstOrDefault(i => i.Id == itemId);
        if (job == null || item == null) return NotFound();

        if (req.title != null) item.Title = req.title;
        if (req.description != null) item.Description = req.description;
        if (req.keywords != null) item.Keywords = req.keywords;
        if (req.category != null) item.Category = req.category;

        var note = string.IsNullOrWhiteSpace(req.feedback) ? null : req.feedback.Trim();
        if (note != null)
        {
            item.Feedback = note;
            item.FeedbackAt = DateTime.UtcNow;
        }

        // Only journal metadata the author actually reviewed. Values that came back from the
        // pipeline are not the local generator's output, so correcting them teaches nothing
        // about the local prompt.
        if (item.MetadataSource != "pipeline")
        {
            var corrected = new MetadataSnapshot
            {
                Title = item.Title,
                Description = item.Description,
                Keywords = item.Keywords.ToList(),
                Category = item.Category,
            };
            _feedback.Record(job, item, corrected, note);
        }

        _store.Save(job);
        return Ok(ToItemDto(job.Id, item));
    }

    [HttpPost("{id}/dispatch")]
    public async Task<IActionResult> Dispatch(string id, CancellationToken ct)
    {
        var job = _store.Get(id);
        if (job == null) return NotFound();
        if (!_dispatcher.Enabled)
            return BadRequest("Integrazione pipeline disabilitata. Imposta Pipeline:Enabled e le credenziali SharePoint.");

        var results = new List<DispatchItemResult>();
        foreach (var it in job.Items.Where(i => i.Status is "completed" or "dispatched"))
            results.Add(await _dispatcher.DispatchAsync(job, it, ct));

        _store.Save(job);
        return Ok(new { jobId = id, results, job = ToDto(job) });
    }

    [HttpGet("{id}/export/adobe")]
    public IActionResult ExportAdobe(string id)
    {
        var job = _store.Get(id);
        if (job == null) return NotFound();
        return File(Encoding.UTF8.GetBytes(_csv.BuildAdobeStock(job)), "text/csv", $"adobe_{id[..8]}.csv");
    }

    [HttpGet("{id}/export/freepik")]
    public IActionResult ExportFreepik(string id)
    {
        var job = _store.Get(id);
        if (job == null) return NotFound();
        return File(Encoding.UTF8.GetBytes(_csv.BuildFreepik(job)), "text/csv", $"freepik_{id[..8]}.csv");
    }

    [HttpGet("{id}/export/bundle")]
    public IActionResult ExportBundle(string id)
    {
        var job = _store.Get(id);
        if (job == null) return NotFound();

        var jobDir = _store.JobDir(id);
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var it in CsvExporter.Exportable(job))
            {
                foreach (var fn in new[] { it.SvgFile, it.EpsFile, it.JpgFile, it.AiFile })
                {
                    if (fn == null) continue;
                    var path = Path.Combine(jobDir, fn);
                    if (System.IO.File.Exists(path)) zip.CreateEntryFromFile(path, fn);
                }
            }
            AddText(zip, "adobe_stock.csv", _csv.BuildAdobeStock(job));
            AddText(zip, "freepik.csv", _csv.BuildFreepik(job));
        }
        ms.Position = 0;
        return File(ms, "application/zip", $"bundle_{id[..8]}.zip");
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private JobDto ToDto(Job job) =>
        new(job.Id, job.CreatedAt.ToString("o"), job.Mode, job.Items.Select(i => ToItemDto(job.Id, i)).ToList());

    private ItemDto ToItemDto(string jobId, JobItem i)
    {
        string? Url(string? f) => f == null ? null : $"/api/files/{jobId}/{f}";
        var preview = i.SvgFile != null ? Url(i.SvgFile) : Url(i.JpgFile);
        string? Iso(DateTime? t) => t?.ToString("o");
        long? duration = (i.StartedAt != null && i.CompletedAt != null)
            ? (long)(i.CompletedAt.Value - i.StartedAt.Value).TotalMilliseconds
            : null;

        var v = _validator.Validate(i.Title, i.Description, i.Keywords, i.Mode);
        var validation = new ValidationDto(
            v.Score, v.BlocksDispatch,
            v.Sites.Select(s => new SiteScoreDto(s.Site, s.Score)).ToList(),
            v.Issues.Select(x => new ValidationIssueDto(x.Severity, x.Field, x.Message, x.Site)).ToList());

        var edited = i.AiOriginal is not null && (
            !string.Equals(i.AiOriginal.Title, i.Title, StringComparison.Ordinal)
            || !string.Equals(i.AiOriginal.Category, i.Category, StringComparison.OrdinalIgnoreCase)
            || !i.AiOriginal.Keywords.SequenceEqual(i.Keywords, StringComparer.OrdinalIgnoreCase));

        return new ItemDto(
            i.Id, i.OriginalFileName, i.BaseName, i.Status, i.Error,
            i.Title, i.Description, i.Keywords, i.Category,
            new ItemFilesDto(Url(i.SvgFile), Url(i.EpsFile), Url(i.JpgFile), Url(i.AiFile)),
            preview,
            new ItemStepsDto(Iso(i.QueuedAt), Iso(i.StartedAt), Iso(i.VectorizedAt), Iso(i.CompletedAt), Iso(i.DispatchedAt), Iso(i.PublishedAt)),
            duration,
            i.PublishStatus,
            i.PublishMessage,
            i.MetadataSource,
            validation,
            i.Mode,
            i.Feedback,
            edited);
    }
}
