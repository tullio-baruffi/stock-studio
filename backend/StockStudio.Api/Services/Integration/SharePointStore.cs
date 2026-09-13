using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Microsoft.SharePoint.Client;
using PnP.Framework;
using IoFile = System.IO.File;

namespace StockStudio.Api.Services.Integration;

/// <summary>
/// SharePoint back-office store. Reuses the resize-image certificate app-only auth
/// (AuthenticationManager with ClientId + .pfx + Tenant) to drop generated assets into a library.
/// </summary>
public class SharePointStore
{
    private readonly PipelineSettings _s;
    private readonly ILogger<SharePointStore> _log;
    private AuthenticationManager? _authManager;
    private readonly object _authLock = new();

    public SharePointStore(IOptions<PipelineSettings> s, ILogger<SharePointStore> log)
    {
        _s = s.Value;
        _log = log;
    }

    private ClientContext CreateContext()
    {
        if (string.IsNullOrWhiteSpace(_s.SiteUrl) || string.IsNullOrWhiteSpace(_s.ClientId)
            || string.IsNullOrWhiteSpace(_s.Tenant))
            throw new InvalidOperationException("SharePoint non configurato (Pipeline: SiteUrl/ClientId/Tenant).");

        // Reuse a single AuthenticationManager: it owns the certificate + MSAL token cache.
        // Creating one per request (the dashboards poll every few seconds) leaked handles.
        lock (_authLock)
        {
            _authManager ??= BuildAuthManager();
            return _authManager.GetContext(_s.SiteUrl);
        }
    }

    /// <summary>
    /// Certificate first from configuration (Key Vault delivers it as base64, which is what a
    /// deployed instance uses), otherwise from a file on disk for local runs. A path baked into
    /// configuration does not exist on App Service, so the base64 form is the deployable one.
    /// </summary>
    private AuthenticationManager BuildAuthManager()
    {
        if (!string.IsNullOrWhiteSpace(_s.CertificateBase64))
        {
            var raw = Convert.FromBase64String(_s.CertificateBase64.Trim());
            var cert = new X509Certificate2(raw, _s.CertificatePassword ?? string.Empty,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
            _log.LogInformation("SharePoint: certificato caricato dalla configurazione (scadenza {Exp:yyyy-MM-dd})", cert.NotAfter);
            return new AuthenticationManager(_s.ClientId, cert, _s.Tenant);
        }

        if (string.IsNullOrWhiteSpace(_s.CertificatePath))
            throw new InvalidOperationException("Certificato SharePoint assente: imposta Pipeline:CertificateBase64 oppure Pipeline:CertificatePath.");
        if (!IoFile.Exists(_s.CertificatePath))
            throw new FileNotFoundException($"Certificato non trovato: {_s.CertificatePath}");

        _log.LogInformation("SharePoint: certificato caricato da file {Path}", _s.CertificatePath);
        return new AuthenticationManager(_s.ClientId, _s.CertificatePath, _s.CertificatePassword ?? string.Empty, _s.Tenant);
    }

    /// <summary>Verifies auth + returns the site title. Read-only connectivity probe.</summary>
    public string ProbeConnection()
    {
        using var ctx = CreateContext();
        ctx.Load(ctx.Web, w => w.Title, w => w.ServerRelativeUrl);
        ctx.ExecuteQuery();
        return $"{ctx.Web.Title} ({ctx.Web.ServerRelativeUrl})";
    }

    /// <summary>Read-only: lists document libraries (title, root folder URL, item count) to help locate the watched drop folder.</summary>
    public IReadOnlyList<object> ListDocumentLibraries()
    {
        using var ctx = CreateContext();
        var lists = ctx.Web.Lists;
        ctx.Load(lists, ls => ls.Where(l => l.BaseTemplate == 101 && !l.Hidden)
            .Include(l => l.Title, l => l.Id, l => l.ItemCount, l => l.RootFolder.ServerRelativeUrl));
        ctx.ExecuteQuery();

        return lists
            .Select(l => (object)new
            {
                title = l.Title,
                id = l.Id.ToString(),
                url = l.RootFolder.ServerRelativeUrl,
                itemCount = l.ItemCount,
            })
            .ToList();
    }

    /// <summary>
    /// Read-only: editable fields of a library. Used to confirm the column names the pipeline
    /// depends on (Title, Tags, OData__ExtendedDescription, Stato, Invia, Inviato) really exist.
    /// </summary>
    public IReadOnlyList<object> ListFields(string listTitle)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        ctx.Load(list.Fields, fs => fs.Include(f => f.InternalName, f => f.Title, f => f.TypeAsString,
                                               f => f.Hidden, f => f.ReadOnlyField));
        ctx.ExecuteQuery();

        return list.Fields
            .Where(f => !f.ReadOnlyField)
            .Select(f => (object)new
            {
                name = f.InternalName,
                label = f.Title,
                type = f.TypeAsString,
                hidden = f.Hidden,
            })
            .ToList();
    }

    /// <summary>Read-only: most-recent N file names in a server-relative folder, to learn the expected input format.</summary>
    public IReadOnlyList<string> ListFolderFiles(string folderServerRelativeUrl, int take)
    {
        using var ctx = CreateContext();
        var folder = ctx.Web.GetFolderByServerRelativeUrl(folderServerRelativeUrl);
        var files = folder.Files;
        ctx.Load(files, fs => fs.Include(f => f.Name, f => f.TimeLastModified));
        ctx.ExecuteQuery();
        return files
            .OrderByDescending(f => f.TimeLastModified)
            .Take(take)
            .Select(f => f.Name)
            .ToList();
    }

    /// <summary>Uploads one file into <paramref name="folderServerRelativeUrl"/>, returning its URL + list item id.</summary>
    public SharePointUploadResult UploadFile(Stream content, string fileName, string folderServerRelativeUrl)
    {
        using var ctx = CreateContext();
        var web = ctx.Web;
        ctx.Load(web);
        ctx.ExecuteQuery();

        web.EnsureFolderPath(folderServerRelativeUrl);
        var folder = web.GetFolderByServerRelativeUrl(folderServerRelativeUrl);
        ctx.Load(folder);
        ctx.ExecuteQuery();

        if (content.CanSeek) content.Position = 0;
        var info = new FileCreationInformation
        {
            ContentStream = content,
            Url = fileName,
            Overwrite = true,
        };
        var uploaded = folder.Files.Add(info);
        ctx.Load(uploaded, f => f.ServerRelativeUrl, f => f.Name);
        ctx.Load(uploaded.ListItemAllFields);
        ctx.ExecuteQuery();

        var itemId = uploaded.ListItemAllFields.Id;
        _log.LogInformation("Uploaded {File} to SharePoint ({Url}) item {Id}", fileName, uploaded.ServerRelativeUrl, itemId);
        return new SharePointUploadResult(uploaded.ServerRelativeUrl, itemId, uploaded.Name);
    }

    /// <summary>Read-only: locates a file across the pipeline stage libraries by exact name (indexed CAML on FileLeafRef).</summary>
    public IReadOnlyList<object> TrackFile(string fileName)
    {
        using var ctx = CreateContext();
        var stages = new (string list, string label)[]
        {
            ("ImagesToClassify", "In attesa AI"),
            ("ImagesToSend", "Post-AI, pre-invio"),
            ("ImagesSent", "Pubblicati"),
        };

        // RecursiveAll perche' il percorso durevole deposita i file in una sottocartella per
        // immagine: una query limitata alla radice li darebbe per assenti anche quando ci sono.
        var caml = "<View Scope='RecursiveAll'><Query><Where><Eq><FieldRef Name='FileLeafRef'/>" +
                   $"<Value Type='Text'>{System.Security.SecurityElement.Escape(fileName)}</Value></Eq></Where></Query>" +
                   "<RowLimit>3</RowLimit></View>";

        var results = new List<object>();
        foreach (var (listName, label) in stages)
        {
            try
            {
                var list = ctx.Web.Lists.GetByTitle(listName);
                var items = list.GetItems(new CamlQuery { ViewXml = caml });
                ctx.Load(items, c => c.Include(i => i["FileLeafRef"], i => i["Modified"]));
                ctx.ExecuteQuery();

                var first = items.FirstOrDefault();
                results.Add(new
                {
                    stage = listName,
                    label,
                    found = first != null,
                    modified = first?["Modified"]?.ToString(),
                });
            }
            catch (Exception ex)
            {
                results.Add(new { stage = listName, label, found = false, error = ex.Message });
            }
        }
        return results;
    }

    /// <summary>Read-only: true if a file with this exact name already exists in ImagesSent (already-published duplicate).</summary>
    public bool IsAlreadyPublished(string fileName)
    {
        using var ctx = CreateContext();
        var caml = "<View><Query><Where><Eq><FieldRef Name='FileLeafRef'/>" +
                   $"<Value Type='Text'>{System.Security.SecurityElement.Escape(fileName)}</Value></Eq></Where></Query>" +
                   "<RowLimit>1</RowLimit></View>";
        var list = ctx.Web.Lists.GetByTitle("ImagesSent");
        var items = list.GetItems(new CamlQuery { ViewXml = caml });
        ctx.Load(items, c => c.Include(i => i["FileLeafRef"]));
        ctx.ExecuteQuery();
        return items.Any();
    }

    // ------------------------------------------------------------------ back-office

