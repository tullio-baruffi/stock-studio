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

            // Una riga per immagine, non per file. Il percorso durevole deposita SVG, EPS e JPEG
            // nella stessa cartella e con lo stesso nome: mostrarli come tre elementi indipendenti
            // fa sembrare tre lavori quello che ne e' uno.
            //
            // Il gruppo e' cartella *e* nome. La sola cartella non basta: nella libreria storica ce
            // ne sono che contengono decine di immagini diverse, e raggruppare per cartella le
            // riduceva tutte a una riga sola -- ventitre' immagini sparite dalla vista, e cancellate
            // insieme alla prima se si fosse premuto Elimina.
            //
            // Il raggruppamento avviene sulla pagina appena letta: un gruppo a cavallo fra due
            // pagine si vedrebbe spezzato, come si vede spezzato oggi. Non peggiora nulla, e
            // rileggere la libreria per ricomporlo costerebbe una query per riga.
            var groups = page.Items
                .GroupBy(i => IsGroupFolder(library, FolderOf(i.ServerRelativeUrl))
                              ? $"{FolderOf(i.ServerRelativeUrl)}|{Path.GetFileNameWithoutExtension(i.FileName)}"
                              : $"solo:{i.Id}",
                         StringComparer.OrdinalIgnoreCase)
                .Select(g => g.ToList())
                .ToList();

            var items = groups.Select(group =>
            {
                // Il portatore e' il raster: e' l'unico che si possa vedere in anteprima e l'unico
                // che un modello sappia descrivere, quindi e' su di lui che agiscono i pulsanti.
                var i = group.FirstOrDefault(x => IsRasterImage(x.FileName)) ?? group[0];
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
                    deliverables = group.Count > 1
                        ? group.OrderBy(d => d.FileName).Select(d => new
                        {
                            d.Id,
                            fileName = d.FileName,
                            kind = Path.GetExtension(d.FileName).TrimStart('.').ToUpperInvariant(),
                            carrier = d.Id == i.Id,
                        }).ToArray()
                        : null,
                    validation = new
                    {
                        score = v.Score,
                        blocksDispatch = v.BlocksDispatch,
                        issues = v.Issues.Select(x => new { x.Severity, x.Field, x.Message }),
                    },
                };
            }).ToList();

            return Ok(new
            {
                ok = true,
                library,
                label = Stages[library],
                items,
                nextPageToken = page.NextPageToken,
                // Quanto e' costato comporre questa pagina. Serve a spiegare una pagina mezza
                // vuota senza doverlo indovinare: righe lette da SharePoint, righe scartate perche'
                // non erano file, e quale delle due strategie di impaginazione ha risposto.
                lettura = new { lette = page.Scanned, scartate = page.Skipped, strategia = page.Strategy },
            });
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

    /// <summary>Cartella che contiene il file, come percorso server-relative.</summary>
    private static string FolderOf(string serverRelativeUrl)
    {
        var slash = serverRelativeUrl.LastIndexOf('/');
        return slash <= 0 ? "" : serverRelativeUrl[..slash];
    }

    /// <summary>
    /// True quando la cartella e' una sottocartella della libreria, cioe' un gruppo di consegna.
    ///
    /// La radice non e' un gruppo: e' dove arrivano le immagini singole del percorso precedente, e
    /// trattarla come tale unirebbe l'intera libreria in una riga sola.
    /// </summary>
    private bool IsGroupFolder(string library, string folder) =>
        folder.Length > 0
        && !string.Equals(folder.TrimEnd('/'), $"{SiteRoot}/{library}", StringComparison.OrdinalIgnoreCase);

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

            // I vettoriali non hanno mai potuto essere descritti: un modello di visione non legge
            // delle curve, e infatti restavano con titolo e keyword vuoti. Ma sono proprio loro il
            // prodotto che si vende, e UploadFileViaSFTP scrive nell'EXIF i valori dell'elemento
            // che sta inviando: partirebbero muti, cioe' invendibili.
            //
            // La propagazione avviene qui, e non appena i metadati vengono generati, perche' questo
            // e' l'ultimo istante in cui sono quelli definitivi: dopo la revisione dell'autore e
            // prima che l'EXIF li fissi. Copiarli prima significherebbe propagare valori che
            // l'autore stava per correggere.
            var group = PropagateToGroup(library, updated, value);

            return Ok(new { ok = true, item = Shape(updated), gruppo = group });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Moves a reviewed image to the next stage — the hand-off previously done by hand.
    ///
    /// L'unita' e' il gruppo di consegna, non il file: il JPEG che si rivede e i vettoriali che si
    /// vendono viaggiano insieme. Spostare il solo JPEG li lascerebbe indietro in una cartella che
    /// nessuno guarda piu', ed e' esattamente quello che succedeva.
    /// </summary>
    [HttpPost("items/{id:int}/move")]
    public IActionResult Move(int id, [FromQuery] string library, [FromBody] MoveSpItemRequest req)
    {
        var bad = Guard(library);
        if (bad != null) return bad;
        if (!Stages.ContainsKey(req.targetLibrary))
            return BadRequest($"Libreria di destinazione non gestita: '{req.targetLibrary}'.");

        try
        {
            var carrier = _sp.GetItem(library, id);
            var siblings = _sp.GetDeliverableSiblings(library, carrier);

            // Il gruppo mantiene la sua cartella anche a destinazione: appiattirlo nella radice
            // farebbe perdere l'unico legame che tiene insieme le consegne di una stessa immagine.
            var target = siblings.Count > 0
                ? $"{SiteRoot}/{req.targetLibrary}/{FolderNameOf(carrier.ServerRelativeUrl)}"
                : $"{SiteRoot}/{req.targetLibrary}";

            var moved = _sp.MoveFile(carrier.ServerRelativeUrl, target);
            var alsoMoved = new List<string>();
            foreach (var s in siblings)
            {
                // Un fratello che non si sposta non deve far fallire lo spostamento gia' avvenuto:
                // meglio riferire cosa e' rimasto indietro che lasciare il gruppo a meta' in silenzio.
                try { alsoMoved.Add(_sp.MoveFile(s.ServerRelativeUrl, target)); }
                catch (Exception ex) { _log.LogWarning(ex, "Consegna non spostata: {File}", s.FileName); }
            }

            return Ok(new
            {
                ok = true,
                movedTo = moved,
                gruppo = alsoMoved.Count + 1,
                nonSpostate = siblings.Count - alsoMoved.Count,
            });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Allinea le altre consegne dell'immagine al file appena marcato: stessi metadati, stesso
    /// flag di invio. Restituisce quante ne ha allineate, incluso il portatore.
    ///
    /// Non solleva: il file principale e' gia' marcato e la sua partenza non deve dipendere dalla
    /// riuscita di questa rifinitura. Un fallimento finisce nei log e nel conteggio, dove si vede.
    /// </summary>
    private int PropagateToGroup(string library, SharePointItem carrier, bool invia)
    {
        var aligned = 1;
        foreach (var s in _sp.GetDeliverableSiblings(library, carrier))
        {
            // Gia' partito: riscriverne i metadati non raggiungerebbe piu' il marketplace, perche'
            // l'EXIF e' stato fissato al momento dell'invio.
            if (s.Inviato) continue;

            try
            {
                if (invia) _sp.UpdateItem(library, s.Id, carrier.Title, carrier.Description, carrier.Tags);
                _sp.SetInvia(library, s.Id, invia);
                aligned++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Consegna non allineata: {File}", s.FileName);
            }
        }

        if (aligned > 1)
            _log.LogInformation("Gruppo di {Count} consegne allineato su {File}", aligned, carrier.FileName);
        return aligned;
    }

    /// <summary>Nome della cartella che contiene il file, vuoto se sta nella radice.</summary>
    private static string FolderNameOf(string serverRelativeUrl)
    {
        var parts = serverRelativeUrl.TrimEnd('/').Split('/');
        return parts.Length >= 2 ? parts[^2] : "";
    }

    /// <summary>
    /// Toglie dalla libreria le cartelle rimaste vuote.
    ///
    /// Manutenzione, non funzionalita': ogni immagine viene depositata in una sottocartella e
    /// quando i file passano allo stadio successivo il contenitore resta indietro. Invisibile nel
    /// Backoffice, ma occupa un posto in ogni pagina che lo attraversa -- ed e' il motivo per cui
    /// sfogliare una libraria piena di residui restituiva una riga per volta.
    /// </summary>
    [HttpPost("tidy")]
    public IActionResult Tidy([FromQuery] string library, [FromQuery] int max = 500)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            var (removed, inspected) = _sp.RemoveEmptyFolders(library, max);
            return Ok(new
            {
                ok = true,
                rimosse = removed,
                esaminate = inspected,
                messaggio = removed == 0
                    ? "Nessuna cartella vuota da rimuovere."
                    : $"{removed} cartelle vuote rimosse su {inspected} esaminate. "
                      + "Se erano molte, ripeti: se ne esamina un blocco per volta.",
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pulizia cartelle non riuscita su {Library}", library);
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Elimina l'immagine, cioe' tutte le sue consegne.
    ///
    /// Una riga del Backoffice rappresenta un gruppo: cancellare il solo portatore lascerebbe SVG
    /// ed EPS in una cartella che nessuno guarda piu' -- lo stesso orfanaggio che lo spostamento
    /// produceva prima.
    /// </summary>
    [HttpDelete("items/{id:int}")]
    public IActionResult Delete(int id, [FromQuery] string library)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            var carrier = _sp.GetItem(library, id);
            var siblings = _sp.GetDeliverableSiblings(library, carrier);
            var folder = FolderOf(carrier.ServerRelativeUrl);

            _sp.DeleteItem(library, id);
            var removed = 1;
            foreach (var s in siblings)
            {
                // Il portatore e' gia' sparito: un fratello che resiste va riferito, non fatto
                // passare per un'eliminazione riuscita.
                try { _sp.DeleteItem(library, s.Id); removed++; }
                catch (Exception ex) { _log.LogWarning(ex, "Consegna non eliminata: {File}", s.FileName); }
            }

            // Svuotato il gruppo, resta il contenitore: invisibile nel Backoffice ma capace di
            // consumare un posto per pagina a ogni sfogliata, per sempre.
            if (IsGroupFolder(library, folder)) _sp.DeleteFolderIfEmpty(folder);

            return Ok(new { ok = true, eliminate = removed, nonEliminate = siblings.Count + 1 - removed });
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
            // Il nome va recuperato qui: senza, l'errore compare accanto a una riga vuota e non si
            // capisce a quale file si riferisca.
            return Ok(new RegenerateResult(false, id, SafeFileName(library, id), ex.Message));
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
                results.Add(new RegenerateResult(false, id, SafeFileName(library, id), ex.Message));
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

    /// <summary>
    /// Nome del file per i messaggi d'errore, o stringa vuota se nemmeno quello si riesce a
    /// leggere. Non deve mai far fallire la gestione di un errore che sta gia' accadendo.
    /// </summary>
    private string SafeFileName(string library, int id)
    {
        try { return _sp.GetItem(library, id).FileName; }
        catch { return ""; }
    }

    /// <summary>
    /// True when the file carries pixels a vision model can actually read.
    ///
    /// Deliberately a whitelist rather than a list of things to exclude: an unknown extension is
    /// far more likely to be another format nobody can decode than a raster one, and guessing wrong
    /// costs a paid call that fails.
    /// </summary>
    private static bool IsRasterImage(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant()
            is ".jpg" or ".jpeg" or ".png" or ".webp" or ".tif" or ".tiff" or ".bmp" or ".gif";

    private async Task<RegenerateResult> RegenerateOneAsync(string library, int id, CancellationToken ct)
    {
        var item = _sp.GetItem(library, id);

        // A vector deliverable has nothing a vision model can read: SVG and EPS describe curves,
        // not pixels. Asking anyway costs a download and returns the decoder's own complaint
        // ("Image cannot be loaded. Available decoders: ...") which tells the author nothing about
        // what to do. The metadata for the set belongs to its JPEG, and the pipeline writes it
        // there; this file inherits it when the deliverables are sent.
        if (!IsRasterImage(item.FileName))
            return new RegenerateResult(false, id, item.FileName,
                $"{Path.GetExtension(item.FileName).TrimStart('.').ToUpperInvariant()} è un file vettoriale: " +
                "non contiene pixel da descrivere. Rigenera i metadati dal JPG dello stesso gruppo.");

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
