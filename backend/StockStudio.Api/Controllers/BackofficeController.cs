using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Microsoft.Extensions.Options;
using StockStudio.Api.Services;
using StockStudio.Api.Services.Integration;

namespace StockStudio.Api.Controllers;

public record UpdateSpItemRequest(string? title, string? description, string? tags);
public record MoveSpItemRequest(string targetLibrary);
public record RegenerateRequest(List<int> ids);

/// <summary>Outcome of one re-description, with the previous title so the change is visible.</summary>
public record RegenerateResult(
    bool ok,
    int id,
    string fileName,
    string? error = null,
    string? provider = null,
    string? previousTitle = null,
    int previousTitleLength = 0,
    int previousKeywords = 0,
    object? item = null);

/// <summary>
/// SharePoint back-office: browse, review and approve the pipeline's files without opening
/// SharePoint. The libraries are the pipeline stages, so only those three are addressable.
/// </summary>
[ApiController]
[Route("api/backoffice")]
public class BackofficeController : ControllerBase
{
    /// <summary>The pipeline stages, in order. Anything else is refused.</summary>
    private static readonly Dictionary<string, string> Stages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ImagesToClassify"] = "In attesa AI / da revisionare",
        ["ImagesToSend"] = "Revisionati, pronti per l'invio",
        ["ImagesSent"] = "Pubblicati",
    };

    private readonly SharePointStore _sp;
    private readonly PipelineSettings _s;
    private readonly StockValidator _validator;
    private readonly IMetadataProvider _metadata;
    private readonly ILogger<BackofficeController> _log;

    public BackofficeController(SharePointStore sp, IOptions<PipelineSettings> s,
                                StockValidator validator, IMetadataProvider metadata,
                                ILogger<BackofficeController> log)
    {
        _sp = sp;
        _s = s.Value;
        _validator = validator;
        _metadata = metadata;
        _log = log;
    }

    private string SiteRoot
    {
        get
        {
            var u = new Uri(_s.SiteUrl!);
            return u.AbsolutePath.TrimEnd('/');
        }
    }

    private ActionResult? Guard(string library)
    {
        if (!_s.Enabled) return BadRequest("Pipeline disabilitata.");
        if (!Stages.ContainsKey(library))
            return BadRequest($"Libreria non gestita: '{library}'. Ammesse: {string.Join(", ", Stages.Keys)}.");
        return null;
    }

    [HttpGet("stages")]
    public IActionResult GetStages() =>
        Ok(Stages.Select(s => new { library = s.Key, label = s.Value }));

    /// <summary>
    /// Indexes the columns the back-office filters on. Without an index SharePoint refuses any
    /// filtered query on a library past 5000 items, which is what blocked search on ImagesSent.
    /// </summary>
    [HttpPost("index")]
    public IActionResult EnsureIndexes([FromQuery] string? library = null)
    {
        if (!_s.Enabled) return BadRequest("Pipeline disabilitata.");

        var targets = string.IsNullOrWhiteSpace(library) ? Stages.Keys.ToArray() : new[] { library };
        var results = new List<object>();
        foreach (var lib in targets)
        {
            if (!Stages.ContainsKey(lib)) { results.Add(new { library = lib, ok = false, message = "Libreria non gestita." }); continue; }
            foreach (var field in new[] { "FileLeafRef", "Title", "Stato" })
            {
                try { results.Add(new { library = lib, field, ok = true, message = _sp.EnsureIndexed(lib, field) }); }
                catch (Exception ex) { results.Add(new { library = lib, field, ok = false, message = ex.Message }); }
            }
        }
        return Ok(new { ok = true, results });
    }

    /// <summary>One page of a stage, with the Adobe/Freepik score computed for each item.</summary>
    [HttpGet("items")]
    public IActionResult Items([FromQuery] string library = "ImagesToClassify",
                               [FromQuery] int take = 24,
                               [FromQuery] string? pageToken = null,
                               [FromQuery] string? search = null,
                               [FromQuery] string? field = null)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            var page = _sp.ListItems(library, take, pageToken, search, field);
            var items = page.Items.Select(i =>
            {
                var kw = SplitTags(i.Tags);
                var v = _validator.Validate(i.Title, i.Description, kw, "vector");
                return new
                {
                    i.Id,
                    fileName = i.FileName,
                    i.Title,
                    i.Description,
                    keywords = kw,
                    stato = i.Stato,
                    i.Invia,
                    i.Inviato,
                    checkedOutBy = i.CheckedOutBy,
                    modified = i.Modified,
                    previewUrl = $"/api/backoffice/file?w=480&url={Uri.EscapeDataString(i.ServerRelativeUrl)}",
                    validation = new
                    {
                        score = v.Score,
                        blocksDispatch = v.BlocksDispatch,
                        issues = v.Issues.Select(x => new { x.Severity, x.Field, x.Message }),
                    },
                };
            }).ToList();

            return Ok(new { ok = true, library, label = Stages[library], items, nextPageToken = page.NextPageToken });
        }
        catch (Exception ex) when (IsThresholdError(ex))
        {
            // SharePoint refuses to filter a library past 5000 items on a column without an index.
            // The raw message says nothing about what the author can do, so translate it.
            _log.LogWarning(ex, "Soglia elenco superata su {Library} con filtro '{Search}'", library, search);
            return Ok(new
            {
                ok = false,
                error = $"La libreria {library} supera i 5.000 elementi: SharePoint rifiuta questa ricerca perché " +
                        "la colonna non è indicizzabile (il nome file è un campo di sistema). Cerca per Titolo " +
                        "o per Stato, che sono indicizzati, oppure sfoglia con le pagine.",
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Lettura libreria {Library} non riuscita", library);
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    private static bool IsThresholdError(Exception ex) =>
        ex.Message.Contains("soglia", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("threshold", StringComparison.OrdinalIgnoreCase);

    [HttpGet("items/{id:int}")]
    public IActionResult Item(int id, [FromQuery] string library)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try { return Ok(new { ok = true, item = Shape(_sp.GetItem(library, id)) }); }
        catch (Exception ex) { return Ok(new { ok = false, error = ex.Message }); }
    }

    [HttpPut("items/{id:int}")]
    public IActionResult Update(int id, [FromQuery] string library, [FromBody] UpdateSpItemRequest req)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            var updated = _sp.UpdateItem(library, id, req.title, req.description, req.tags);
            return Ok(new { ok = true, item = Shape(updated) });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Releases a file left checked out in SharePoint. Until this happens SharePoint refuses every
    /// metadata write on it, so the review, the Invia flag and the Logic App all fail on that file.
    /// </summary>
    [HttpPost("items/{id:int}/checkin")]
    public IActionResult CheckIn(int id, [FromQuery] string library, [FromQuery] bool discard = false)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            var r = _sp.ReleaseCheckOut(library, id, discard);
            _log.LogInformation("Check-in {Library}/{Id}: {Message}", library, id, r.Message);
            return Ok(new { ok = true, changed = r.Changed, message = r.Message, item = Shape(r.Item) });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Check-in non riuscito per {Library}/{Id}", library, id);
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Flips the "Invia" flag the invia-to-sftp Logic App watches, which starts the real upload.
    /// Refused when the metadata would be rejected by the marketplaces: better to stop here than
    /// to have the asset bounce back from Adobe Stock.
    /// </summary>
    [HttpPost("items/{id:int}/send")]
    public IActionResult Send(int id, [FromQuery] string library = "ImagesToSend",
                              [FromQuery] bool value = true, [FromQuery] bool force = false)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        if (!string.Equals(library, "ImagesToSend", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Il flag Invia esiste solo su ImagesToSend: sposta prima l'elemento.");

        try
        {
            if (value && !force)
            {
                var target = _sp.GetItem(library, id);
                var v = _validator.Validate(target.Title, target.Description, SplitTags(target.Tags), "vector");
                if (v.BlocksDispatch)
                    return Ok(new
                    {
                        ok = false,
                        blocked = true,
                        error = "I metadati non superano la validazione: correggili o forza l'invio.",
                        issues = v.Issues.Where(x => x.Severity == "error").Select(x => x.Message),
                    });
            }

            var updated = _sp.SetInvia(library, id, value);
            return Ok(new { ok = true, item = Shape(updated) });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Moves a reviewed file to the next stage — the hand-off previously done by hand.</summary>
    [HttpPost("items/{id:int}/move")]
    public IActionResult Move(int id, [FromQuery] string library, [FromBody] MoveSpItemRequest req)
    {
        var bad = Guard(library);
        if (bad != null) return bad;
        if (!Stages.ContainsKey(req.targetLibrary))
            return BadRequest($"Libreria di destinazione non gestita: '{req.targetLibrary}'.");

        try
        {
            var target = _sp.GetItem(library, id);
            var moved = _sp.MoveFile(target.ServerRelativeUrl, $"{SiteRoot}/{req.targetLibrary}");
            return Ok(new { ok = true, movedTo = moved });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    [HttpDelete("items/{id:int}")]
    public IActionResult Delete(int id, [FromQuery] string library)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            _sp.DeleteItem(library, id);
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Re-describes a file already in the libraries, using the current metadata prompt: the
    /// Adobe-guide rules plus whatever the review process learned from the author's corrections.
    /// Files described by the older Logic App prompt carry 150-character titles, which this fixes.
    /// </summary>
    [HttpPost("items/{id:int}/regenerate")]
    public async Task<IActionResult> Regenerate(int id, [FromQuery] string library, CancellationToken ct)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            var result = await RegenerateOneAsync(library, id, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rigenerazione non riuscita per {Library}/{Id}", library, id);
            return Ok(new RegenerateResult(false, id, "", ex.Message));
        }
    }

    /// <summary>
    /// Same, for the items currently on screen. Sequential on purpose: these are paid vision calls
    /// and firing 24 of them at once would hit the provider's rate limit.
    /// </summary>
    [HttpPost("regenerate")]
    public async Task<IActionResult> RegenerateMany([FromQuery] string library, [FromBody] RegenerateRequest req,
                                                    CancellationToken ct)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        var ids = (req.ids ?? new List<int>()).Distinct().Take(30).ToList();
        if (ids.Count == 0) return BadRequest("Nessun elemento indicato.");

        var results = new List<RegenerateResult>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                results.Add(await RegenerateOneAsync(library, id, ct));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Rigenerazione saltata per {Library}/{Id}", library, id);
                results.Add(new RegenerateResult(false, id, "", ex.Message));
            }
        }

        return Ok(new
        {
            ok = true,
            requested = ids.Count,
            succeeded = results.Count(r => r.ok),
            results,
        });
    }

    private async Task<RegenerateResult> RegenerateOneAsync(string library, int id, CancellationToken ct)
    {
        var item = _sp.GetItem(library, id);

        // Once the pipeline has taken the file, rewriting SharePoint metadata would not reach the
        // marketplaces: the EXIF was already written from the values in force at that moment.
        if (item.Inviato)
            return new RegenerateResult(false, id, item.FileName, "Già inviato: i metadati non verrebbero più usati.");

        // Checked out means the write at the end would be refused. Stopping here also saves the
        // paid vision call that would otherwise be thrown away.
        if (item.CheckedOutBy.Length > 0)
            return new RegenerateResult(false, id, item.FileName,
                $"File estratto da {item.CheckedOutBy}: SharePoint rifiuterebbe la riscrittura. Archivialo e riprova.");

        var bytes = _sp.DownloadFile(item.ServerRelativeUrl);
        var baseName = Path.GetFileNameWithoutExtension(item.FileName);
        var md = await _metadata.GenerateFromBytesAsync(bytes, baseName, ct);

        var updated = _sp.UpdateItem(library, id, md.Title, md.Description, string.Join(", ", md.Keywords));
        _log.LogInformation("Metadati rigenerati per {File} ({Provider})", item.FileName, _metadata.Name);

        return new RegenerateResult(
            true, id, item.FileName,
            provider: _metadata.Name,
            previousTitle: item.Title,
            previousTitleLength: item.Title.Length,
            previousKeywords: SplitTags(item.Tags).Count,
            item: Shape(updated));
    }

    /// <summary>Streams a library file so previews render inside the app.</summary>
    [HttpGet("file")]
    public IActionResult File([FromQuery] string url, [FromQuery] int? w = null)
    {
        if (!_s.Enabled) return BadRequest("Pipeline disabilitata.");

        // The path must stay inside this site: the parameter reaches CSOM directly.
        var root = SiteRoot;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("..", StringComparison.Ordinal))
            return BadRequest("Percorso non ammesso.");

        try
        {
            var bytes = _sp.DownloadFile(url);
            var ext = Path.GetExtension(url).ToLowerInvariant();
            var mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".svg" => "image/svg+xml",
                ".webp" => "image/webp",
                _ => "application/octet-stream",
            };

            // Every preview costs a full download from SharePoint plus a resize here, and paging
            // back and forth would pay it again for images already on screen. The pixels of a file
            // under review do not change — only its metadata does — so letting the browser keep
            // the thumbnail for an hour removes most of that load outright.
            Response.Headers.CacheControl = "private, max-age=3600";

            // Originals are multi-megabyte. A grid of them would move ~100 MB per page, so the
            // thumbnail is produced here rather than shipping the full file to the browser.
            if (w is > 0 && mime.StartsWith("image/") && mime != "image/svg+xml")
            {
                try
                {
                    using var img = Image.Load(bytes);
                    int edge = Math.Clamp(w.Value, 64, 2000);
                    if (Math.Max(img.Width, img.Height) > edge)
                    {
                        double scale = (double)edge / Math.Max(img.Width, img.Height);
                        img.Mutate(x => x.Resize(Math.Max(1, (int)(img.Width * scale)),
                                                 Math.Max(1, (int)(img.Height * scale))));
                    }
                    using var ms = new MemoryStream();
                    img.SaveAsJpeg(ms, new JpegEncoder { Quality = 80 });
                    return File(ms.ToArray(), "image/jpeg");
                }
                catch (Exception ex)
                {
                    // Not a decodable image (EPS/AI live in these libraries too): serve it untouched.
                    _log.LogDebug(ex, "Ridimensionamento non applicabile a {Url}", url);
                }
            }

            return File(bytes, mime);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Anteprima non disponibile per {Url}", url);
            return NotFound();
        }
    }

    private static List<string> SplitTags(string tags) =>
        (tags ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

    private object Shape(SharePointItem i)
    {
        var kw = SplitTags(i.Tags);
        var v = _validator.Validate(i.Title, i.Description, kw, "vector");
        return new
        {
            i.Id,
            fileName = i.FileName,
            i.Title,
            i.Description,
            keywords = kw,
            stato = i.Stato,
            i.Invia,
            i.Inviato,
            checkedOutBy = i.CheckedOutBy,
            modified = i.Modified,
            previewUrl = $"/api/backoffice/file?w=480&url={Uri.EscapeDataString(i.ServerRelativeUrl)}",
            validation = new
            {
                score = v.Score,
                blocksDispatch = v.BlocksDispatch,
                issues = v.Issues.Select(x => new { x.Severity, x.Field, x.Message }),
            },
        };
    }
}