    /// <summary>
    /// Column internal names used by the pipeline. CSOM exposes the description column as
    /// "_ExtendedDescription"; the Logic App connector shows the same field OData-escaped as
    /// "OData__ExtendedDescription". Same column, different API convention.
    /// </summary>
    private const string FieldDescription = "_ExtendedDescription";

    /// <summary>
    /// La colonna dove si deposita il punteggio calcolato.
    ///
    /// Il punteggio è un dato derivato -- si ricava da titolo, descrizione e keyword -- e i dati
    /// derivati invecchiano appena cambia ciò da cui derivano. Si salva lo stesso perché senza una
    /// colonna non si può chiedere a SharePoint "dammi i pubblicati sotto settanta": si può solo
    /// scaricare tutta la libreria e contare qui, che su diecimila file non è una domanda, è una
    /// mattinata. Con la colonna diventa una query, e per giunta ordinabile.
    ///
    /// Contro l'invecchiamento c'è una sola difesa che funziona davvero: ricalcolarlo comunque a
    /// ogni lettura -- costa un sesto di millisecondo per elemento, misurato -- e riscriverlo solo
    /// quando il valore salvato non corrisponde. Così la colonna serve a filtrare, ma non è mai
    /// lei a decidere cosa si vede.
    /// </summary>
    public const string FieldScore = "Punteggio";

    private static string? Str(ListItem i, string field) =>
        i.FieldValues.TryGetValue(field, out var v) ? v?.ToString() : null;

    private static int? Num(ListItem i, string field) =>
        i.FieldValues.TryGetValue(field, out var v) && v != null
        && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
                           System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? (int)Math.Round(d)
            : null;

    private static bool Flag(ListItem i, string field) =>
        i.FieldValues.TryGetValue(field, out var v) && v is bool b && b;

    /// <summary>
    /// Who holds the file checked out, empty when nobody does. SharePoint refuses every metadata
    /// write on a checked-out file, and the raw failure ("il file è stato estratto per la modifica
    /// da i:0#.f|membership|...") says nothing about how to recover, so the state is surfaced.
    /// </summary>
    private static string CheckedOutBy(ListItem i) =>
        i.FieldValues.TryGetValue("CheckoutUser", out var v) && v is FieldUserValue u
            ? (u.LookupValue ?? u.Email ?? "sconosciuto")
            : "";

    /// <summary>
    /// Stops a write that SharePoint would reject anyway, with a message that names the holder and
    /// the way out. Called before the paid AI call too, so a locked file costs nothing.
    /// </summary>
    private static void EnsureWritable(ListItem i)
    {
        var holder = CheckedOutBy(i);
        if (holder.Length == 0) return;
        throw new InvalidOperationException(
            $"File estratto per la modifica da {holder}: SharePoint rifiuta ogni scrittura di " +
            "metadati finché resta così. Usa \"Archivia\" sulla scheda per sbloccarlo.");
    }

    /// <summary>
    /// One page of library items with the metadata the author reviews.
    ///
    /// Due strategie, perche' nessuna delle due basta da sola. Quella piatta ('RecursiveAll' con un
    /// cursore sull'Id) da' pagine piene, ma su una libreria oltre i 5000 elementi SharePoint la
    /// rifiuta: deve considerare l'intera lista e si ferma sulla soglia. Quella per cartelle
    /// ('Recursive' col cursore opaco) regge qualunque dimensione ma impagina dentro ogni cartella,
    /// e in una libreria di sottocartelle restituisce una manciata di righe per volta.
    ///
    /// Si prova quindi la prima e si ripiega sulla seconda, che e' come funzionava prima: la
    /// libreria grande torna sfogliabile com'era, le altre migliorano. Il cursore porta un prefisso
    /// perche' i due formati non si confondano fra una pagina e l'altra.
    /// </summary>
    /// <summary>
    /// Cerca nell'intera libreria con il motore di ricerca di SharePoint invece che con una query
    /// sulla lista.
    ///
    /// Perché non basta CAML: su una libreria oltre i cinquemila elementi SharePoint rifiuta
    /// qualunque filtro su una colonna non indicizzata, e il nome del file non è indicizzabile
    /// perché è un campo di sistema. Il risultato era che cercare per nome funzionava solo finché
    /// la libreria era piccola, e poi restituiva un errore di soglia -- che l'applicazione doveva
    /// tradurre in italiano perché il messaggio originale non diceva niente di utile. In più
    /// BeginsWith trova solo i prefissi: cercare "danza" non trovava "silhouette_danza_045".
    ///
    /// L'indice di ricerca non ha soglie e cerca dentro le parole, quindi risponde alla domanda che
    /// si voleva fare davvero. Il prezzo è il ritardo di indicizzazione: un file appena modificato
    /// può non comparire per qualche minuto, e chi guarda va avvertito.
    ///
    /// La ricerca restituisce gli identificativi; i valori delle colonne si rileggono dalla lista in
    /// una sola query per identificativo, che è indicizzato e quindi immune alla soglia.
    /// </summary>
    /// <summary>
    /// La proprietà gestita numerica su cui l'indice sa fare confronti d'ordine.
    ///
    /// SharePoint crea da sé una proprietà gestita per ogni colonna -- per il punteggio è
    /// PunteggioOWSNMBR -- ma è **di tipo testo**: risponde all'uguaglianza e tace sui confronti.
    /// Provato: "PunteggioOWSNMBR:84" trova, "PunteggioOWSNMBR>70" restituisce zero senza errore.
    ///
    /// I tipi numerici non si possono creare: nemmeno l'amministratore del tenant può, il tipo
    /// "Intero" è disabilitato in entrambi i livelli. SharePoint Online offre invece cinquanta
    /// proprietà predefinite già intere -- RefinableInt00..49 -- da mappare alla colonna.
    ///
    /// Il collegamento va fatto **a livello di raccolta siti**, non di tenant: le proprietà
    /// indicizzate generate dalle colonne di sito (qui ows_q_NMBR_Punteggio) esistono solo lì, e
    /// a livello tenant il selettore resta vuoto per qualsiasi ricerca. Fatto il 26/08/2026.
    ///
    /// Finché quel collegamento non esiste, questa proprietà non risponde e il filtro resta sulla
    /// lista, dove funziona ma non può selezionare più di cinquemila elementi per volta.
    /// </summary>
    private const string FieldScoreIndicizzato = "RefinableInt00";

    private (bool Disponibile, DateTimeOffset Quando)? _indiceNumerico;

    /// <summary>
    /// Se l'indice sa già rispondere a un confronto d'ordine sul punteggio.
    ///
    /// La verifica è per assurdo, e non poteva essere altrimenti: chiedere all'indice se la
    /// proprietà "esiste" non funziona, perché **una clausola su una proprietà non mappata viene
    /// semplicemente ignorata** invece di dare errore. Misurato: con RefinableInt00 non collegata,
    /// "RefinableInt00&lt;70" e "RefinableInt00&gt;=90" restituivano entrambe l'intera libreria --
    /// due insiemi disgiunti che contengono entrambi tutto, cioè la prova che il filtro non c'era.
    ///
    /// Un controllo ingenuo avrebbe detto "pronto" e il filtro sarebbe passato all'indice,
    /// restituendo risultati non filtrati con l'aria di essere filtrati: peggio di non filtrare.
    ///
    /// Qui invece si chiede una cosa che non può essere vera -- un punteggio oltre il milione --
    /// e si pretende zero. Se torna qualcosa, la clausola è stata ignorata e la proprietà non c'è.
    ///
    /// L'impossibile va cercato verso l'alto, non verso il basso. Il primo tentativo chiedeva un
    /// punteggio negativo, e non ha mai detto "pronto" nemmeno a collegamento avvenuto: **un
    /// elemento senza punteggio soddisfa i confronti verso il basso**. Misurato su ImagesToSend,
    /// che contiene immagini non ancora classificate: "&lt;=59" rendeva 74 righe, di cui 59 con la
    /// proprietà vuota, mentre "&gt;=0 AND &lt;=59" ne rendeva 15 e nessuna vuota. Le immagini
    /// senza punteggio esistono per costruzione, quindi il vecchio controllo bocciava un indice
    /// sano. Verso l'alto invece i vuoti non rispondono: "&gt;1000000" rende zero.
    ///
    /// La risposta cambia una volta sola nella vita del sito, quindi si ricontrolla ogni tanto.
    /// </summary>
    public bool PunteggioIndicizzato(string listTitle)
    {
        if (_indiceNumerico is { } c && DateTimeOffset.UtcNow - c.Quando < TimeSpan.FromMinutes(15))
            return c.Disponibile;

        var disponibile = false;
        try
        {
            using var ctx = CreateContext();
            var cartella = $"{_s.SiteUrl!.TrimEnd('/')}/{listTitle}";

            int Righe(string clausola)
            {
                var kq = new Microsoft.SharePoint.Client.Search.Query.KeywordQuery(ctx)
                {
                    QueryText = $"Path:\"{cartella}\" AND IsContainer:false AND {clausola}",
                    RowLimit = 1,
                    TrimDuplicates = false,
                };
                kq.SelectProperties.Add("ListItemID");
                var r = new Microsoft.SharePoint.Client.Search.Query.SearchExecutor(ctx).ExecuteQuery(kq);
                ctx.ExecuteQuery();
                return r.Value?.FirstOrDefault()?.TotalRows ?? 0;
            }

            // Nessun punteggio supera il milione: se questa trova qualcosa, la clausola è stata
            // ignorata. Il confronto è verso l'alto perché i vuoti soddisfano quelli verso il basso.
            var impossibile = Righe($"{FieldScoreIndicizzato}>1000000");
            // E qualcosa deve pur esserci, altrimenti la proprietà c'è ma è vuota.
            var possibile = impossibile == 0 ? Righe($"{FieldScoreIndicizzato}>=0") : 0;

            disponibile = impossibile == 0 && possibile > 0;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Indice numerico del punteggio non interrogabile su {List}", listTitle);
        }

        _indiceNumerico = (disponibile, DateTimeOffset.UtcNow);
        if (disponibile) _log.LogInformation("{Prop} risponde: il filtro per punteggio può usare l'indice", FieldScoreIndicizzato);
        return disponibile;
    }

