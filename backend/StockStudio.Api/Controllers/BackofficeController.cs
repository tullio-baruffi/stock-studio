using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StockStudio.Api.Domain;
using StockStudio.Api.Services;
using StockStudio.Shared.Vettoriale;
using StockStudio.Api.Services.Feedback;
using StockStudio.Api.Services.Integration;
using StockStudio.Api.Services.Scoring;
using StockStudio.Shared.Pipeline;

namespace StockStudio.Api.Controllers;

public record UpdateSpItemRequest(string? title, string? description, string? tags);

/// <summary>
/// Una correzione fatta in revisione, con i valori di partenza per poterla leggere come diff.
/// I valori generati arrivano dal client perche' e' l'unico a sapere cosa c'era sullo schermo
/// prima delle modifiche: SharePoint conserva solo l'ultimo stato, non quello proposto dall'AI.
/// </summary>
public record BackofficeFeedbackRequest(
    string? generatedTitle,
    string? generatedDescription,
    string? generatedKeywords,
    string? title,
    string? description,
    string? keywords,
    string? note);
public record MoveSpItemRequest(string targetLibrary);
public record RegenerateRequest(List<int> ids);

/// <summary>
/// Esito di una rivettorializzazione: il peso dei file prima e dopo, perché è il modo più diretto
/// per vedere che qualcosa è davvero cambiato senza aprire il disegno.
/// </summary>
public record RivettorializzaResult(
    bool ok,
    int id,
    string fileName,
    string? error = null,
    /// <summary>Quali consegne sono state riscritte, con il peso vecchio e nuovo.</summary>
    IReadOnlyList<ConsegnaRiscritta>? consegne = null,
    /// <summary>
    /// Vero se il tracciato è avvenuto a colori, falso se in bianco e nero.
    /// </summary>
    bool aColori = false,
    /// <summary>
    /// Vero quando l'immagine è già su Adobe: il file nuovo vive qui, non là.
    /// Per i contributor non esiste un'API, quindi il ricarico sul portale resta a mano.
    /// </summary>
    bool daRiportare = false,
    /// <summary>
    /// Da dove sono stati ricavati i tracciati: "originale" quando si è ripartiti dal file
    /// caricato, "jpeg" quando quello non c'era più e si è dovuto ricalcare il JPEG di consegna.
    /// Chi guarda il risultato deve sapere da cosa è stato ottenuto.
    /// </summary>
    string sorgente = "jpeg",
    /// <summary>
    /// Con che taratura si è tracciato, detta in una riga.
    ///
    /// Senza questa, la scelta automatica è invisibile: il file cambia e nessuno sa perché. Dice
    /// anche se i numeri li ha scelti il disegno o chi ha premuto il pulsante.
    /// </summary>
    string? taratura = null);

