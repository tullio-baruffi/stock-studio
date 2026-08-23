using Microsoft.Extensions.Options;
using StockStudio.Api.Domain;
using StockStudio.Shared.Contracts;

namespace StockStudio.Api.Services.Integration;
public record DispatchItemResult(string ItemId, bool Ok, string? ServerRelativeUrl, bool Enqueued, string? Error);

/// <summary>
/// Hands a completed job item off to the existing Azure pipeline:
/// drops original + vector deliverables into SharePoint, then enqueues the classify message
/// (or relies on a SharePoint-triggered Logic App when no Storage connection is set).
/// </summary>
public class StockPipelineDispatcher
{
    private readonly PipelineSettings _s;
    private readonly SharePointStore _sp;
    private readonly QueueDispatcher _queue;
    private readonly IJobStore _store;
    private readonly StockValidator _validator;
    private readonly ILogger<StockPipelineDispatcher> _log;

    public StockPipelineDispatcher(
        IOptions<PipelineSettings> s, SharePointStore sp, QueueDispatcher queue,
        IJobStore store, StockValidator validator, ILogger<StockPipelineDispatcher> log)
    {
        _s = s.Value;
        _sp = sp;
        _queue = queue;
        _store = store;
        _validator = validator;
        _log = log;
    }

    public bool Enabled => _s.Enabled;

    public async Task<DispatchItemResult> DispatchAsync(Job job, JobItem item, CancellationToken ct)
    {
        if (!_s.Enabled)
            return new DispatchItemResult(item.Id, false, null, false, "Pipeline disabilitata (Pipeline:Enabled=false).");
        if (item.Status is not ("completed" or "dispatched" or "published"))
            return new DispatchItemResult(item.Id, false, null, false, $"Item non pronto (status={item.Status}).");
        if (string.IsNullOrWhiteSpace(_s.LibraryFolder))
            return new DispatchItemResult(item.Id, false, null, false, "Pipeline:LibraryFolder mancante.");

        // Never publish metadata that the stock sites would reject (trademarks, missing/oversized fields).
        var validation = _validator.Validate(item.Title, item.Description, item.Keywords);
        if (validation.BlocksDispatch)
        {
            var reasons = string.Join(" ", validation.Issues.Where(i => i.Severity == "error").Select(i => i.Message));
            return new DispatchItemResult(item.Id, false, null, false, $"Bloccato dal controllo qualità: {reasons}");
        }

        try
        {
            var jobDir = _store.JobDir(job.Id);
            var folder = $"{_s.LibraryFolder!.TrimEnd('/')}/{item.BaseName}";

            // What actually goes to the marketplaces depends on the job: a raster sells as a JPG,
            // a vector sells as EPS and SVG. A fixed setting still wins when one is configured.
            var wanted = ResolveDispatchKinds(item.Mode);

            // Upload every available deliverable + the original into the SharePoint back-office.
            var dispatched = new List<SharePointUploadResult>();

            foreach (var (kind, fileName) in EnumerateFiles(item, jobDir))
            {
                var path = Path.Combine(jobDir, fileName);
                var originalDir = Path.Combine(jobDir, "original");
                if (kind == "original")
                    path = Directory.Exists(originalDir)
                        ? Directory.GetFiles(originalDir, item.BaseName + ".*").FirstOrDefault() ?? path
                        : path;

                if (!File.Exists(path)) continue;

                // Disambiguate the uploaded name when another item already owns it, so the pipeline
                // callback can never resolve back to the wrong image.
                var uploadName = UniqueUploadName(Path.GetFileName(path), item.Id);

                await using var fs = File.OpenRead(path);
                var res = _sp.UploadFile(fs, uploadName, folder);
                if (wanted.Contains(kind)) dispatched.Add(res);
            }

            if (dispatched.Count == 0)
                return new DispatchItemResult(item.Id, false, null, false,
                    $"Nessun file di dispatch trovato per la modalità '{item.Mode}' (attesi: {string.Join(", ", wanted)}).");

            foreach (var upload in dispatched)
            {
                // The metadata are written here, not left to the Logic App: it classifies by sending
                // the file to a vision model, which cannot read an EPS or an SVG. Writing them at
                // dispatch keeps vector deliverables complete regardless of the classification.
                TryWriteMetadata(upload, item);

                var msg = new ResizeQueueMessage
                {
                    ServerRelativeUrl = upload.ServerRelativeUrl,
                    BlobName = upload.FileName,
                    IdSharePoint = upload.ItemId.ToString(),
                    PathBlob = $"images/{upload.FileName}",
                };
                await _queue.EnqueueAsync(msg, ct);
                _store.IndexDispatch(upload.FileName, job.Id, item.Id);
            }

            item.Status = "dispatched";
            item.DispatchedAt = DateTime.UtcNow;
            return new DispatchItemResult(item.Id, true, dispatched[0].ServerRelativeUrl, _queue.CanEnqueue, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Dispatch fallito per item {Item}", item.Id);
            return new DispatchItemResult(item.Id, false, null, false, ex.Message);
        }
    }

    /// <summary>
    /// Deliverables to send onward for a given job mode. Vector jobs go out as EPS *and* SVG, the
    /// formats Adobe Stock and Freepik want; raster jobs as JPG. An explicit Pipeline:DispatchFile
    /// overrides the rule for every job.
    /// </summary>
    private IReadOnlyCollection<string> ResolveDispatchKinds(string mode)
    {
        var configured = (_s.DispatchFile ?? "auto").Trim().ToLowerInvariant();
        if (configured is not ("auto" or ""))
            return configured.Split(',', StringSplitOptions.RemoveEmptyEntries)
                             .Select(s => s.Trim())
                             .Where(s => s.Length > 0)
                             .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return string.Equals(mode, "raster", StringComparison.OrdinalIgnoreCase)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "jpg" }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "eps", "svg" };
    }

    /// <summary>
    /// Copies the reviewed metadata onto the SharePoint item. Best effort: a failure here must not
    /// undo an upload that already succeeded.
    /// </summary>
    private void TryWriteMetadata(SharePointUploadResult upload, JobItem item)
    {
        try
        {
            var library = _s.LibraryFolder!.TrimEnd('/').Split('/').Last();
            _sp.UpdateItem(library, upload.ItemId, item.Title, item.Description, string.Join(", ", item.Keywords));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Metadati non scritti su SharePoint per {File}", upload.FileName);
        }
    }

    /// <summary>
    /// Returns a file name that is unique across the whole workspace: keeps the original name unless
    /// another item already dispatched it, in which case it appends a short item-id suffix.
    /// </summary>
    private string UniqueUploadName(string fileName, string itemId)
    {
        var owner = _store.ResolveDispatch(fileName);
        if (owner == null || owner.Value.itemId == itemId) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        return $"{stem}_{itemId[..6]}{ext}";
    }

    private static IEnumerable<(string kind, string fileName)> EnumerateFiles(JobItem item, string jobDir)    {
        if (item.SvgFile != null) yield return ("svg", item.SvgFile);
        if (item.EpsFile != null) yield return ("eps", item.EpsFile);
        if (item.JpgFile != null) yield return ("jpg", item.JpgFile);
        if (item.AiFile != null) yield return ("ai", item.AiFile);
        yield return ("original", item.BaseName);
    }
}
