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

    private static string? Str(ListItem i, string field) =>
        i.FieldValues.TryGetValue(field, out var v) ? v?.ToString() : null;

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
    public SharePointPage ListItems(string listTitle, int take, string? pageToken, string? search, string? field = null)
    {
        if (pageToken?.StartsWith("sp:", StringComparison.Ordinal) == true)
            return ListByFolder(listTitle, take, pageToken[3..], search, field);

        try
        {
            return ListFlat(listTitle, take, pageToken, search, field);
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
            ViewXml = "<View Scope='Recursive'><Query>" + BuildWhere(search, field, null) +
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

    private static string BuildWhere(string? search, string? field, string? idCondition)
    {
        var conditions = new List<string>();
        if (idCondition != null) conditions.Add(idCondition);

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

        return conditions.Count switch
        {
            0 => "",
            1 => $"<Where>{conditions[0]}</Where>",
            _ => $"<Where><And>{conditions[0]}{conditions[1]}</And></Where>",
        };
    }

    /// <summary>Elenco piatto con cursore sull'Id: pagine piene, ma non regge oltre la soglia.</summary>
    private SharePointPage ListFlat(string listTitle, int take, string? pageToken, string? search, string? field)
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
                $"<Lt><FieldRef Name='ID'/><Value Type='Counter'>{cursor}</Value></Lt>");

            var caml = new CamlQuery
            {
                // RecursiveAll, non Recursive: sembrano sinonimi e non lo sono. Con 'Recursive'
                // SharePoint impagina cartella per cartella e il RowLimit vale dentro ciascuna, il
                // che dava una riga per pagina. 'RecursiveAll' restituisce un elenco piatto.
                //
                // Ordine per Id discendente: mostra le piu' recenti per prime ed e' piu' stabile di
                // Modified, che rimescolerebbe la pagina proprio mentre la si revisiona.
                ViewXml = "<View Scope='RecursiveAll'><Query>" + where +
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

        var caml = "<View Scope='RecursiveAll'><Query><Where><Eq><FieldRef Name='FileDirRef'/>" +
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
            ViewXml = "<View Scope='RecursiveAll'><Query><Where>" +
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
        CheckedOutBy(i));
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
    string CheckedOutBy);

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