public record ConsegnaRiscritta(string tipo, string fileName, int kbPrima, int kbDopo);

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
    object? item = null,
    /// <summary>
    /// True quando l'immagine è già su Adobe: i metadati nuovi vivono qui, non là.
    /// Per i contributor non esiste un'API -- lo dichiara Adobe -- quindi il passaggio finale
    /// resta a mano, e chi ha appena rigenerato deve saperlo subito.
    /// </summary>
    bool daRiportare = false);

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
    private readonly IVectorizer _vettorizzatore;
    private readonly MetadataFeedbackStore _feedback;
    private readonly PunteggioStore _punteggi;
    private readonly Punteggiatore _punteggiatore;
    private readonly QueueDispatcher _queue;
    private readonly PipelineHandoff _handoff;
    private readonly IOptions<VectorizeOptions> _vectorize;
    private readonly ILogger<BackofficeController> _log;

    public BackofficeController(SharePointStore sp, IOptions<PipelineSettings> s,
                                StockValidator validator, IMetadataProvider metadata,
                                IVectorizer vettorizzatore,
                                MetadataFeedbackStore feedback, PunteggioStore punteggi,
                                Punteggiatore punteggiatore,
                                QueueDispatcher queue,
                                PipelineHandoff handoff,
                                IOptions<VectorizeOptions> vectorize,
                                ILogger<BackofficeController> log)
    {
        _sp = sp;
        _s = s.Value;
        _validator = validator;
        _metadata = metadata;
        _vettorizzatore = vettorizzatore;
        _feedback = feedback;
        _punteggi = punteggi;
        _punteggiatore = punteggiatore;
        _queue = queue;
        _handoff = handoff;
        _vectorize = vectorize;
        _log = log;
    }

    /// <summary>
    /// Confronta il punteggio appena calcolato con quello depositato nella libreria, e se non
    /// corrispondono chiede che venga riscritto.
    ///
    /// I metadati non cambiano solo da qui: la Logic App che li genera scrive direttamente su
    /// SharePoint, senza passare da questa applicazione. Un punteggio aggiornato soltanto nei
    /// nostri punti di scrittura resterebbe vuoto su ogni immagine appena lavorata, e sbagliato su
    /// ogni immagine rigenerata fuori. Ricalcolarlo a ogni lettura e correggerlo quando serve è
    /// l'unico modo che non dipende da chi ha scritto per ultimo.
    /// </summary>
    private void AllineaPunteggio(string library, SharePointItem i, int calcolato)
    {
        if (i.PunteggioSalvato != calcolato) _punteggi.Segnala(library, i.Id, calcolato);
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

    /// <summary>
    /// Prepara la colonna del punteggio nelle librerie. Ripetibile senza danni.
    /// </summary>
    [HttpPost("punteggio/colonna")]
    public IActionResult PreparaColonnaPunteggio([FromQuery] string? library = null)
    {
        if (!_s.Enabled) return BadRequest("Pipeline disabilitata.");

        var targets = string.IsNullOrWhiteSpace(library) ? Stages.Keys.ToArray() : new[] { library };
        var results = new List<object>();
        foreach (var lib in targets)
        {
            if (!Stages.ContainsKey(lib)) { results.Add(new { library = lib, ok = false, message = "Libreria non gestita." }); continue; }
            try { results.Add(new { library = lib, ok = true, message = _sp.EnsureScoreColumn(lib) }); }
            catch (Exception ex) { results.Add(new { library = lib, ok = false, message = ex.Message }); }
        }
        return Ok(new { ok = true, results });
    }

    /// <summary>
    /// Se il filtro per punteggio può già usare l'indice, che non ha la soglia dei 5.000.
    ///
    /// Serve a sapere quando il collegamento della colonna a una proprietà numerica dell'indice è
    /// diventato operativo: succede da sé dopo che il crawler ha riletto la libreria, e senza un
    /// modo di chiederlo si potrebbe solo tirare a indovinare.
    /// </summary>
    [HttpGet("punteggio/indice")]
    public IActionResult IndicePunteggio([FromQuery] string library = "ImagesSent")
    {
        var bad = Guard(library);
        if (bad != null) return bad;
        try
        {
            var pronto = _sp.PunteggioIndicizzato(library);
            return Ok(new
            {
                ok = true,
                library,
                indiceNumericoPronto = pronto,
                nota = pronto
                    ? "Le fasce di punteggio possono essere larghe quanto si vuole: l'indice non ha soglie."
                    : "Le fasce restano limitate a 5.000 immagini ciascuna: la colonna non è ancora "
                      + "collegata a una proprietà numerica dell'indice.",
            });
        }
        catch (Exception ex) { return Ok(new { ok = false, error = ex.Message }); }
    }

    /// <summary>Quanti punteggi sono stati depositati, e se questa libreria è già stata riempita per intero.</summary>
    ///
    /// La completezza la dichiara il servizio di sfondo, che sta scorrendo le librerie e sa dove è
    /// arrivato. Prima la si chiedeva a SharePoint con un &lt;IsNull&gt; sulla colonna, e su
    /// ImagesSent quella query superava la soglia: la risposta non arrivava proprio dalla libreria
    /// dove l'avviso serve di più. Dopo un riavvio il servizio riparte da zero e per qualche minuto
    /// dichiara "non ancora completa" anche dove lo è: un avviso di troppo, che è il verso giusto
    /// in cui sbagliare.
    /// </summary>
    [HttpGet("punteggio/stato")]
    public IActionResult StatoPunteggi([FromQuery] string? library = null)
    {
        object? colonna = null;
        if (!string.IsNullOrWhiteSpace(library) && Stages.ContainsKey(library))
            colonna = new { library, completa = _punteggi.Completa(library) };
        return Ok(new { ok = true, coda = _punteggi.Stato(), lotti = _sp.StatoLotti(), colonna });
    }

    /// <summary>
    /// Riempie la colonna del punteggio scorrendo la libreria, un tratto per volta.
    ///
    /// A lotti e con un cursore, non tutto in una volta: diecimila file sono diecimila scritture, e
    /// una richiesta che dura mezz'ora verrebbe interrotta dal server molto prima di finire, senza
    /// lasciare traccia di dove si era arrivati. Così invece ogni chiamata fa un tratto, dice dove
    /// si è fermata, e chi la guida decide se proseguire.
    ///
    /// Scrive solo dove il valore depositato non corrisponde: ripassare su un tratto già fatto non
    /// costa scritture, quindi l'operazione si può ripetere senza pensarci.
    /// </summary>
    [HttpPost("punteggio/riempi")]
    public IActionResult RiempiPunteggi([FromQuery] string library,
                                        [FromQuery] string? pageToken = null,
                                        [FromQuery] int pagine = 4,
                                        [FromQuery] int take = 100)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        var letti = 0;
        var gruppi = 0;
        var daScrivere = new Dictionary<int, int>();
        var cursore = pageToken;
        var fine = false;

        try
        {
            for (var p = 0; p < Math.Clamp(pagine, 1, 20); p++)
            {
                var page = _sp.ListItems(library, Math.Clamp(take, 1, 100), cursore, null, null);
                letti += page.Items.Count;

                foreach (var group in _punteggiatore.Raggruppa(library, page.Items))
                {
                    gruppi++;
                    var i = Punteggiatore.PortatoreDi(group);
                    var v = _validator.Validate(i.Title, i.Description, Punteggiatore.KeywordDi(i.Tags), Punteggiatore.ModoDi(group));
                    if (i.PunteggioSalvato != v.Score) daScrivere[i.Id] = v.Score;
                }

                cursore = page.NextPageToken;
                if (string.IsNullOrEmpty(cursore)) { fine = true; break; }
            }

            // Scrittura in linea, non in coda: qui si sa quanti ne restano e si vuole che il
            // conteggio restituito sia vero, non una promessa.
            var scritti = _sp.SetScores(library, daScrivere);

            return Ok(new
            {
                ok = true,
                library,
                letti,
                gruppi,
                giaCorretti = gruppi - daScrivere.Count,
                scritti,
                nonScritti = daScrivere.Count - scritti,
                fine,
                pageToken = fine ? null : cursore,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Riempimento punteggi non riuscito su {Library}", library);
            return Ok(new { ok = false, error = ex.Message, library, letti, pageToken = cursore });
        }
    }

    /// <summary>One page of a stage, with the Adobe/Freepik score computed for each item.</summary>
    [HttpGet("items")]
    public IActionResult Items([FromQuery] string library = "ImagesToClassify",
                               [FromQuery] int take = 24,
                               [FromQuery] string? pageToken = null,
                               [FromQuery] string? search = null,
                               [FromQuery] string? field = null,
                               [FromQuery] int? punteggioMin = null,
                               [FromQuery] int? punteggioMax = null)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            var page = _sp.ListItems(library, take, pageToken, search, field, punteggioMin, punteggioMax);

            // Il raggruppamento e la regola del punteggio vivono nel Punteggiatore: lo stesso numero
            // serve qui, nella lettura del singolo elemento e nel riempimento che lo deposita in
            // libreria, e tre definizioni diverse dello stesso numero sono tre occasioni di
            // divergere in silenzio.
            var groups = _punteggiatore.Raggruppa(library, page.Items);

            var items = groups.Select(group =>
            {
                // Il portatore e' il raster: e' l'unico che si possa vedere in anteprima e l'unico
                // che un modello sappia descrivere, quindi e' su di lui che agiscono i pulsanti.
                var i = Punteggiatore.PortatoreDi(group);
                var kw = Punteggiatore.KeywordDi(i.Tags);
                var v = _validator.Validate(i.Title, i.Description, kw, Punteggiatore.ModoDi(group));
                AllineaPunteggio(library, i, v.Score);
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
                    previewUrl = Miniatura(i.ServerRelativeUrl),
                    fileUrl = Diretto(i.ServerRelativeUrl),
                    pipeline = StatoDi(library, i),
                    deliverables = group.Count > 1
                        ? group.OrderBy(d => d.FileName).Select(d => new
                        {
                            d.Id,
                            fileName = d.FileName,
                            kind = Path.GetExtension(d.FileName).TrimStart('.').ToUpperInvariant(),
                            carrier = d.Id == i.Id,
                            // Senza indirizzo la consegna e' solo un'etichetta: il vettoriale che
                            // si sta per vendere non si puo' ne' guardare ne' scaricare, e l'unica
                            // verifica possibile resta aprire SharePoint a mano.
                            url = Diretto(d.ServerRelativeUrl),
                            // Le date rendono visibile il ritracciamento: un vettoriale piu' recente
                            // del raster e' stato rifatto dopo, ed e' l'unico modo di accorgersene
                            // senza aprire SharePoint e confrontare a mano.
                            d.Created,
                            d.Modified,
                            rifatto = PiuRecenteDi(d, i),
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
        catch (Exception ex) when (IsThresholdError(ex) && (punteggioMin is not null || punteggioMax is not null))
        {
            // Un filtro per punteggio che seleziona più di 5.000 immagini non è realizzabile su
            // SharePoint, né dalla lista né dall'indice. Dirlo è l'unica risposta onesta: una
            // griglia vuota qui significherebbe "non ce ne sono", che è falso.
            _log.LogWarning(ex, "Soglia superata su {Library} con fascia {Min}-{Max}", library, punteggioMin, punteggioMax);
            return Ok(new
            {
                ok = false,
                error = "Questa fascia di punteggio contiene più di 5.000 immagini e SharePoint " +
                        "rifiuta di filtrarle. Scegli una fascia più stretta: le più utili sono gli " +
                        "estremi -- le peggiori, da rilavorare, e le migliori.",
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
        || ex.Message.Contains("threshold", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("5.000", StringComparison.Ordinal);

    [HttpGet("items/{id:int}")]
    public IActionResult Item(int id, [FromQuery] string library)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try { return Ok(new { ok = true, item = Shape(library, _sp.GetItem(library, id)) }); }
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
            return Ok(new { ok = true, item = Shape(library, updated) });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Registra nel journal la correzione appena salvata, con la nota che la spiega.
    ///
    /// Finora il ciclo di apprendimento partiva solo dai job caricati dall'app: le correzioni fatte
    /// in revisione sulle immagini gia' passate dalla pipeline si perdevano, ed erano proprio quelle
    /// che si ripetevano di piu'. Qui il confronto e' fra i metadati che l'autore ha trovato scritti
    /// (opera dell'AI, a monte) e quelli con cui li ha sostituiti.
    /// </summary>
    [HttpPost("items/{id:int}/feedback")]
    public IActionResult Feedback(int id, [FromQuery] string library, [FromBody] BackofficeFeedbackRequest req)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            var item = _sp.GetItem(library, id);
            var baseName = Path.GetFileNameWithoutExtension(item.FileName);

            var generated = new MetadataSnapshot
            {
                Title = req.generatedTitle ?? "",
                Description = req.generatedDescription ?? "",
                Keywords = Punteggiatore.KeywordDi(req.generatedKeywords ?? ""),
                Category = "",
            };
            var corrected = new MetadataSnapshot
            {
                Title = req.title ?? "",
                Description = req.description ?? "",
                Keywords = Punteggiatore.KeywordDi(req.keywords ?? ""),
                Category = "",
            };

            var entry = _feedback.RecordCorrection(baseName, "vector", generated, corrected, req.note);
            if (entry is null)
                return Ok(new { ok = true, recorded = false, message = "Nessuna differenza e nessuna nota: niente da imparare." });

            _log.LogInformation("Feedback registrato per {File}: +{Added} -{Removed}",
                                item.FileName, entry.KeywordsAdded.Count, entry.KeywordsRemoved.Count);

            return Ok(new
            {
                ok = true,
                recorded = true,
                keywordsAdded = entry.KeywordsAdded,
                keywordsRemoved = entry.KeywordsRemoved,
                titleChanged = entry.TitleChanged,
                pending = _feedback.Count,
            });
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
            return Ok(new { ok = true, changed = r.Changed, message = r.Message, item = Shape(library, r.Item) });
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
            var target = _sp.GetItem(library, id);

            if (value && !force)
            {
                // Un invio si chiede da fermo. Dagli stati di passaggio no: e' proprio li' che
                // premere due volte -- o selezionare in blocco senza guardare -- faceva partire due
                // copie della stessa immagine. La regola e' la stessa che decide i pulsanti, letta
                // dallo stesso posto: tenerne due versioni vorrebbe dire vederle divergere.
                var (stato, etichetta, spiega) = StatoPipeline.Di(library, target.Invia, target.Inviato, target.Stato);
                if (!StatoPipeline.SiPuoInviare(stato))
                    return Ok(new
                    {
                        ok = false,
                        giaInCorso = true,
                        stato,
                        error = $"«{target.FileName}» è già «{etichetta.ToLowerInvariant()}»: {spiega}",
                    });

                var v = _validator.Validate(target.Title, target.Description, Punteggiatore.KeywordDi(target.Tags), "vector");
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

            return Ok(new { ok = true, item = Shape(library, updated), gruppo = group });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Fa partire la pubblicazione subito, invece di aspettare il giro di sorveglianza.
    ///
    /// Il flag Invia viene guardato da una Logic App che interroga SharePoint ogni quindici minuti:
    /// e' un ritardo accettabile per un lotto notturno, non per chi sta guardando la schermata e
    /// vuole vedere se il file passa. Qui si scrive direttamente sulla coda che la Logic App
    /// riempirebbe, con lo stesso messaggio: la funzione che pubblica non sa da dove arriva, e la
    /// catena resta una sola.
    ///
    /// ## Le due strade devono essere indistinguibili
    /// Non basta che finiscano nella stessa coda: devono lasciare SharePoint nello stesso stato, o
    /// il file diventa raggiungibile da entrambe e parte due volte. In particolare si segna
    /// "Inviato" **prima** di accodare, esattamente come fa la Logic App, e ogni consegna gia'
    /// partita resta fuori.
    /// </summary>
    [HttpPost("items/{id:int}/pubblica-ora")]
    public async Task<IActionResult> PubblicaOra(int id, [FromQuery] string library = "ImagesToSend",
                                                 [FromQuery] bool force = false,
                                                 CancellationToken ct = default)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        if (!_queue.CanEnqueue)
            return Ok(new { ok = false, error = "Storage della pipeline non configurato: resta il giro di sorveglianza ogni quindici minuti." });

        try
        {
            var portatore = _sp.GetItem(library, id);

            if (!force)
            {
                // Forzare si puo' anche da "in attesa" -- e' il caso per cui il pulsante esiste --
                // ma non da una consegna gia' in corso: li' il messaggio e' gia' in coda, e un
                // secondo lo duplicherebbe sul marketplace.
                var (stato, etichetta, spiega) = StatoPipeline.Di(library, portatore.Invia, portatore.Inviato, portatore.Stato);
                if (!StatoPipeline.SiPuoForzare(stato))
                    return Ok(new
                    {
                        ok = false,
                        giaInCorso = true,
                        stato,
                        error = $"«{portatore.FileName}» è già «{etichetta.ToLowerInvariant()}»: {spiega}",
                    });

                var v = _validator.Validate(portatore.Title, portatore.Description,
                                            Punteggiatore.KeywordDi(portatore.Tags), "vector");
                if (v.BlocksDispatch)
                    return Ok(new
                    {
                        ok = false,
                        blocked = true,
                        error = "I metadati non superano la validazione: correggili o forza l'invio.",
                        issues = v.Issues.Where(x => x.Severity == "error").Select(x => x.Message),
                    });
            }

            // Prima si marca, poi si accoda: se l'accodamento fallisce resta il giro di
            // sorveglianza a raccogliere il file, mentre accodare senza marcare lo farebbe
            // pubblicare senza che nulla lo ricordi.
            var aggiornato = _sp.SetInvia(library, id, true);
            var gruppo = PropagateToGroup(library, aggiornato, true);

            // Le consegne gia' partite restano fuori. PropagateToGroup le salta gia' quando
            // allinea i contrassegni, ma finche' il ciclo qui sotto le includeva lo stesso, un
            // gruppo inviato a meta' rimandava ai marketplace quel che era gia' salito.
            var consegne = new List<SharePointItem> { aggiornato };
            consegne.AddRange(_sp.GetDeliverableSiblings(library, aggiornato).Where(s => !s.Inviato));

            var accodate = new List<string>();
            var saltate = new List<string>();
            SharePointItem finale = aggiornato;

            foreach (var c in consegne)
            {
                // La presa in carico sta dentro al riparo insieme all'accodamento: sono due passi
                // di una cosa sola, e se il primo non riesce il secondo non deve nemmeno partire --
                // ma nemmeno deve fermare le altre consegne del gruppo.
                var presa = false;
                try
                {
                    // Prima dell'accodamento, come fa la Logic App: da questo istante la
                    // sorveglianza non vede piu' il file e non puo' accodarlo una seconda volta.
                    var marcato = _sp.MarcaPresoInCarico(library, c.Id, true);
                    presa = true;
                    if (c.Id == aggiornato.Id) finale = marcato;

                    var corpo = new
                    {
                        title = c.Title,
                        description = c.Description,
                        tags = c.Tags,
                        url = c.ServerRelativeUrl,
                        Id = c.Id,
                        Identifier = IdentificatoreDi(c.ServerRelativeUrl),
                    };
                    if (!await _queue.AccodaInvioAsync(corpo, ct))
                        throw new InvalidOperationException("la coda non ha accettato il messaggio");
                    accodate.Add(c.FileName);
                }
                catch (Exception ex)
                {
                    // Marcato ma non accodato sarebbe il peggiore dei due mondi: il file resterebbe
                    // fermo per sempre, creduto partito, e nemmeno la sorveglianza lo raccoglierebbe.
                    _log.LogWarning(ex, "Accodamento non riuscito per {File}", c.FileName);
                    if (presa)
                    {
                        try { _sp.MarcaPresoInCarico(library, c.Id, false); }
                        catch (Exception rip) { _log.LogError(rip, "Presa in carico non tolta per {File}: resta fermo", c.FileName); }
                    }
                    saltate.Add(c.FileName);
                }
            }

            _log.LogInformation("Pubblicazione immediata: accodate {N} consegne di {File}, non riuscite {M}",
                                accodate.Count, aggiornato.FileName, saltate.Count);
            // Riuscito vuol dire che e' partito **tutto** il gruppo. Con una consegna sola in coda e
            // due rimaste indietro, dire "fatto" manderebbe l'autore a guardare altro mentre due
            // terzi del prodotto non sono saliti: le riprendera' la sorveglianza entro un quarto
            // d'ora, ma chi ha premuto il pulsante deve saperlo adesso.
            return Ok(new
            {
                ok = saltate.Count == 0 && accodate.Count > 0,
                accodate = accodate.Count,
                nonRiuscite = saltate.Count,
                error = accodate.Count == 0
                    ? "Nessuna consegna è finita in coda: non è partito niente."
                    : saltate.Count > 0
                        ? $"{accodate.Count} in coda, {saltate.Count} no ({string.Join(", ", saltate)}): "
                          + "quelle rimaste indietro ripartono col giro di sorveglianza entro quindici minuti."
                        : null,
                gruppo,
                item = Shape(library, finale),
            });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Quanto deve restare fermo un file "in pubblicazione" prima che lo si possa sbloccare a mano.
    ///
    /// Una pubblicazione vera si chiude in secondi, e il giro di sorveglianza passa ogni quindici
    /// minuti: mezz'ora e' oltre qualunque attesa legittima. Sotto quella soglia lo sblocco e'
    /// rifiutato, perche' togliere la presa in carico a un file davvero in volo lo farebbe accodare
    /// una seconda volta -- cioe' esattamente il difetto che la presa in carico esiste per evitare.
    /// </summary>
    private const int MinutiPrimaDiPoterSbloccare = 30;

    private static bool FermoDaTroppo(string modified)
    {
        return DateTimeOffset.TryParse(modified, out var quando)
            && DateTimeOffset.UtcNow - quando.ToUniversalTime() > TimeSpan.FromMinutes(MinutiPrimaDiPoterSbloccare);
    }

    /// <summary>
    /// Rimette in gioco un file rimasto fermo in "in pubblicazione".
    ///
    /// ## Perche' serve una via di rientro
    /// Chi accoda segna "Inviato" prima di mettere il messaggio in coda, e da quel momento nessuno
    /// puo' piu' scrivere su quell'elemento: ne' l'invio, ne' la pubblicazione immediata, ne' la
    /// sorveglianza, che cerca proprio i file **senza** quel contrassegno. E' la protezione contro
    /// il doppio invio, e funziona finche' la catena si chiude.
    ///
    /// Se non si chiude -- il processo muore fra la marcatura e l'accodamento, la chiamata che
    /// sposta il file fallisce e viene inghiottita, la coda perde il messaggio -- quel file resta
    /// fermo per sempre, invisibile a tutti e mostrato come "in pubblicazione" a vita. Senza questo
    /// endpoint l'unico rimedio sarebbe modificare la colonna a mano in SharePoint.
    /// </summary>
    [HttpPost("items/{id:int}/sblocca")]
    public IActionResult Sblocca(int id, [FromQuery] string library = "ImagesToSend")
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            var it = _sp.GetItem(library, id);
            var (stato, _, _) = StatoPipeline.Di(library, it.Invia, it.Inviato, it.Stato);

            if (stato != StatoPipeline.InConsegna)
                return Ok(new { ok = false, error = $"«{it.FileName}» non è in pubblicazione: non c'è niente da sbloccare." });

            if (!FermoDaTroppo(it.Modified))
                return Ok(new
                {
                    ok = false,
                    error = $"«{it.FileName}» è stato preso in carico da poco: aspetta che finisca. "
                          + $"Lo sblocco serve a un file rimasto fermo da più di {MinutiPrimaDiPoterSbloccare} minuti, "
                          + "e toglierlo a una consegna in volo la farebbe partire due volte.",
                });

            var aggiornato = _sp.MarcaPresoInCarico(library, id, false);
            _log.LogWarning("Sbloccato {File}: era fermo in pubblicazione da {Quando}", it.FileName, it.Modified);
            return Ok(new { ok = true, item = Shape(library, aggiornato) });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sblocco non riuscito per {Library}/{Id}", library, id);
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
            var folder = Punteggiatore.CartellaDi(carrier.ServerRelativeUrl);

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
            if (_punteggiatore.CartellaDiGruppo(library, folder)) _sp.DeleteFolderIfEmpty(folder);

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
    /// Ritraccia SVG ed EPS di un'immagine già in libreria, riscrivendoli al loro posto.
    ///
    /// ## Perché serve un'azione apposta
    /// Il tracciato migliora nel tempo, ma le immagini già consegnate restano com'erano: chi le
    /// guarda nel Backoffice vede il risultato del codice del giorno in cui sono state lavorate.
    /// Senza questo comando l'unico modo di aggiornarle sarebbe ricaricarle da capo, perdendo
    /// metadati, punteggio, data di ingresso e la posizione nel flusso. La rigenerazione dei
    /// metadati (l'altro pulsante) non c'entra: quella riscrive titolo e keyword chiamando il
    /// modello di visione, questa riscrive il disegno e non tocca una parola.
    ///
    /// ## Il JPEG non si riscrive, ed è deliberato
    /// Il JPEG del gruppo è la **sorgente** da cui si traccia. Rigenerarlo significherebbe
    /// ricomprimere una compressione -- perdita di generazione su un file che il cliente vede nei
    /// risultati di ricerca -- in cambio di niente, perché il vettorizzatore lo ricava dallo stesso
    /// originale che ha appena letto. Si riscrivono solo i vettoriali, che sono ciò che cambia.
    /// </summary>
    /// <summary>
    /// Che taratura chiede **questa** immagine, guardandola.
    ///
    /// ## Perché serve
    /// I nove numeri del tracciato si possono scegliere a mano, ma per sceglierli bisogna sapere
    /// che cosa guardare, e chi carica cinquanta disegni al giorno non ha motivo di impararlo. Qui
    /// li sceglie il sistema misurando il disegno -- quanto è fatto di tinte piatte, quanto sono
    /// spessi i tratti, quante tinte distingue -- e riferisce anche **perché**, così la proposta
    /// si può discutere invece di doverla prendere per buona.
    ///
    /// Si misura sull'originale conservato quando c'è: è l'unica copia mai compressa, e misurare
    /// il JPEG di consegna vorrebbe dire misurare anche gli aloni della compressione.
    /// </summary>
    [HttpGet("items/{id:int}/tracciato-consigliato")]
    public async Task<IActionResult> TracciatoConsigliato(int id, [FromQuery] string library,
                                                          CancellationToken ct)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            var carrier = _sp.GetItem(library, id);
            if (!Punteggiatore.EImmagineRaster(carrier.FileName))
                return Ok(new { ok = false, error = "Non è un'immagine: non c'è niente da misurare." });

            var conservato = await _handoff.OriginaleAsync(CartellaDi(carrier.ServerRelativeUrl), ct);
            byte[] contenuto;
            if (conservato != null)
            {
                using var memoria = new MemoryStream();
                await conservato.Value.Contenuto.CopyToAsync(memoria, ct);
                await conservato.Value.Contenuto.DisposeAsync();
                contenuto = memoria.ToArray();
            }
            else
            {
                contenuto = _sp.DownloadFile(carrier.ServerRelativeUrl);
            }

            using var img = CaricaImmagine.SuBianco(contenuto);
            var rgb = new byte[img.Width * img.Height * 3];
            img.CopyPixelDataTo(rgb);

            var misure = Disegno.Guarda(rgb, img.Width, img.Height, null);
            var serie = _vectorize.Value.Tracciato.Convalidato();
            var consigliati = Disegno.Consiglia(misure, serie);

            // Due scale, e vanno riferite entrambe. I parametri si scrivono riferiti a una
            // grandezza convenzionale, ma su **questa** immagine valgono un altro numero: dire solo
            // il primo vuol dire dare a chi guarda un valore che non ritrova da nessuna parte.
            var effettivi = consigliati.PerImmagine(img.Width, img.Height);
            var serieEffettivi = serie.PerImmagine(img.Width, img.Height);

            var differenze = new List<object>();
            void Confronta(string campo, string etichetta, double proposto, double diSerie, string unita = "")
            {
                if (Math.Abs(proposto - diSerie) < 0.01) return;
                differenze.Add(new
                {
                    campo,
                    etichetta,
                    daSerie = Math.Round(diSerie, 1),
                    proposto = Math.Round(proposto, 1),
                    unita,
                });
            }

            Confronta("colori", "Numero di tinte", consigliati.NumeroColori, serie.NumeroColori);
            Confronta("lisciatura", "Lisciatura della mappa", effettivi.RaggioLisciatura, serieEffettivi.RaggioLisciatura, " px");
            Confronta("granelli", "Granelli da togliere", effettivi.Granelli, serieEffettivi.Granelli, " px²");
            Confronta("rumore", "Riduzione rumore", effettivi.RiduzioneRumore, serieEffettivi.RiduzioneRumore, " px");
            Confronta("tolleranza", "Fedeltà del tracciato", effettivi.Tolleranza, serieEffettivi.Tolleranza, " px");

            return Ok(new
            {
                ok = true,
                sorgente = conservato != null ? "originale" : "jpeg",
                misure = new
                {
                    genere = misure.Genere,
                    scarto = Math.Round(misure.Scarto, 1),
                    tinte = misure.Tinte,
                    spessore = Math.Round(misure.Spessore, 1),
                    inchiostro = Math.Round(misure.Inchiostro * 100, 1),
                    larghezza = img.Width,
                    altezza = img.Height,
                },
                perche = Perche(misure),
                /// Cosa cambia davvero rispetto alla taratura di serie, con i numeri che valgono
                /// su questa immagine. Vuoto vuol dire che per questo disegno i predefiniti vanno
                /// gia' bene, ed e' un'informazione, non un fallimento.
                differenze,
                valori = new
                {
                    colori = consigliati.NumeroColori,
                    unione = consigliati.SogliaUnione,
                    rumore = consigliati.RiduzioneRumore,
                    lisciatura = consigliati.RaggioLisciatura,
                    granelli = consigliati.Granelli,
                    morbidezza = consigliati.Morbidezza,
                    giri = consigliati.GiriLisciatura,
                    tolleranza = consigliati.Tolleranza,
                    angolo = consigliati.AngoloSpigolo,
                },
                effettivi = new
                {
                    colori = effettivi.NumeroColori,
                    rumore = effettivi.RiduzioneRumore,
                    lisciatura = effettivi.RaggioLisciatura,
                    granelli = effettivi.Granelli,
                    tolleranza = Math.Round(effettivi.Tolleranza, 1),
                },
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Misura del disegno non riuscita per {Library}/{Id}", library, id);
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Perché la proposta è quella: una riga per ogni numero che il disegno ha deciso.</summary>
    private static string[] Perche(Disegno.Misure m)
    {
        var righe = new List<string>();
        righe.Add(m.ATintePiatte
            ? $"I colori cambiano di scatto (scarto {m.Scarto:0.0}): è un disegno a tinte piatte, " +
              "quindi niente lisciatura — arrotonderebbe gli spigoli e stringerebbe i vuoti."
            : $"I colori passano per valori intermedi (scarto {m.Scarto:0.0}): è un'illustrazione " +
              "sfumata, e una lisciatura leggera toglie la scalinata senza toccare la forma.");
        righe.Add($"I tratti sono spessi circa {m.Spessore:0} pixel: i granelli si tolgono sotto un " +
                  "quarto di quell'area, e la riduzione del rumore resta a un ottavo di quello " +
                  "spessore per non mangiarli.");
        righe.Add($"Si distinguono {m.Tinte} tinte: se ne chiedono la metà in più, perché chiederne " +
                  "molte di più non ne inventa e costa tempo.");
        return righe.ToArray();
    }

    [HttpPost("items/{id:int}/rivettorializza")]
    public async Task<IActionResult> Rivettorializza(int id, [FromQuery] string library,
                                                     [FromBody] ParametriTracciatoModulo? tracciato,
                                                     CancellationToken ct)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        try
        {
            return Ok(await RivettorializzaOneAsync(library, id, tracciato, ct));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Rivettorializzazione non riuscita per {Library}/{Id}", library, id);
            return Ok(new RivettorializzaResult(false, id, SafeFileName(library, id), ex.Message));
        }
    }

    /// <summary>
    /// Non esiste un equivalente "per tutta la pagina" di questa azione, ed è deliberato.
    ///
    /// Un tracciato a colori è una ventina di passate di potrace: sull'immagine di prova sono
    /// quattro secondi di CPU, ai quali si aggiungono lo scaricamento e due caricamenti su
    /// SharePoint. Ventiquattro immagini in una sola richiesta supererebbero i 230 secondi oltre i
    /// quali Azure chiude la connessione, e il lavoro andrebbe perso a metà senza che nessuno sappia
    /// dove si era arrivati.
    ///
    /// Il Backoffice chiama quindi questo endpoint una immagine alla volta. Costa qualche
    /// round-trip in più e in cambio non può scadere, mostra a che punto è e, se una immagine
    /// fallisce, le altre proseguono.
    /// </summary>
    private async Task<RivettorializzaResult> RivettorializzaOneAsync(string library, int id,
                                                                      ParametriTracciatoModulo? tracciato,
                                                                      CancellationToken ct)
    {
        var carrier = _sp.GetItem(library, id);

        // Si traccia **dai pixel**: chiedere di ritracciare un SVG non ha senso, e l'errore va detto
        // indicando cosa fare invece, perché nel Backoffice il gruppo si presenta come una riga sola
        // e non è ovvio quale dei tre file sia quello su cui agire.
        if (!Punteggiatore.EImmagineRaster(carrier.FileName))
            return new RivettorializzaResult(false, id, carrier.FileName,
                $"{Path.GetExtension(carrier.FileName).TrimStart('.').ToUpperInvariant()} è già un vettoriale: " +
                "il tracciato si rifà dal JPG dello stesso gruppo.");

        var fratelli = _sp.GetDeliverableSiblings(library, carrier);
        var vettoriali = fratelli
            .Where(f => Path.GetExtension(f.FileName).ToLowerInvariant() is ".svg" or ".eps")
            .ToList();

        if (vettoriali.Count == 0)
            return new RivettorializzaResult(false, id, carrier.FileName,
                "Nessun vettoriale accanto a questa immagine: non c'è niente da riscrivere. " +
                "Un'immagine senza SVG ed EPS va ricaricata dalla pagina di caricamento.");

        // Un file estratto rifiuterebbe la scrittura *dopo* il tracciato, buttando via il lavoro.
        // Meglio accorgersene prima, e dire chi lo tiene.
        var bloccato = vettoriali.FirstOrDefault(v => v.CheckedOutBy.Length > 0);
        if (bloccato != null)
            return new RivettorializzaResult(false, id, carrier.FileName,
                $"{bloccato.FileName} è estratto da {bloccato.CheckedOutBy}: SharePoint rifiuterebbe " +
                "la riscrittura. Archivialo e riprova.");

        var lavoro = Path.Combine(Path.GetTempPath(), "rivettorializza-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(lavoro);
        try
        {
            // Nome di lavoro neutro: i nomi veri arrivano da SharePoint e contengono puntini di
            // sospensione e altri caratteri che su disco è inutile far viaggiare. I file si
            // ricaricano poi con il nome del fratello che sostituiscono, non con questo.
            //
            // Da dove si parte conta più di ogni altro parametro del tracciato: il JPEG accanto
            // all'immagine è compresso a qualità 92 e ridotto a 4000 pixel, quindi ricalcarlo
            // significa tracciare anche gli aloni della compressione. L'originale, quando c'è,
            // non è mai stato compresso.
            var conservato = await _handoff.OriginaleAsync(CartellaDi(carrier.ServerRelativeUrl), ct);
            var daOriginale = conservato != null;

            var sorgente = Path.Combine(lavoro,
                "sorgente" + Path.GetExtension(daOriginale ? conservato!.Value.Nome : carrier.FileName));

            if (daOriginale)
            {
                using var destinazione = System.IO.File.Create(sorgente);
                await conservato!.Value.Contenuto.CopyToAsync(destinazione, ct);
                await conservato.Value.Contenuto.DisposeAsync();
            }
            else
            {
                await System.IO.File.WriteAllBytesAsync(sorgente, _sp.DownloadFile(carrier.ServerRelativeUrl), ct);
            }

            // Chi rivettorializza lo fa **guardando il risultato precedente**: e' il momento in cui
            // la taratura di serie si rivela sbagliata per quel disegno, e l'unico in cui si puo'
            // dire di meglio.
            //
            // Se non ha scelto niente, invece di applicare i predefiniti si **guarda il disegno**:
            // e' quel che rende il ritracciamento automatico anche dove non c'e' una finestra da
            // cui scegliere -- la veste classica, e i ritracciamenti di gruppo, che sono poi il
            // posto dove passa la maggior parte delle immagini. Vedi Disegno.Consiglia.
            var scelti = tracciato?.Su(_vectorize.Value.Tracciato);
            var preset = tracciato?.PresetScelto;
            // Un preset in bianco e nero non porta numeri di tracciato -- ne porta la modalita' e
            // la soglia. Senza questo, scegliere "Silhouette" nella finestra avrebbe cambiato
            // l'elenco e non il disegno.
            var colore = Modalita.ColoreImposto(preset?.ModalitaTracciato);
            var soglia = preset?.Soglia;

            // Un preset che dice "automatico" non porta numeri **apposta**: deve lasciare che la
            // misura sull'immagine faccia il suo mestiere, non congelarsi sui predefiniti.
            if (preset != null && preset.MisuraLImmagine) scelti = null;

            // Sui preset in bianco e nero non si misura: quelli portano una soglia, non dei numeri
            // di tracciato, e `Su()` restituisce comunque la taratura configurata perche' un preset
            // scelto conta gia' come "qualcosa e' stato scelto". Il disegno lo fa la soglia.

            var daMisura = scelti == null;
            if (daMisura)
            {
                using var osservata = await CaricaImmagine.SuBiancoAsync(sorgente, ct);
                var pixel = new byte[osservata.Width * osservata.Height * 3];
                osservata.CopyPixelDataTo(pixel);
                var misure = Disegno.Guarda(pixel, osservata.Width, osservata.Height, null);
                scelti = Disegno.Consiglia(misure, _vectorize.Value.Tracciato);
                _log.LogInformation("Ritracciamento di {File}: {Genere}, {Tinte} tinte, tratti {Spessore:0} px " +
                                    "-> lisciatura {Lisciatura}, granelli {Granelli}, tinte chieste {Colori}",
                                    carrier.FileName, misure.Genere, misure.Tinte, misure.Spessore,
                                    scelti.RaggioLisciatura, scelti.Granelli, scelti.NumeroColori);
            }

            var vr = await _vettorizzatore.VectorizeAsync(sorgente, lavoro, "tracciato", ct,
                new VectorizeOverride(AutoThreshold: soglia.HasValue ? false : null,
                                      Threshold: soglia, Colore: colore, Tracciato: scelti));

            var riscritte = new List<ConsegnaRiscritta>();
            foreach (var v in vettoriali)
            {
                var estensione = Path.GetExtension(v.FileName).ToLowerInvariant();
                var prodotto = estensione == ".svg" ? vr.SvgFile : vr.EpsFile;
                if (prodotto == null) continue;

                var percorso = Path.Combine(lavoro, prodotto);
                if (!System.IO.File.Exists(percorso)) continue;

                var kbPrima = PesoInKb(v.ServerRelativeUrl);

                // Si riscrive il file al suo posto invece di ricaricarlo come nuovo: così la riga di
                // libreria non si muove e titolo, keyword, punteggio e data di ingresso restano
                // quelli di prima, senza doverli salvare e rimettere a mano.
                try
                {
                    using var contenuto = System.IO.File.OpenRead(percorso);
                    _sp.ReplaceFile(contenuto, v.ServerRelativeUrl);
                }
                catch (Exception ex)
                {
                    // SharePoint risponde con frasi brevissime -- "Accesso negato." -- che da sole
                    // non dicono su quale dei tre file si sia fermato né cosa stesse facendo. Senza
                    // questo contorno il messaggio manda a cercare un problema di permessi che di
                    // solito non c'entra: è già successo durante lo sviluppo di questa azione.
                    throw new InvalidOperationException(
                        $"Riscrittura di {v.FileName} non riuscita: {ex.Message}", ex);
                }

                riscritte.Add(new ConsegnaRiscritta(
                    estensione.TrimStart('.').ToUpperInvariant(), v.FileName,
                    kbPrima, (int)Math.Round(new FileInfo(percorso).Length / 1024.0)));
            }

            if (riscritte.Count == 0)
                return new RivettorializzaResult(false, id, carrier.FileName,
                    "Il tracciato non ha prodotto file: controlla i log del vettorizzatore.");

            _log.LogInformation("Rivettorializzate {Quante} consegne di {File} partendo da {Sorgente}",
                                riscritte.Count, carrier.FileName, daOriginale ? "originale" : "JPEG di consegna");

            return new RivettorializzaResult(
                true, id, carrier.FileName,
                consegne: riscritte,
                aColori: vr.AColori ?? false,
                daRiportare: carrier.Inviato,
                sorgente: daOriginale ? "originale" : "jpeg",
                taratura: daMisura
                    ? $"scelta guardando il disegno: {scelti!.NumeroColori} tinte, lisciatura " +
                      $"{scelti.RaggioLisciatura}, granelli {scelti.Granelli}"
                    : "scelta a mano nella finestra");
        }
        finally
        {
            try { Directory.Delete(lavoro, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Quanto pesa oggi un file in libreria, in KB. Serve solo a mostrare il prima e il dopo: se
    /// non si riesce a leggerlo si risponde zero, perché una misura mancante non deve far fallire
    /// un'operazione che invece è riuscita.
    /// </summary>
    private int PesoInKb(string serverRelativeUrl)
    {
        try { return (int)Math.Round(_sp.FileSize(serverRelativeUrl) / 1024.0); }
        catch { return 0; }
    }

    private async Task<RegenerateResult> RegenerateOneAsync(string library, int id, CancellationToken ct)
    {
        var item = _sp.GetItem(library, id);

        // A vector deliverable has nothing a vision model can read: SVG and EPS describe curves,
        // not pixels. Asking anyway costs a download and returns the decoder's own complaint
        // ("Image cannot be loaded. Available decoders: ...") which tells the author nothing about
        // what to do. The metadata for the set belongs to its JPEG, and the pipeline writes it
        // there; this file inherits it when the deliverables are sent.
        if (!Punteggiatore.EImmagineRaster(item.FileName))
            return new RegenerateResult(false, id, item.FileName,
                $"{Path.GetExtension(item.FileName).TrimStart('.').ToUpperInvariant()} è un file vettoriale: " +
                "non contiene pixel da descrivere. Rigenera i metadati dal JPG dello stesso gruppo.");

        // Un file già consegnato si può rigenerare, ma i metadati nuovi restano qui.
        //
        // Prima era vietato, con la motivazione che "i metadati non verrebbero più usati": vero
        // per la pipeline -- l'EXIF è stato scritto all'invio e quel treno è passato -- ma falso
        // per il mercato. Adobe consente di modificare titolo e keyword di un'immagine già
        // pubblicata e in vendita ("Content can be edited before submission or after approval and
        // publication"), e dichiara che rifinire i metadati migliora la visibilità in ricerca.
        //
        // Vietarlo significava impedire l'unico intervento possibile su diecimila immagini già
        // online. Ora si può, ma chi lo fa deve sapere che il lavoro non è finito: i metadati vanno
        // riportati sul portale Adobe a mano, perché per i contributor non esiste un'API -- lo
        // dichiara Adobe stessa. Il messaggio lo dice, invece di lasciarlo scoprire dopo.
        var giaPubblicato = item.Inviato;

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
            previousKeywords: Punteggiatore.KeywordDi(item.Tags).Count,
            item: Shape(library, updated),
            daRiportare: giaPubblicato);
    }

    /// <summary>Dice se il motore di ricerca risponde a questa identità. Diagnostica, non di flusso.</summary>
    [HttpGet("search-check")]
    public IActionResult SearchCheck([FromQuery] string library = "ImagesToClassify",
                                     [FromQuery] string q = "",
                                     [FromQuery] string? select = null)
    {
        var bad = Guard(library);
        if (bad != null) return bad;

        var d = _sp.DiagnosticaRicerca(library, q, select);
        return Ok(new { ok = d.Ok, totalRows = d.TotalRows, righe = d.Righe, kql = d.Kql, errore = d.Errore, campioni = d.Campioni });
    }

    /// <summary>
    /// L'indirizzo del file su SharePoint, per il browser.
    ///
    /// Fino a ieri ogni anteprima passava dall'API: scarico del file intero con il certificato
    /// dell'applicazione, ridimensionamento in memoria, e poi i byte al browser. Funzionava, ma
    /// pagava due volte -- banda in entrata e CPU -- su un piano con sessanta minuti di CPU al
    /// giorno, tanto che il frontend aveva dovuto mettere una coda a quattro anteprime per volta
    /// per non far cadere il server.
    ///
    /// Chi guarda ha accesso alla libreria, quindi il browser può chiedere il file a SharePoint per
    /// conto suo: l'API esce dal percorso delle immagini e resta solo su dati e comandi. Il vecchio
    /// indirizzo continua a essere emesso come ripiego, per il caso in cui la sessione SharePoint
    /// non ci sia: meglio un'anteprima lenta che un riquadro rotto.
    /// </summary>
    private string Diretto(string serverRelativeUrl) =>
        UrlSharePoint.Diretto(_s.SiteUrl!, serverRelativeUrl);

    /// <summary>
    /// La miniatura già pronta di SharePoint.
    ///
    /// Serve perché la galleria mostra decine di immagini insieme e gli originali pesano megabyte
    /// l'uno: chiedere i file interi al posto delle miniature sposterebbe il costo dal nostro
    /// server alla rete di chi guarda, che non è un miglioramento. SharePoint le genera e le tiene
    /// in cache per conto suo, quindi qui non si ridimensiona più niente.
    ///
    /// La risoluzione è una scala, non un numero di pixel: 2 dà il lato lungo intorno agli 800,
    /// che su una scheda da 168 basta e avanza anche su schermi a densità doppia. Il valore 4 --
    /// provato per primo -- restituiva 1600 pixel, cioè quattro volte i dati necessari.
    /// </summary>
    private string Miniatura(string serverRelativeUrl, int risoluzione = 2) =>
        UrlSharePoint.Miniatura(_s.SiteUrl!, serverRelativeUrl, risoluzione);

    private object Shape(string library, SharePointItem i)
    {
        var kw = Punteggiatore.KeywordDi(i.Tags);
        // Fuori dal raggruppamento si vede un file solo: il suo modo lo dice la sua estensione.
        var v = _validator.Validate(i.Title, i.Description, kw, Punteggiatore.ModoDi(new[] { i }));
        AllineaPunteggio(library, i, v.Score);
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
            created = i.Created,
            previewUrl = Miniatura(i.ServerRelativeUrl),
            fileUrl = Diretto(i.ServerRelativeUrl),
            pipeline = StatoDi(library, i),
            validation = new
            {
                score = v.Score,
                blocksDispatch = v.BlocksDispatch,
                issues = v.Issues.Select(x => new { x.Severity, x.Field, x.Message }),
            },
        };
    }

    /// <summary>
    /// Lo stato di pipeline, risolto qui e non dedotto da chi mostra.
    ///
    /// Insieme allo stato viaggiano le due domande che decidono i pulsanti. Potrebbero sembrare
    /// deducibili dallo stato, e infatti lo sono -- ma dedurle nel frontend vorrebbe dire scrivere
    /// la stessa regola in due lingue diverse e vederle divergere alla prima modifica. E' la regola
    /// che fa partire un caricamento vero: vale la pena mandarla gia' risolta.
    /// </summary>
    private static object StatoDi(string library, SharePointItem i)
    {
        var (stato, etichetta, spiega) = StatoPipeline.Di(library, i.Invia, i.Inviato, i.Stato);
        return new
        {
            stato,
            etichetta,
            spiega,
            puoInviare = StatoPipeline.SiPuoInviare(stato),
            puoForzare = StatoPipeline.SiPuoForzare(stato),
            // Solo per un file rimasto fermo: vedi Sblocca. Qui serve la data, che la macchina a
            // stati non conosce, quindi la domanda si risolve dove l'elemento c'e' per intero.
            puoSbloccare = stato == StatoPipeline.InConsegna && FermoDaTroppo(i.Modified),
        };
    }

    /// <summary>
    /// Se questa consegna e' stata rifatta dopo il raster che la accompagna.
    ///
    /// E' il segno del ritracciamento: si rifanno SVG ed EPS e il JPEG resta com'era, quindi una
    /// data piu' recente della sua dice che le curve non sono piu' quelle di partenza. Il confronto
    /// vuole un margine, perche' i file di una stessa generazione si scrivono a pochi secondi
    /// l'uno dall'altro e non vanno segnalati.
    /// </summary>
    /// <summary>
    /// L'identificatore che SharePoint attribuisce a un file, nella forma in cui lo scrive la
    /// Logic App di sorveglianza.
    ///
    /// E' il percorso relativo al sito con le barre codificate due volte:
    /// <c>ImagesToSend%252fcartella%252ffile.svg</c>. La forma non e' una scelta, e' quella che
    /// esce dal connettore SharePoint -- verificata su un messaggio vero -- e le due strade di
    /// pubblicazione devono produrre lo stesso messaggio, o a valle diventano distinguibili.
    /// </summary>
    private static string IdentificatoreDi(string serverRelativeUrl)
    {
        if (string.IsNullOrWhiteSpace(serverRelativeUrl)) return string.Empty;
        var pezzi = serverRelativeUrl.Trim('/').Split('/');
        // "/sites/<sito>/<libreria>/…": il prefisso del sito non fa parte dell'identificatore.
        var da = pezzi.Length >= 3 && string.Equals(pezzi[0], "sites", StringComparison.OrdinalIgnoreCase)
            ? 2 : 0;
        return string.Join("%252f", pezzi.Skip(da));
    }

    /// <summary>
    /// Il nome della cartella che contiene un file di libreria, che è anche la chiave con cui
    /// l'originale è stato conservato.
    ///
    /// Ogni immagine vettorizzata vive in una sottocartella intitolata al suo nome, e i tre file
    /// stanno dentro. Un'immagine senza tracciati invece sta nella radice della libreria: lì non
    /// c'è nessuna cartella, e nessun originale da cercare.
    /// </summary>
    private static string CartellaDi(string serverRelativeUrl)
    {
        if (string.IsNullOrWhiteSpace(serverRelativeUrl)) return string.Empty;
        var pezzi = serverRelativeUrl.Trim('/').Split('/');
        // .../<libreria>/<cartella>/<file>: servono almeno quattro pezzi perché una cartella ci sia.
        return pezzi.Length >= 4 ? pezzi[pezzi.Length - 2] : string.Empty;
    }

    private static bool PiuRecenteDi(SharePointItem consegna, SharePointItem portatore)
    {
        if (consegna.Id == portatore.Id) return false;
        if (!DateTimeOffset.TryParse(consegna.Modified, out var quando)) return false;
        if (!DateTimeOffset.TryParse(portatore.Modified, out var riferimento)) return false;
        return quando - riferimento > TimeSpan.FromMinutes(2);
    }
}