    public SharePointPage SearchItems(string listTitle, string? query, int take, int startRow = 0,
                                      bool perStato = false, int? punteggioMin = null, int? punteggioMax = null)
    {
        var wanted = Math.Clamp(take, 1, 100);
        using var ctx = CreateContext();

        var cartella = $"{_s.SiteUrl!.TrimEnd('/')}/{listTitle}";

        // Le cartelle si escludono nella query invece di scartarle dopo.
        //
        // Prima si chiedevano sei righe in più del necessario e si buttavano quelle che non erano
        // file: funzionava, ma costava righe inutili a ogni pagina e obbligava a far avanzare il
        // cursore sulle righe consumate invece che sulle immagini rese -- un conto facile da
        // sbagliare, e sbagliandolo si perdono file senza accorgersene. Chiedere direttamente ciò
        // che serve toglie il problema invece di gestirlo.
        var filtri = new List<string> { $"Path:\"{cartella}\"", "IsContainer:false" };

        if (!string.IsNullOrWhiteSpace(query))
        {
            if (perStato)
            {
                // La colonna Stato è indicizzata come proprietà gestita, con il suffisso che
                // SharePoint aggiunge da sé alle colonne di sito. Il valore va fra virgolette
                // perché contiene spazi e due punti.
                filtri.Add($"StatoOWSTEXT:\"{query.Trim().Replace("\"", "\"\"")}\"");
            }
            else
            {
                // Il testo va fra virgolette solo se contiene spazi; le virgolette interne si
                // raddoppiano, altrimenti una sola vira l'intera query in sintassi non valida.
                var termine = query.Trim().Replace("\"", "\"\"");
                if (termine.Contains(' ')) termine = $"\"{termine}\"";
                filtri.Add(termine);
            }
        }

        // Il punteggio come intervallo, quando l'indice sa farlo: qui non esiste la soglia dei
        // cinquemila, quindi qualunque fascia funziona -- anche quella che contiene quasi tutto.
        //
        // L'intervallo si scrive con due confronti e non con range(): su questa proprietà range()
        // rende zero righe sempre, senza dare errore -- misurato, "RefinableInt00:range(0,100)"
        // non trova niente mentre "RefinableInt00>=0" trova tutto.
        //
        // L'estremo inferiore c'è sempre, anche quando l'utente chiede solo un massimo: senza di
        // esso rientrerebbero le immagini prive di punteggio, che soddisfano i confronti verso il
        // basso. Misurato su ImagesToSend: "<=59" rendeva 74 righe, "<=59 AND >=0" ne rende 15.
        if (punteggioMin is not null || punteggioMax is not null)
        {
            filtri.Add($"{FieldScoreIndicizzato}>={punteggioMin ?? 0}");
            if (punteggioMax is not null)
                filtri.Add($"{FieldScoreIndicizzato}<={punteggioMax}");
        }

        var kql = string.Join(" AND ", filtri);

        var kq = new Microsoft.SharePoint.Client.Search.Query.KeywordQuery(ctx)
        {
            QueryText = kql,
            RowLimit = wanted,
            StartRow = Math.Max(0, startRow),
            TrimDuplicates = false,
        };
        kq.SelectProperties.Add("ListItemID");
        kq.SelectProperties.Add("Path");
        kq.SelectProperties.Add("Title");

        // Senza testo non esiste una pertinenza: l'ordine utile è il più recente per primo, che è
        // anche quello che la lista dava prima. Con un testo invece si lascia decidere all'indice.
        if (string.IsNullOrWhiteSpace(query))
            kq.SortList.Add("LastModifiedTime", Microsoft.SharePoint.Client.Search.Query.SortDirection.Descending);

        var executor = new Microsoft.SharePoint.Client.Search.Query.SearchExecutor(ctx);
        var risultati = executor.ExecuteQuery(kq);
        ctx.ExecuteQuery();

        var tabella = risultati.Value?.FirstOrDefault();
        var righe = tabella?.ResultRows?.ToList() ?? new();
        var totale = tabella?.TotalRows ?? righe.Count;

        var ids = new List<int>();
        foreach (var r in righe)
            if (r.TryGetValue("ListItemID", out var v) && int.TryParse(v?.ToString(), out var id))
                ids.Add(id);

        // Il cursore avanza di quante righe ha reso l'indice: escluse le cartelle dalla query, ogni
        // riga è un'immagine e le due grandezze coincidono.
        var prossimo = startRow + righe.Count < totale ? $"sr:{startRow + righe.Count}" : null;

        if (ids.Count == 0)
            return new SharePointPage(new List<SharePointItem>(), prossimo, righe.Count, righe.Count, "ricerca");

        var items = ItemsByIds(ctx, listTitle, ids);

        // L'ordine dato dall'indice va conservato: rileggendo per identificativo si otterrebbe
        // quello della lista, che non ha niente a che vedere né con la pertinenza né con la data.
        var perId = items.ToDictionary(x => x.Id);
        var ordinati = ids.Where(perId.ContainsKey).Select(id => perId[id]).ToList();

        _log.LogInformation("Indice su {List}: '{Q}' -> {N} di {Tot}", listTitle, query ?? "(elenco)", ordinati.Count, totale);
        return new SharePointPage(ordinati, prossimo, righe.Count, righe.Count - ordinati.Count, "ricerca");
    }

    /// <summary>
    /// Verifica se il motore di ricerca risponde a questa identità, e cosa risponde.
    ///
    /// Serve perché una ricerca che non trova niente e una ricerca che non è permessa si
    /// assomigliano: entrambe restituiscono zero righe senza errore.
    /// </summary>
    public (bool Ok, int TotalRows, int Righe, string Kql, string? Errore, List<string> Campioni) DiagnosticaRicerca(string listTitle, string query, string? select = null)
    {
        var cartella = $"{_s.SiteUrl!.TrimEnd('/')}/{listTitle}";
        var kql = string.IsNullOrWhiteSpace(query)
            ? $"Path:\"{cartella}\""
            : $"Path:\"{cartella}\" AND {query}";
        var campioni = new List<string>();
        try
        {
            using var ctx = CreateContext();
            var kq = new Microsoft.SharePoint.Client.Search.Query.KeywordQuery(ctx)
            {
                QueryText = kql,
                RowLimit = 5,
                TrimDuplicates = false,
            };
            kq.SelectProperties.Add("ListItemID");
            kq.SelectProperties.Add("Path");
            foreach (var p in (select ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                kq.SelectProperties.Add(p.Trim());

            var ex = new Microsoft.SharePoint.Client.Search.Query.SearchExecutor(ctx);
            var res = ex.ExecuteQuery(kq);
            ctx.ExecuteQuery();

            var t = res.Value?.FirstOrDefault();
            var righe = t?.ResultRows?.ToList() ?? new();
            foreach (var r in righe.Take(3))
                campioni.Add(string.Join(" | ", r.Select(kv => $"{kv.Key}={kv.Value}")));

            return (true, t?.TotalRows ?? 0, righe.Count, kql, null, campioni);
        }
        catch (Exception e)
        {
            return (false, 0, 0, kql, e.Message, campioni);
        }
    }

    /// <summary>
    /// Le sole colonne che servono a costruire un elemento.
    ///
    /// Chiederle esplicitamente non è un dettaglio: senza questo elenco SharePoint restituisce
    /// ogni colonna di ogni riga, e sulla libreria dei pubblicati la stessa identica query
    /// passava da centosessanta millisecondi a ventuno secondi. Il costo non era nel modo di
    /// selezionare le righe -- come sembrava -- ma nella quantità di dati richiesti per ciascuna.
    /// </summary>
    private static readonly string[] ColonneUtili =
    {
        "ID", "FSObjType", "FileLeafRef", "FileRef", "Title", FieldDescription,
        "Tags", "Stato", "Invia", "Inviato", "Modified", "Created", "CheckoutUser", FieldScore,
    };

    private static string ViewFields() =>
        "<ViewFields>" + string.Concat(ColonneUtili.Select(c => $"<FieldRef Name='{c}'/>")) + "</ViewFields>";

    /// <summary>
    /// Legge un gruppo di elementi per identificativo.
    ///
    /// Una sola andata, con l'elenco delle colonne: l'identificativo è indicizzato, quindi non
    /// c'è scansione e la soglia dei cinquemila elementi non entra in gioco.
    /// </summary>
    private List<SharePointItem> ItemsByIds(ClientContext ctx, string listTitle, IReadOnlyList<int> ids)
    {
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var valori = string.Concat(ids.Select(i => $"<Value Type='Counter'>{i}</Value>"));

        var caml = new CamlQuery
        {
            ViewXml = $"<View Scope='RecursiveAll'>{ViewFields()}<Query><Where><In>" +
                      $"<FieldRef Name='ID'/><Values>{valori}</Values></In></Where></Query>" +
                      $"<RowLimit>{ids.Count}</RowLimit></View>",
        };

        var items = list.GetItems(caml);
        ctx.Load(items);
        ctx.ExecuteQuery();

        return items.Where(i => !string.Equals(Str(i, "FSObjType"), "1", StringComparison.Ordinal))
                    .Select(ToItem)
                    .ToList();
    }

    /// <summary>
    /// Scorrimento sequenziale per il riempimento, senza ripieghi.
    ///
    /// ListItems, se la lettura dalla lista non riesce, ripiega sull'indice ripartendo da riga
    /// zero: per la galleria è la scelta giusta -- meglio una pagina imperfetta che una schermata
    /// di errore -- ma per chi sta scorrendo una libreria dall'inizio alla fine è un disastro
    /// silenzioso, perché ricomincia da capo e il cursore torna indietro senza che nessuno lo
    /// dica. È così che il riempimento dei pubblicati è arrivato a diciottomila elementi visitati
    /// su una libreria che ne ha diecimila.
    ///
    /// Qui invece un errore è un errore: chi chiama lo vede e riprova la stessa pagina.
    /// </summary>
    public SharePointPage ScorriPerRiempimento(string listTitle, int take, string? pageToken) =>
        ListFlat(listTitle, take, pageToken, null, null);

    /// <summary>
    /// Una pagina di elenco. Due vie, scelte in base alla domanda e non alla dimensione.
    ///
    /// Scorrere e cercare non sono la stessa operazione, e conviene servirle in modo diverso.
    ///
    /// Per scorrere basta la lista: il cursore è l'identificativo dell'ultima riga vista, che è
    /// indicizzato, quindi non c'è scansione e la soglia non entra in gioco. Misurato a cache
    /// fredda sulla libreria dei pubblicati: 174 ms contro i 646 dell'indice, e scendendo fino a
    /// seimila elementi -- ben oltre la soglia dei cinquemila -- il costo resta piatto sui 316 ms.
    /// In più la lista è sempre aggiornata, mentre l'indice ha il suo ritardo.
    ///
    /// Per cercare invece la lista non basta: CAML confronta stringhe, quindi trova solo i
    /// prefissi, e un filtro per stato su una libreria da diecimila file supera la soglia perché
    /// lo stato non discrimina abbastanza. Lì serve l'indice.
    ///
    /// Le due vie danno risultati completi entrambe -- nessuna delle due si ferma ai primi
    /// cinquemila -- quindi la base dati non cambia con la domanda: cambia solo l'ordine, per
    /// identificativo qui e per pertinenza o data là, che è quello che ci si aspetta cercando.
    /// </summary>
    public SharePointPage ListItems(string listTitle, int take, string? pageToken, string? search, string? field = null,
                                    int? punteggioMin = null, int? punteggioMax = null)
    {
        var conRicerca = !string.IsNullOrWhiteSpace(search);
        var perPunteggio = punteggioMin is not null || punteggioMax is not null;
        var daIndice = pageToken?.StartsWith("sr:", StringComparison.Ordinal) == true;
        var perCartelle = pageToken?.StartsWith("sp:", StringComparison.Ordinal) == true;

        // Scorrere l'elenco, con o senza filtro per punteggio: la lista, che è più rapida e più
        // fresca. Il punteggio è una colonna numerica indicizzata, quindi il confronto non
        // scandisce -- ma SharePoint rifiuta comunque di restituire un insieme che supera i
        // cinquemila elementi, e su una fascia larga succede.
        if (!conRicerca && !daIndice && !perCartelle)
        {
            try
            {
                return ListFlat(listTitle, take, pageToken, null, null, punteggioMin, punteggioMax);
            }
            catch (Exception ex) when (perPunteggio && IsThreshold(ex) && PunteggioIndicizzato(listTitle))
            {
                // La fascia è troppo larga per la lista, ma l'indice sa rispondere: l'indice non
                // ha soglie. Si passa di là, accettando il suo ritardo di aggiornamento.
                _log.LogInformation("{List}: fascia di punteggio oltre la soglia, si passa dall'indice", listTitle);
                return SearchItems(listTitle, null, take, 0, false, punteggioMin, punteggioMax);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "{List}: elenco dalla lista non riuscito, si passa dall'indice", listTitle);
            }
        }

        var perStato = (field ?? "").Equals("stato", StringComparison.OrdinalIgnoreCase) && conRicerca;
        var da = daIndice && int.TryParse(pageToken![3..], out var s) ? s : 0;

        // Un filtro per punteggio ha senso sull'indice solo se lì il punteggio è un numero. Se non
        // lo è ancora, chiedere comunque restituirebbe zero righe -- e zero righe è una risposta
        // che sembra vera. Meglio dire che non si può.
        if (perPunteggio && !PunteggioIndicizzato(listTitle))
            throw new InvalidOperationException(
                "Il filtro per punteggio non è disponibile su questa libreria: la fascia scelta " +
                "contiene più di 5.000 immagini e SharePoint rifiuta di filtrarle. Scegli una " +
                "fascia più stretta.");

        try
        {
            return SearchItems(listTitle, search, take, da, perStato, punteggioMin, punteggioMax);
        }
        catch (Exception ex)
        {
            // L'indice può essere spento o non ancora popolato su un sito nuovo: in quel caso si
            // torna alla query sulla lista, che sulla ricerca trova meno ma qualcosa restituisce.
            _log.LogWarning(ex, "Indice non disponibile su {List}: ripiego su CAML", listTitle);
        }

        if (perCartelle)
            return ListByFolder(listTitle, take, pageToken![3..], search, field);

        try
        {
            return ListFlat(listTitle, take, pageToken, search, field, punteggioMin, punteggioMax);
        }
        catch (Exception ex) when (IsThreshold(ex))
        {
            _log.LogInformation(
                "{List}: elenco piatto oltre la soglia, ripiego sull'impaginazione per cartelle", listTitle);
            return ListByFolder(listTitle, take, null, search, field);
        }
    }

    private static bool IsThreshold(Exception ex) =>
        ex.Message.Contains("soglia", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("threshold", StringComparison.OrdinalIgnoreCase);

    /// <summary>La strategia precedente: cursore opaco di SharePoint, impagina per cartella.</summary>
    private SharePointPage ListByFolder(string listTitle, int take, string? pagingInfo, string? search, string? field)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);

        var caml = new CamlQuery
        {
            ViewXml = "<View Scope='Recursive'>" + ViewFields() + "<Query>" + BuildWhere(search, field, null) +
                      "<OrderBy><FieldRef Name='ID' Ascending='FALSE'/></OrderBy></Query>" +
                      $"<RowLimit>{Math.Clamp(take, 1, 100)}</RowLimit></View>",
        };
        if (!string.IsNullOrWhiteSpace(pagingInfo))
            caml.ListItemCollectionPosition = new ListItemCollectionPosition { PagingInfo = pagingInfo };

        var items = list.GetItems(caml);
        ctx.Load(items);
        ctx.ExecuteQuery();

        var all = items.ToList();
        var rows = all
            .Where(i => !string.Equals(Str(i, "FSObjType"), "1", StringComparison.Ordinal))
            .Select(ToItem)
            .ToList();

        var next = items.ListItemCollectionPosition?.PagingInfo;
        return new SharePointPage(rows, next == null ? null : "sp:" + next,
                                  all.Count, all.Count - rows.Count, "cartelle");
    }

    /// <summary>
    /// Compone la clausola CAML dalle condizioni attive.
    ///
    /// Le condizioni si annidano a due a due perché &lt;And&gt; in CAML ne accetta esattamente due:
    /// scriverne tre di fila non è un errore di sintassi, SharePoint semplicemente ignora la terza.
    /// Prima qui c'era una scelta a due casi che con tre condizioni ne perdeva una senza dirlo --
    /// un filtro che non filtra, che è peggio di un filtro che fallisce.
    /// </summary>
    private static string BuildWhere(string? search, string? field, string? idCondition,
                                     int? punteggioMin = null, int? punteggioMax = null)
    {
        var conditions = new List<string>();
        if (idCondition != null) conditions.Add(idCondition);

        if (punteggioMin is int min)
            conditions.Add($"<Geq><FieldRef Name='{FieldScore}'/><Value Type='Number'>{min}</Value></Geq>");
        if (punteggioMax is int max)
            conditions.Add($"<Leq><FieldRef Name='{FieldScore}'/><Value Type='Number'>{max}</Value></Leq>");

        if (!string.IsNullOrWhiteSpace(search))
        {
            var safe = System.Security.SecurityElement.Escape(search.Trim());
            // BeginsWith and Eq are the only operators that can use an index; Contains always
            // scans, so it is reserved for the columns and libraries where scanning is allowed.
            conditions.Add((field ?? "name").ToLowerInvariant() switch
            {
                "title" => $"<BeginsWith><FieldRef Name='Title'/><Value Type='Text'>{safe}</Value></BeginsWith>",
                "stato" => $"<Contains><FieldRef Name='Stato'/><Value Type='Text'>{safe}</Value></Contains>",
                "keyword" => $"<Contains><FieldRef Name='Tags'/><Value Type='Note'>{safe}</Value></Contains>",
                _ => $"<BeginsWith><FieldRef Name='FileLeafRef'/><Value Type='Text'>{safe}</Value></BeginsWith>",
            });
        }

        if (conditions.Count == 0) return "";
        var espressione = conditions[0];
        for (var i = 1; i < conditions.Count; i++)
            espressione = $"<And>{espressione}{conditions[i]}</And>";
        return $"<Where>{espressione}</Where>";
    }

    /// <summary>
    /// Elenco piatto con cursore sull'identificativo: pagine piene e costo costante.
    ///
    /// Sembrava non poter reggere oltre la soglia dei cinquemila elementi, e invece regge: il
    /// confronto è su ID, che è indicizzato, quindi SharePoint non scandisce. Sceso fino a
    /// seimila elementi sulla libreria dei pubblicati senza un errore, con il costo per pagina
    /// fermo intorno ai trecento millisecondi.
    /// </summary>
    private SharePointPage ListFlat(string listTitle, int take, string? pageToken, string? search, string? field,
                                    int? punteggioMin = null, int? punteggioMax = null)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);

        var wanted = Math.Clamp(take, 1, 100);
        var rows = new List<SharePointItem>();
        var cursor = int.TryParse(pageToken, out var afterId) && afterId > 0 ? afterId : int.MaxValue;
        var more = false;
        var scanned = 0;
        var skipped = 0;

        // Le cartelle sono elementi di lista e consumano il RowLimit, ma non si vedono: una
        // libreria fatta di sottocartelle per immagine restituiva una riga visibile per pagina.
        // Filtrarle nella query non si puo' -- FSObjType non e' indicizzabile e oltre la soglia fa
        // fallire tutto -- quindi si legge una finestra piu' larga e, se non basta, si continua.
        // Il numero di giri e' limitato: meglio una pagina corta che una richiesta che non finisce.
        for (var round = 0; round < 6 && rows.Count < wanted; round++)
        {
            var fetch = Math.Min(wanted * 4, 500);
            var where = BuildWhere(search, field,
                $"<Lt><FieldRef Name='ID'/><Value Type='Counter'>{cursor}</Value></Lt>",
                punteggioMin, punteggioMax);

            var caml = new CamlQuery
            {
                // Scope 'Recursive', non 'RecursiveAll'. Sembrano sinonimi e la differenza qui è
                // fra funzionare e non funzionare: con RecursiveAll questa stessa query, sulla
                // libreria dei pubblicati, viene rifiutata per superamento soglia -- anche
                // chiedendo ventiquattro righe -- mentre con Recursive risponde in trecento
                // millisecondi. Provate una accanto all'altra sulle tre librerie: Recursive passa
                // sempre, RecursiveAll passa solo sulle due piccole.
                //
                // Recursive scende comunque nelle sottocartelle -- i file che stanno dentro
                // "Da inviare" e "NB - Mandate" vengono restituiti -- ma non restituisce le
                // cartelle come righe, che è esattamente quello che serve: erano loro a consumare
                // il limite di riga e a far tornare pagine quasi vuote.
                //
                // Ordine per Id discendente: mostra le più recenti per prime ed è più stabile di
                // Modified, che rimescolerebbe la pagina proprio mentre la si revisiona.
                ViewXml = "<View Scope='Recursive'>" + ViewFields() + "<Query>" + where +
                          "<OrderBy><FieldRef Name='ID' Ascending='FALSE'/></OrderBy></Query>" +
                          $"<RowLimit>{fetch}</RowLimit></View>",
            };

            var items = list.GetItems(caml);
            ctx.Load(items);
            ctx.ExecuteQuery();

            var raw = 0;
            foreach (var i in items)
            {
                raw++;
                cursor = i.Id;
                if (string.Equals(Str(i, "FSObjType"), "1", StringComparison.Ordinal)) { skipped++; continue; }
                rows.Add(ToItem(i));
                if (rows.Count == wanted) break;
            }
            scanned += raw;

            // Finestra esaurita senza arrivare in fondo: c'e' altro piu' sotto, si continua.
            more = rows.Count == wanted || raw == fetch;
            if (raw < fetch) break;
        }

        return new SharePointPage(rows, more && cursor != int.MaxValue ? cursor.ToString() : null,
                                  scanned, skipped, "piatta");
    }

    /// <summary>
    /// A single item by list id. Needed because these libraries hold thousands of files: looking
    /// one up by scanning the first page would miss almost everything.
    /// </summary>
    public SharePointItem GetItem(string listTitle, int id)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var item = list.GetItemById(id);
        ctx.Load(item);
        ctx.ExecuteQuery();
        return ToItem(item);
    }

    /// <summary>
    /// Adds an index to a column so libraries past the 5000-item threshold stay filterable.
    /// Idempotent: an already-indexed column is left alone.
    /// </summary>
    public string EnsureIndexed(string listTitle, string fieldInternalName)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var field = list.Fields.GetByInternalNameOrTitle(fieldInternalName);
        ctx.Load(field, f => f.Indexed, f => f.InternalName, f => f.CanBeDeleted);
        ctx.ExecuteQuery();

        if (field.Indexed)
            return $"'{fieldInternalName}' era già indicizzata su {listTitle}.";

        field.Indexed = true;
        field.Update();
        ctx.ExecuteQuery();
        _log.LogInformation("SharePoint {List}: indice aggiunto su {Field}", listTitle, fieldInternalName);
        return $"Indice creato su '{fieldInternalName}' in {listTitle}.";
    }

    /// <summary>
    /// Quanti elementi contiene la libreria, secondo il conteggio che SharePoint tiene da sé.
    ///
    /// Comprende anche le cartelle, quindi su una libreria che ne ha è qualche unità più alto del
    /// numero di immagini: serve a dire "a che punto siamo", non a fare i conti.
    /// </summary>
    public int ItemCount(string listTitle)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        ctx.Load(list, l => l.ItemCount);
        ctx.ExecuteQuery();
        return list.ItemCount;
    }

    /// <summary>
    /// L'identificativo più basso presente in libreria.
    ///
    /// Serve a chi vuole campionare: il cursore scorre dagli identificativi alti verso i bassi, e
    /// senza sapere dove finisce non si può distribuire un campione lungo tutta la libreria --
    /// si finirebbe per leggere le prime righe e chiamarle campione, che è il modo più facile di
    /// misurare solo i caricamenti più recenti credendo di aver misurato tutto.
    /// </summary>
    public int MinItemId(string listTitle)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var caml = new CamlQuery
        {
            ViewXml = "<View Scope='Recursive'><ViewFields><FieldRef Name='ID'/></ViewFields>" +
                      "<Query><OrderBy><FieldRef Name='ID' Ascending='TRUE'/></OrderBy></Query>" +
                      "<RowLimit>1</RowLimit></View>",
        };
        var items = list.GetItems(caml);
        ctx.Load(items, c => c.Include(i => i.Id));
        ctx.ExecuteQuery();
        return items.Count > 0 ? items[0].Id : 0;
    }

    /// <summary>
    /// Quante immagini stanno in una fascia di punteggio.
    ///
    /// Serve solo a dare le proporzioni del lavoro, quindi si ferma a cinquemila: oltre, SharePoint
    /// rifiuterebbe comunque, e sapere che "sono più di cinquemila" è già la risposta.
    /// </summary>
    public int ContaPerPunteggio(string listTitle, int? min, int? max)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var caml = new CamlQuery
        {
            ViewXml = "<View Scope='Recursive'><ViewFields><FieldRef Name='ID'/></ViewFields><Query>" +
                      BuildWhere(null, null, null, min, max) +
                      "</Query><RowLimit>5000</RowLimit></View>",
        };
        var items = list.GetItems(caml);
        ctx.Load(items);
        ctx.ExecuteQuery();
        return items.Count;
    }

    /// <summary>
    /// Crea la colonna del punteggio se non c'è, e la indicizza. Ripetibile senza danni.
    /// </summary>
    public string EnsureScoreColumn(string listTitle)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var fields = list.Fields;
        ctx.Load(fields, f => f.Include(x => x.InternalName, x => x.Indexed));
        ctx.ExecuteQuery();

        var esistente = fields.FirstOrDefault(f =>
            string.Equals(f.InternalName, FieldScore, StringComparison.OrdinalIgnoreCase));

        if (esistente == null)
        {
            // Number con zero decimali: il punteggio è intero da 0 a 100. Dichiararlo qui evita
            // che SharePoint lo mostri come "84,00000000000000" nelle sue viste.
            var xml = $"<Field Type='Number' DisplayName='{FieldScore}' Name='{FieldScore}' " +
                      "Decimals='0' Min='0' Max='100' Indexed='TRUE' " +
                      "Description='Punteggio metadati calcolato da Stock Vector Studio. Derivato: si ricalcola a ogni lettura.' />";
            list.Fields.AddFieldAsXml(xml, addToDefaultView: false,
                                      options: AddFieldOptions.AddFieldInternalNameHint);
            ctx.ExecuteQuery();
            _log.LogInformation("SharePoint {List}: creata la colonna {Field}", listTitle, FieldScore);
            return $"Colonna '{FieldScore}' creata e indicizzata su {listTitle}.";
        }

        if (esistente.Indexed) return $"Colonna '{FieldScore}' già presente e indicizzata su {listTitle}.";

        esistente.Indexed = true;
        esistente.Update();
        ctx.ExecuteQuery();
        return $"Colonna '{FieldScore}' già presente su {listTitle}, indice aggiunto ora.";
    }

    /// <summary>
    /// Deposita il punteggio, senza toccare nient'altro dell'elemento.
    ///
    /// SystemUpdate e non Update: il punteggio non è una modifica fatta da una persona, e scriverlo
    /// come tale sposterebbe la data di ultima modifica e creerebbe una versione. Su diecimila file
    /// significherebbe diecimila "modificato oggi" falsi e diecimila versioni in più, cancellando
    /// proprio l'informazione -- quando questo file è stato toccato l'ultima volta -- che serve a
    /// capire cosa è stato rivisto e cosa no.
    ///
    /// Restituisce false se l'elemento non era scrivibile, invece di sollevare: in un riempimento
    /// da migliaia di file un solo file estratto per la modifica non deve fermare tutto il resto.
    /// </summary>
    public bool SetScore(ClientContext ctx, string listTitle, int id, int punteggio)
    {
        try
        {
            var list = ctx.Web.Lists.GetByTitle(listTitle);
            var item = list.GetItemById(id);
            item[FieldScore] = punteggio;
            item.SystemUpdate();
            ctx.ExecuteQuery();
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Punteggio non scritto su {List} item {Id}", listTitle, id);
            return false;
        }
    }

    /// <summary>
    /// Scrive più punteggi, dividendo il lavoro fra più connessioni.
    ///
    /// Accodare cento SystemUpdate in un solo ExecuteQuery sembrava già "in blocco", ed è un solo
    /// viaggio di rete -- ma SharePoint quelle cento operazioni le esegue **in fila**, su un thread
    /// solo. Misurato sulla libreria dei pubblicati: circa un secondo per elemento, cioè cento
    /// secondi per pagina.
    ///
    /// Il costo non è la banda, è l'attesa: ogni scrittura è tempo passato ad aspettare SharePoint.
    /// Provate le alternative sullo stesso campione, riscrivendo lo stesso valore per non alterare
    /// niente:
    ///     una dopo l'altra          612 ms per elemento
    ///     quattro in parallelo      108 ms
    ///     otto in parallelo          53 ms
    ///     sedici in parallelo        44 ms
    /// Sessanta scritture su sessanta riuscite, nessuna rifiutata per eccesso di richieste, e le
    /// date di ultima modifica invariate.
    ///
    /// Quindi si divide il lotto fra più connessioni e si accodano poche scritture per volta: un
    /// ExecuteQuery lungo rimetterebbe in fila proprio ciò che si sta cercando di affiancare.
    ///
    /// Il grado di parallelismo è volutamente moderato. Più in alto si guadagna ancora qualcosa, ma
    /// si comincia a chiedere a SharePoint più di quanto sia educato chiedere, e il prezzo di uno
    /// strappo -- il rifiuto per eccesso di richieste, con l'attesa forzata che ne segue -- è più
    /// alto del tempo che si risparmia.
    /// </summary>
    public int SetScores(string listTitle, IReadOnlyDictionary<int, int> punteggi)
    {
        if (punteggi.Count == 0) return 0;

        const int Connessioni = 8;
        const int PerViaggio = 4;

        var tutti = punteggi.ToList();
        if (tutti.Count <= PerViaggio) return ScriviTratto(listTitle, tutti, PerViaggio);

        // Si distribuisce a giro, non a blocchi: se una connessione incontra un file bloccato, il
        // danno resta sparso invece di concentrarsi su un tratto contiguo della libreria.
        var parti = new List<List<KeyValuePair<int, int>>>();
        for (var k = 0; k < Connessioni; k++) parti.Add(new List<KeyValuePair<int, int>>());
        for (var i = 0; i < tutti.Count; i++) parti[i % Connessioni].Add(tutti[i]);

        var fatti = 0;
        Parallel.ForEach(parti.Where(p => p.Count > 0),
            new ParallelOptions { MaxDegreeOfParallelism = Connessioni },
            parte => Interlocked.Add(ref fatti, ScriviTratto(listTitle, parte, PerViaggio)));

        return fatti;
    }

    /// <summary>
    /// Riconosce il rifiuto per eccesso di richieste.
    ///
    /// Va distinto dagli altri errori perché vuole la cura opposta: davanti a un file bloccato si
    /// riprova subito uno per uno, davanti a un 429 riprovare subito è esattamente ciò che lo fa
    /// durare di più. SharePoint lo dice chiaro nella sua documentazione: chi insiste viene
    /// rallentato ancora.
    /// </summary>
    private static bool TroppeRichieste(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e.Message.Contains("(429)", StringComparison.Ordinal)
                || e.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("(503)", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Un tratto di scritture su una connessione sua, a piccoli viaggi.</summary>
    private int ScriviTratto(string listTitle, IReadOnlyList<KeyValuePair<int, int>> tratto, int perViaggio)
    {
        var fatti = 0;
        try
        {
            using var ctx = CreateContext();
            var list = ctx.Web.Lists.GetByTitle(listTitle);

            for (var i = 0; i < tratto.Count; i += perViaggio)
            {
                var viaggio = tratto.Skip(i).Take(perViaggio).ToList();

                // Fino a tre tentativi, ma solo per il rifiuto da eccesso di richieste: è l'unico
                // errore che passa da sé, se gli si dà il tempo di passare.
                for (var tentativo = 0; tentativo < 3; tentativo++)
                {
                    var accodati = 0;
                    foreach (var (id, punteggio) in viaggio)
                    {
                        try
                        {
                            var item = list.GetItemById(id);
                            item[FieldScore] = punteggio;
                            item.SystemUpdate();
                            accodati++;
                        }
                        catch (Exception ex)
                        {
                            _log.LogDebug(ex, "Punteggio non accodato su {List} item {Id}", listTitle, id);
                        }
                    }
                    if (accodati == 0) break;

                    try
                    {
                        ctx.ExecuteQuery();
                        Interlocked.Increment(ref _lottiRiusciti);
                        fatti += accodati;
                        break;
                    }
                    catch (Exception ex) when (TroppeRichieste(ex))
                    {
                        // Si respira e si riprova lo stesso viaggio: due secondi, poi quattro, poi
                        // otto. Se non basta, questi quattro punteggi li riprenderà il giro
                        // successivo -- il riempimento ripassa comunque.
                        Interlocked.Increment(ref _rifiutiPerCarico);
                        _ultimoErroreLotto = $"{DateTimeOffset.UtcNow:HH:mm:ss} {listTitle}: troppe richieste, attendo";
                        Thread.Sleep(TimeSpan.FromSeconds(2 * Math.Pow(2, tentativo)));
                    }
                    catch (Exception ex)
                    {
                        // Un file estratto per la modifica fa rifiutare tutto il viaggio: si riprova
                        // uno per uno, così gli altri tre passano comunque.
                        Interlocked.Increment(ref _lottiFalliti);
                        _ultimoErroreLotto = $"{DateTimeOffset.UtcNow:HH:mm:ss} {listTitle}: {ex.Message.Split('\n')[0]}";
                        foreach (var (id, punteggio) in viaggio)
                        {
                            try
                            {
                                var item = list.GetItemById(id);
                                item[FieldScore] = punteggio;
                                item.SystemUpdate();
                                ctx.ExecuteQuery();
                                fatti++;
                            }
                            catch (Exception e)
                            {
                                _log.LogDebug(e, "Punteggio non scritto su {List} item {Id}", listTitle, id);
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Tratto di punteggi non scritto su {List}", listTitle);
            _ultimoErroreLotto = $"{DateTimeOffset.UtcNow:HH:mm:ss} {listTitle}: {ex.Message.Split('\n')[0]}";
        }
        return fatti;
    }

    private int _lottiRiusciti;
    private int _lottiFalliti;
    private int _rifiutiPerCarico;
    private string? _ultimoErroreLotto;

    /// <summary>Quanti lotti di punteggi sono passati interi e quanti hanno dovuto ripiegare.</summary>
    public object StatoLotti() => new
    {
        riusciti = _lottiRiusciti,
        falliti = _lottiFalliti,
        rifiutiPerCarico = _rifiutiPerCarico,
        ultimoErrore = _ultimoErroreLotto,
    };

    /// <summary>Reviewed metadata written back to the library. Null fields are left untouched.</summary>
    public SharePointItem UpdateItem(string listTitle, int id, string? title, string? description, string? tags)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var item = list.GetItemById(id);
        // Read before writing: a checked-out file fails on Update() with a message that names an
        // internal claim string, which is useless to the author. Fail early and say what to do.
        ctx.Load(item);
        ctx.ExecuteQuery();
        EnsureWritable(item);

        if (title != null) item["Title"] = title;
        if (description != null) item[FieldDescription] = description;
        if (tags != null) item["Tags"] = tags;
        item.Update();
        ctx.Load(item);
        ctx.ExecuteQuery();

        _log.LogInformation("SharePoint {List} item {Id}: metadati aggiornati", listTitle, id);
        return ToItem(item);
    }

    /// <summary>
    /// Releases a file someone left checked out ("estratto"), which is what blocks every metadata
    /// write on it. Check-in keeps the pending content as a new version; discard throws it away and
    /// restores the last checked-in one.
    /// </summary>
    public SharePointCheckIn ReleaseCheckOut(string listTitle, int id, bool discard, string? comment = null)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var item = list.GetItemById(id);
        var file = item.File;
        ctx.Load(item);
        ctx.Load(file, f => f.CheckOutType, f => f.Name);
        ctx.ExecuteQuery();

        var holder = CheckedOutBy(item);
        if (file.CheckOutType == CheckOutType.None && holder.Length == 0)
            return new SharePointCheckIn(false, "Il file non era estratto: nessuna azione necessaria.", ToItem(item));

        if (discard) file.UndoCheckOut();
        else file.CheckIn(comment ?? "Archiviato dal backoffice Stock Vector Studio.", CheckinType.MajorCheckIn);
        ctx.ExecuteQuery();

        ctx.Load(item);
        ctx.ExecuteQuery();

        var how = discard ? "estrazione ignorata" : "archiviato";
        _log.LogInformation("SharePoint {List} item {Id}: {How} (era estratto da {Holder})", listTitle, id, how, holder);
        return new SharePointCheckIn(true,
            $"File {how}: era estratto da {(holder.Length > 0 ? holder : "un altro utente")}, ora accetta scritture.",
            ToItem(item));
    }

    /// <summary>
    /// Sets the "Invia" flag that the invia-to-sftp Logic App watches. Setting it to true is what
    /// actually starts the upload to the marketplaces, so it is a separate, explicit operation.
    /// </summary>
    public SharePointItem SetInvia(string listTitle, int id, bool invia)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var item = list.GetItemById(id);
        ctx.Load(item);
        ctx.ExecuteQuery();

        if (Flag(item, "Inviato"))
            throw new InvalidOperationException("Elemento già inviato: la pipeline lo ha preso in carico.");
        EnsureWritable(item);

        item["Invia"] = invia;
        item.Update();
        ctx.Load(item);
        ctx.ExecuteQuery();

        _log.LogInformation("SharePoint {List} item {Id}: Invia = {V}", listTitle, id, invia);
        return ToItem(item);
    }

    /// <summary>
    /// Segna un elemento come preso in carico, prima che il suo messaggio finisca in coda.
    ///
    /// ## Perche' va fatto *prima*
    /// La Logic App di sorveglianza interroga SharePoint ogni quindici minuti e raccoglie tutto
    /// quello che ha "Invia" alzato e "Inviato" no. Se si accodasse lasciando "Inviato" a falso, il
    /// giro successivo troverebbe lo stesso file e lo accoderebbe una seconda volta: due messaggi,
    /// due caricamenti, la stessa immagine due volte sul marketplace.
    ///
    /// La Logic App infatti scrive "Inviato" **prima** di accodare. Chi pubblica subito deve fare
    /// lo stesso, o le due strade non sono equivalenti -- ed e' proprio quello che succedeva:
    /// finora la pubblicazione immediata si salvava solo perche' lo spostamento in ImagesSent
    /// arrivava prima del giro successivo. Una corsa vinta, non una garanzia.
    ///
    /// ## Perche' si puo' anche disfare
    /// Se poi l'accodamento non riesce, il contrassegno va rimesso a falso: altrimenti il file
    /// resterebbe fermo per sempre, marcato come partito senza esserlo, e nemmeno la sorveglianza
    /// lo raccoglierebbe piu'.
    ///
    /// ## Perche' rifiuta invece di sovrascrivere
    /// Fra il momento in cui si legge lo stato di un gruppo e quello in cui si marcano le sue
    /// consegne passano piu' viaggi verso SharePoint, e in quei secondi la Logic App di
    /// sorveglianza puo' aver preso in carico lo stesso file. Sovrascrivere un "Inviato" gia' alzato
    /// sarebbe un consenso silenzioso: il messaggio finirebbe in coda una seconda volta e
    /// l'immagine salirebbe due volte sul marketplace. Chi chiama deve accorgersene, e saltare.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Quando si chiede la presa in carico di un elemento che qualcun altro ha gia' preso.
    /// </exception>
    public SharePointItem MarcaPresoInCarico(string listTitle, int id, bool preso)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var item = list.GetItemById(id);
        ctx.Load(item);
        ctx.ExecuteQuery();

        if (preso && Flag(item, "Inviato"))
            throw new InvalidOperationException(
                "Preso in carico da qualcun altro mentre si preparava l'invio: non si accoda due volte.");

        EnsureWritable(item);

        item["Inviato"] = preso;
        item.Update();
        ctx.Load(item);
        ctx.ExecuteQuery();

        _log.LogInformation("SharePoint {List} item {Id}: Inviato = {V}", listTitle, id, preso);
        return ToItem(item);
    }

    /// <summary>
    /// Le altre consegne della stessa immagine: i file che stanno nella sua stessa sottocartella e
    /// portano il suo stesso nome, estensione a parte.
    ///
    /// Il percorso durevole deposita ogni immagine come un insieme -- SVG, EPS e il JPEG -- dentro
    /// una cartella che porta il suo nome. Solo il JPEG puo' essere classificato, perche' un
    /// modello di visione non sa leggere delle curve: la descrizione quindi appartiene al gruppo e
    /// non a un singolo file, e ogni azione deve trattare l'insieme come unita'.
    ///
    /// Il nome conta quanto la cartella. Definire il gruppo con la sola cartella sembra bastare --
    /// una cartella per immagine, e' cosi' che le deposita la pipeline -- ma nella libreria storica
    /// esistono cartelle che ne contengono decine di diverse. Con quella regola una riga sola ne
    /// rappresentava ventiquattro estranee fra loro: nascoste alla vista, e cancellate insieme se
    /// si fosse premuto Elimina.
    ///
    /// Un file nella radice della libreria non fa gruppo: e' cosi' che arrivavano le immagini
    /// prima, e considerare la radice una cartella renderebbe sorelle immagini che non c'entrano.
    /// </summary>
    public IReadOnlyList<SharePointItem> GetDeliverableSiblings(string listTitle, SharePointItem carrier)
    {
        var slash = carrier.ServerRelativeUrl.LastIndexOf('/');
        if (slash <= 0) return Array.Empty<SharePointItem>();
        var folder = carrier.ServerRelativeUrl[..slash];
        var stem = Path.GetFileNameWithoutExtension(carrier.FileName);
        if (string.IsNullOrWhiteSpace(stem)) return Array.Empty<SharePointItem>();

        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        ctx.Load(list, l => l.RootFolder.ServerRelativeUrl);
        ctx.ExecuteQuery();

        if (string.Equals(folder.TrimEnd('/'), list.RootFolder.ServerRelativeUrl.TrimEnd('/'),
                          StringComparison.OrdinalIgnoreCase))
            return Array.Empty<SharePointItem>();

        var caml = $"<View Scope='RecursiveAll'>{ViewFields()}<Query><Where><Eq><FieldRef Name='FileDirRef'/>" +
                   $"<Value Type='Text'>{System.Security.SecurityElement.Escape(folder)}</Value>" +
                   "</Eq></Where></Query><RowLimit>50</RowLimit></View>";

        var items = list.GetItems(new CamlQuery { ViewXml = caml });
        ctx.Load(items);
        ctx.ExecuteQuery();

        return items
            .Where(i => i.Id != carrier.Id)
            .Where(i => !string.Equals(Str(i, "FSObjType"), "1", StringComparison.Ordinal))
            .Select(ToItem)
            .Where(i => string.Equals(Path.GetFileNameWithoutExtension(i.FileName), stem,
                                      StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Moves a file between libraries, which is the manual hand-off the author used to do in
    /// SharePoint: reviewed items go from ImagesToClassify to ImagesToSend.
    /// </summary>
    public string MoveFile(string sourceServerRelativeUrl, string targetFolderServerRelativeUrl)
    {
        using var ctx = CreateContext();
        var web = ctx.Web;
        ctx.Load(web, w => w.ServerRelativeUrl);
        ctx.ExecuteQuery();

        // Un gruppo di consegna si sposta in una sottocartella omonima, che nella libreria di
        // destinazione ancora non esiste: senza crearla prima, MoveTo fallisce e basta.
        //
        // EnsureFolderPath vuole un percorso relativo al web, non server-relative: passandogli
        // "/sites/Classifier/ImagesToSend/nome" proverebbe a creare quella gerarchia *dentro* il
        // web e risponderebbe "Accesso negato" -- un messaggio che manda a cercare un problema di
        // permessi che non c'e'.
        var webRelative = targetFolderServerRelativeUrl;
        if (webRelative.StartsWith(web.ServerRelativeUrl, StringComparison.OrdinalIgnoreCase))
            webRelative = webRelative[web.ServerRelativeUrl.Length..];
        webRelative = webRelative.Trim('/');
        if (webRelative.Length > 0) web.EnsureFolderPath(webRelative);

        var file = web.GetFileByServerRelativeUrl(sourceServerRelativeUrl);
        ctx.Load(file, f => f.Name);
        ctx.ExecuteQuery();

        var target = $"{targetFolderServerRelativeUrl.TrimEnd('/')}/{file.Name}";
        file.MoveTo(target, MoveOperations.Overwrite);
        ctx.ExecuteQuery();

        _log.LogInformation("SharePoint: {Src} spostato in {Dst}", sourceServerRelativeUrl, target);
        return target;
    }

    /// <summary>
    /// Rimuove le cartelle rimaste vuote nella libreria, restituendo quante ne ha tolte.
    ///
    /// Ogni immagine viene depositata in una sottocartella col suo nome, e quando i file passano
    /// allo stadio successivo il contenitore resta li'. Non si vede nel Backoffice, ma e' un
    /// elemento di lista come gli altri e occupa un posto del limite di riga a ogni pagina che lo
    /// attraversa: accumulate a migliaia, sono la ragione per cui una pagina da ventiquattro
    /// tornava con una riga sola.
    /// </summary>
    public (int Removed, int Inspected) RemoveEmptyFolders(string listTitle, int max = 500)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        ctx.Load(list, l => l.RootFolder.ServerRelativeUrl);
        ctx.ExecuteQuery();

        // Solo le cartelle, ordinate per Id: FSObjType qui e' l'oggetto della ricerca e non un
        // filtro accessorio, e la query resta piccola perche' il limite la tiene corta.
        var caml = new CamlQuery
        {
            ViewXml = "<View Scope='RecursiveAll'>" +
                      "<ViewFields><FieldRef Name='ID'/><FieldRef Name='FSObjType'/><FieldRef Name='FileRef'/></ViewFields>" +
                      "<Query><Where>" +
                      "<Eq><FieldRef Name='FSObjType'/><Value Type='Integer'>1</Value></Eq>" +
                      "</Where><OrderBy><FieldRef Name='ID' Ascending='TRUE'/></OrderBy></Query>" +
                      $"<RowLimit>{Math.Clamp(max, 1, 2000)}</RowLimit></View>",
        };

        var items = list.GetItems(caml);
        ctx.Load(items);
        ctx.ExecuteQuery();

        var root = list.RootFolder.ServerRelativeUrl.TrimEnd('/');
        var removed = 0;
        var inspected = 0;

        foreach (var i in items)
        {
            var url = Str(i, "FileRef");
            if (string.IsNullOrWhiteSpace(url)) continue;
            // La radice non e' una cartella da togliere, e nemmeno le cartelle di sistema.
            if (string.Equals(url.TrimEnd('/'), root, StringComparison.OrdinalIgnoreCase)) continue;
            inspected++;

            try
            {
                using var inner = CreateContext();
                var folder = inner.Web.GetFolderByServerRelativeUrl(url);
                inner.Load(folder, f => f.ItemCount);
                inner.ExecuteQuery();
                if (folder.ItemCount != 0) continue;

                folder.DeleteObject();
                inner.ExecuteQuery();
                removed++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cartella non rimossa: {Folder}", url);
            }
        }

        _log.LogInformation("{List}: rimosse {Removed} cartelle vuote su {Inspected} esaminate",
                            listTitle, removed, inspected);
        return (removed, inspected);
    }

    /// <summary>
    /// Elimina la cartella se e' rimasta vuota, ignorando ogni ostacolo.
    ///
    /// Cancellare le consegne di un'immagine lascerebbe indietro il contenitore: una cartella vuota
    /// non si vede nel Backoffice ma occupa un posto del limite di riga a ogni pagina che la
    /// attraversa, quindi accumularle vuol dire pagine sempre piu' magre. Non e' un'operazione
    /// critica: se non riesce, si e' solo lasciato dell'ordine da fare.
    /// </summary>
    public void DeleteFolderIfEmpty(string folderServerRelativeUrl)
    {
        try
        {
            using var ctx = CreateContext();
            var folder = ctx.Web.GetFolderByServerRelativeUrl(folderServerRelativeUrl);
            ctx.Load(folder, f => f.ItemCount, f => f.Name);
            ctx.ExecuteQuery();

            if (folder.ItemCount != 0) return;

            folder.DeleteObject();
            ctx.ExecuteQuery();
            _log.LogInformation("SharePoint: cartella vuota rimossa {Folder}", folderServerRelativeUrl);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cartella vuota non rimossa: {Folder}", folderServerRelativeUrl);
        }
    }

    public void DeleteItem(string listTitle, int id)
    {
        using var ctx = CreateContext();
        var list = ctx.Web.Lists.GetByTitle(listTitle);
        var item = list.GetItemById(id);
        item.DeleteObject();
        ctx.ExecuteQuery();
        _log.LogInformation("SharePoint {List} item {Id}: eliminato", listTitle, id);
    }

    /// <summary>Downloads a file's bytes, used to show previews inside the app.</summary>
    /// <summary>
    /// Riscrive il contenuto di un file che esiste già, lasciando intatta la riga di libreria.
    ///
    /// ## Perché non basta UploadFile
    /// Quello crea la cartella prima di scriverci dentro, e per farlo chiama EnsureFolderPath, che
    /// vuole un percorso **relativo al web**. Passandogli un percorso server-relative --
    /// "/sites/Classifier/ImagesToClassify/nome" -- prova a creare quella gerarchia dentro il web e
    /// risponde "Accesso negato", che manda a cercare un problema di permessi che non esiste. È lo
    /// stesso inganno già incontrato in MoveFile, dove è annotato per esteso.
    ///
    /// Qui la cartella c'è già per definizione: si sta sostituendo un file, non depositandone uno
    /// nuovo. Scrivere direttamente sul file evita del tutto quel passaggio.
    ///
    /// ## Il vantaggio che conta
    /// SaveBinary tocca solo i byte: titolo, descrizione, keyword, punteggio e data di ingresso
    /// restano quelli di prima senza doverli rileggere e riscrivere. Ricaricare il file come nuovo
    /// avrebbe richiesto di salvarli e rimetterli a mano, cioè di fidarsi di aver ricordato tutti i
    /// campi -- e i campi si aggiungono nel tempo.
    /// </summary>
    public void ReplaceFile(Stream content, string fileServerRelativeUrl)
    {
        using var ctx = CreateContext();
        var file = ctx.Web.GetFileByServerRelativeUrl(fileServerRelativeUrl);
        ctx.Load(file, f => f.Name);
        ctx.ExecuteQuery();

        if (content.CanSeek) content.Position = 0;
        file.SaveBinary(new FileSaveBinaryInformation { ContentStream = content });
        ctx.ExecuteQuery();

        _log.LogInformation("SharePoint: contenuto riscritto per {Url}", fileServerRelativeUrl);
    }

    /// <summary>
    /// Quanto pesa un file, senza scaricarlo. Chiedere la lunghezza costa una chiamata di
    /// metadati; scaricare il file per poi misurarne l'array costa il file intero, ed era quel che
    /// si faceva prima di accorgersene.
    /// </summary>
    public long FileSize(string serverRelativeUrl)
    {
        using var ctx = CreateContext();
        var file = ctx.Web.GetFileByServerRelativeUrl(serverRelativeUrl);
        ctx.Load(file, f => f.Length);
        ctx.ExecuteQuery();
        return file.Length;
    }

    public byte[] DownloadFile(string serverRelativeUrl)
    {
        using var ctx = CreateContext();
        var file = ctx.Web.GetFileByServerRelativeUrl(serverRelativeUrl);
        // OpenBinaryDirect is .NET Framework only; the Core CSOM path is OpenBinaryStream.
        var data = file.OpenBinaryStream();
        ctx.Load(file);
        ctx.ExecuteQuery();

        using var ms = new MemoryStream();
        data.Value.CopyTo(ms);
        return ms.ToArray();
    }

    private static SharePointItem ToItem(ListItem i) => new(
        i.Id,
        Str(i, "FileLeafRef") ?? "",
        Str(i, "Title") ?? "",
        Str(i, FieldDescription) ?? "",
        Str(i, "Tags") ?? "",
        Str(i, "Stato") ?? "",
        Flag(i, "Invia"),
        Flag(i, "Inviato"),
        Str(i, "Modified") ?? "",
        Str(i, "FileRef") ?? "",
        CheckedOutBy(i),
        Num(i, FieldScore),
        Str(i, "Created") ?? "");
}

public record SharePointItem(
    int Id,
    string FileName,
    string Title,
    string Description,
    string Tags,
    string Stato,
    bool Invia,
    bool Inviato,
    string Modified,
    string ServerRelativeUrl,
    string CheckedOutBy,
    /// <summary>Il punteggio depositato nella libreria, nullo finché non è mai stato scritto.</summary>
    int? PunteggioSalvato = null,
    /// <summary>Quando il file è entrato in libreria: l'unica data che non cambia rilavorandolo.</summary>
    string Created = "");

public record SharePointCheckIn(bool Changed, string Message, SharePointItem Item);

/// <summary>
/// Una pagina di elementi, piu' cosa e' costata leggerla.
///
/// Scanned e Skipped non servono a chi guarda il Backoffice ma a chi deve capire perche' una
/// pagina torna mezza vuota: dicono quante righe SharePoint ha restituito davvero e quante ne sono
/// state scartate perche' non erano file. Senza, l'unico modo di indagare e' indovinare.
/// </summary>
public record SharePointPage(
    IReadOnlyList<SharePointItem> Items,
    string? NextPageToken,
    int Scanned = 0,
    int Skipped = 0,
    string Strategy = "");
