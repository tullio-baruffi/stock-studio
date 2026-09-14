using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StockStudio.Api.Services;
using StockStudio.Api.Services.Ai;
using StockStudio.Api.Services.Integration;
using StockStudio.Shared.Vettoriale;

namespace StockStudio.Api.Controllers;

/// <summary>
/// Safe, read-only summary of the active configuration and supported alternatives.
/// Secret values and connection strings are never returned.
/// </summary>
[ApiController]
[Route("api/configuration")]
public class ConfigurationController : ControllerBase
{
    private readonly VectorizeOptions _vector;
    private readonly AiOptions _ai;
    private readonly TableOptions _tables;
    private readonly PipelineSettings _pipeline;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly QueueDispatcher _queue;
    private readonly LlmClient _llm;
    private readonly AgentCache _agentCache;

    public ConfigurationController(
        IOptions<VectorizeOptions> vector,
        IOptions<AiOptions> ai,
        IOptions<TableOptions> tables,
        IOptions<PipelineSettings> pipeline,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        QueueDispatcher queue,
        LlmClient llm,
        AgentCache agentCache)
    {
        _vector = vector.Value;
        _ai = ai.Value;
        _tables = tables.Value;
        _pipeline = pipeline.Value;
        _configuration = configuration;
        _environment = environment;
        _queue = queue;
        _llm = llm;
        _agentCache = agentCache;
    }

    /// <summary>
    /// I numeri con cui si traccia a colori, con il loro intervallo e una riga che dice a cosa
    /// servono.
    ///
    /// Serve alle due schermate che li lasciano scegliere -- il caricamento e la finestra della
    /// rivettorializzazione -- e sta qui invece che scritto nella pagina perche' i predefiniti sono
    /// una proprieta' dell'installazione: cambiarli in <c>appsettings</c> deve cambiare quel che si
    /// vede, non lasciare la pagina a raccontare numeri che nessuno usa piu'.
    /// </summary>
    [HttpGet("tracciato")]
    public IActionResult Tracciato()
    {
        var p = _vector.Tracciato.Convalidato();
        return Ok(new
        {
            riferimento = ParametriTracciato.LatoDiRiferimento,
            // I preset stanno qui e non scritti nella pagina per la stessa ragione dei predefiniti:
            // sono una proprieta' del motore, e la pagina deve raccontare quelli che il motore
            // applica davvero, non una copia che invecchia per conto suo.
            preset = Preset.Tutti.Select(x => new
            {
                codice = x.Codice,
                nome = x.Nome,
                famiglia = x.Famiglia,
                descrizione = x.Descrizione,
                quando = x.QuandoUsarlo,
                modalita = x.ModalitaTracciato,
                automatico = x.MisuraLImmagine,
                // I numeri veri, cosi' scegliendo un preset i cursori si muovono sotto gli occhi
                // invece di restare fermi mentre il disegno cambia. Null sull'automatico, dove i
                // numeri si misurano sull'immagine e prima di vederla non esistono.
                valori = x.Parametri == null ? null : Valori(x.Parametri),
            }),
            presetPredefinito = Preset.CodicePredefinito,
            valori = Valori(p),
            campi = new object[]
            {
                Campo("colori", "Numero di tinte", 2, 64, 1,
                      "Quante campiture diverse avrà il disegno finito. Poche e i dettagli " +
                      "colorati spariscono; chiederne tante non ne inventa."),
                Campo("unione", "Unione tinte gemelle", 0, 4000, 50,
                      "Rimette insieme due tinte quasi identiche che si sono divise la stessa " +
                      "campitura. Alzalo se una superficie unita esce a chiazze."),
                Campo("rumore", "Riduzione rumore", 0, 8, 1,
                      "Pulisce l'immagine prima di scegliere le tinte. Alzalo se i contorni " +
                      "escono seghettati; troppo, e i dettagli minuti spariscono."),
                Campo("lisciatura", "Lisciatura della mappa", 0, 6, 1,
                      "Quanto arrotondare il bordo fra una campitura e l'altra. Abbassalo per " +
                      "tenere gli spigoli vivi di loghi e scritte."),
                Campo("granelli", "Granelli da togliere", 0, 20000, 10,
                      "Butta via le macchie più piccole di così. Alzalo se il disegno è pieno di " +
                      "schegge; troppo, e spariscono anche i dettagli veri. Zero non ne toglie."),
                Campo("tolleranza", "Fedeltà del tracciato", 0.1, 12, 0.1,
                      "Di quanto la curva può discostarsi dal bordo misurato. Basso: tante curve " +
                      "e ricalca le sbavature. Alto: poche curve e le forme si deformano."),
                Campo("angolo", "Angolo di spigolo", 15, 170, 5,
                      "Da quanti gradi in su una svolta è uno spigolo invece di una curva. Si " +
                      "sente poco: per gli spigoli conta più la lisciatura."),
                Campo("morbidezza", "Morbidezza dei contorni", 0, 12, 0.5,
                      "Quanto il contorno può allontanarsi dai pixel misurati mentre viene " +
                      "lisciato. È un limite di sicurezza, non una leva."),
                Campo("giri", "Giri di lisciatura", 0, 60, 1,
                      "Quante passate di lisciatura. Oltre un certo punto non cambia più niente."),
            },
        });

        static object Valori(ParametriTracciato v) => new
        {
            grigi = v.ScalaDiGrigi,
            colori = v.NumeroColori,
            unione = v.SogliaUnione,
            rumore = v.RiduzioneRumore,
            lisciatura = v.RaggioLisciatura,
            granelli = v.Granelli,
            morbidezza = v.Morbidezza,
            giri = v.GiriLisciatura,
            tolleranza = v.Tolleranza,
            angolo = v.AngoloSpigolo,
        };

        static object Campo(string nome, string etichetta, double min, double max, double passo, string spiega)
            => new { nome, etichetta, min, max, passo, spiega };
    }

    [HttpGet]
    public IActionResult Get()
    {
        var aiKeyConfigured = !string.IsNullOrWhiteSpace(_configuration["Ai:ApiKey"]);
        var deployment = _configuration["Ai:Deployment"];
        var keyVaultUri = _configuration["KeyVault:Uri"];
        var sharePointConfigured =
            !string.IsNullOrWhiteSpace(_pipeline.SiteUrl)
            && !string.IsNullOrWhiteSpace(_pipeline.ClientId)
            && !string.IsNullOrWhiteSpace(_pipeline.Tenant)
            && !string.IsNullOrWhiteSpace(_pipeline.CertificatePath);
        var callbackProtected = !string.IsNullOrWhiteSpace(_pipeline.CallbackSecret);

        // App Service authentication runs in front of this process, so the honest way to report it
        // is to look at what it actually did to this request: an authenticated caller arrives with
        // a principal header that nothing downstream can forge, because the platform strips any
        // incoming copy of it before the request reaches us.
        var signedInAs = Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].ToString();
        var authEnabled = string.Equals(
            Environment.GetEnvironmentVariable("WEBSITE_AUTH_ENABLED"), "True", StringComparison.OrdinalIgnoreCase);
        // "logicapp" delegates the description to metadata-generator-001, so the metadata is just
        // as final as the direct call — it simply travels through the Logic App that owns the prompt.
        var viaGenerator = _ai.Provider is "logicapp" && !string.IsNullOrWhiteSpace(_ai.GeneratorUrl);
        var localMetadataAi = viaGenerator || ((_ai.Provider is "openai" or "azure") && aiKeyConfigured);
        var tableKind = IsDevelopmentStorage(_tables.ConnectionString)
            ? "Azurite (emulatore locale)"
            : "Azure Table Storage";

        var groups = new object[]
        {
            new
            {
                key = "vectorization",
                title = "Vettorializzazione",
                summary = _vector.Engine.Equals("illustrator", StringComparison.OrdinalIgnoreCase)
                    ? "Adobe Illustrator"
                    : "Open source (potrace)",
                state = "ok",
                items = new object[]
                {
                    Item("Motore attivo", _vector.Engine, "Vectorize:Engine"),
                    Item("Soglia", _vector.AutoThreshold ? "Automatica (Otsu)" : $"Fissa: {_vector.Threshold}", "Vectorize:AutoThreshold / Threshold"),
                    Item("Inversione immagini scure", YesNo(_vector.InvertIfMostlyDark), "Vectorize:InvertIfMostlyDark"),
                    Item("Riduzione impurità", $"{_vector.TurdSize} px", "Vectorize:TurdSize"),
                    Item("Smoothing curve", _vector.AlphaMax.ToString("0.##"), "Vectorize:AlphaMax"),
                    Item("Tolleranza ottimizzazione", _vector.OptTolerance.ToString("0.###"), "Vectorize:OptTolerance"),
                    Item("Tinte del tracciato a colori", $"{_vector.NumeroColori}", "Vectorize:NumeroColori"),
                    Item("Unione tinte gemelle", _vector.SogliaUnione <= 0
                            ? "Disattivata"
                            : $"{_vector.SogliaUnione:0} (predefinita; si può scegliere al caricamento)",
                         "Vectorize:SogliaUnione"),
                    Item("JPEG", $"{_vector.JpegLongEdge}px · qualità {_vector.JpegQuality}", "Vectorize:JpegLongEdge / JpegQuality"),
                    Item("Illustrator", $"{_vector.Illustrator.ScalePercent}% · timeout {_vector.Illustrator.TimeoutSeconds}s", "Vectorize:Illustrator"),
                },
            },
            new
            {
                key = "ai",
                title = "AI e metadati",
                // Il gruppo si chiama "AI e metadati" e la sua funzione principale sono i metadati:
                // il verdetto deve dipendere da quelli. Prima dipendeva solo dal motore agentico,
                // che serve alle Opportunita' ed e' un'altra cosa: con i metadati perfettamente
                // funzionanti via Logic App l'intero gruppo risultava incompleto, il che mandava a
                // cercare un guasto dove non c'era.
                summary = viaGenerator ? "Metadati dalla Logic App"
                          : localMetadataAi ? "Metadati da AI locale"
                                            : "Metadati non configurati",
                state = viaGenerator || localMetadataAi ? "ok" : _environment.IsDevelopment() ? "warning" : "off",
                items = new object[]
                {
                    Item("Provider", string.IsNullOrWhiteSpace(_ai.Provider) ? "stub" : _ai.Provider, "Ai:Provider"),
                    Item("Metadati",
                         viaGenerator ? "Logic App metadata-generator-001 (prompt condiviso con la pipeline)"
                                      : localMetadataAi ? "AI locale attiva"
                                                        : "Provvisori locali; definitivi dalla Logic App",
                         "Ai:Provider"),
                    Item("Generatore metadati", viaGenerator ? "Configurato" : "Non configurato", "Ai:GeneratorUrl"),
                    // L'agente e' indipendente dai metadati: alimenta le Opportunita' e vuole il
                    // tool calling. Dirlo qui, invece di lasciarlo decidere sul semaforo del gruppo.
                    Item("Motore agentico (Opportunità)",
                         _llm.IsConfigured ? $"Attivo · {_llm.Describe()}" : "Fallback deterministico",
                         "Ai:ApiKey / Ai:Endpoint / Ai:AgentModel"),
                    Item("Modello / deployment", viaGenerator ? "definito nella Logic App" : (deployment ?? _ai.EffectiveModel), "Ai:Model / VisionModel"),
                    Item("Endpoint", SafeHost(_ai.Endpoint) ?? "predefinito del provider", "Ai:Endpoint"),
                    Item("API key", aiKeyConfigured ? "Configurata" : "Non configurata", "Ai:ApiKey"),
                    Item("Cache risposte agente", $"{_agentCache.Ttl.TotalHours:0.##} ore", "Ai:CacheHours"),
                    Item("Immagine inviata al modello", $"lato max {_ai.ImageMaxEdge}px", "Ai:ImageMaxEdge"),
                    Item("Keyword massime", _ai.MaxKeywords.ToString(), "Ai:MaxKeywords"),
                    Item("Temperatura metadati", _ai.Temperature.ToString("0.##"), "Ai:Temperature"),
                },
            },
            new
            {
                key = "pipeline",
                title = "Pipeline Azure",
                summary = _pipeline.Enabled ? "Attiva" : "Disabilitata",
                state = !_pipeline.Enabled ? "off" : callbackProtected ? "ok" : "warning",
                items = new object[]
                {
                    Item("Trigger effettivo", _queue.CanEnqueue ? "Azure Queue" : "SharePoint / Logic App", "Pipeline:StorageConnectionString"),
                    Item("Sito SharePoint", SafeHost(_pipeline.SiteUrl) ?? "Non configurato", "Pipeline:SiteUrl"),
                    Item("Libreria di ingresso", _pipeline.LibraryFolder ?? "Non configurata", "Pipeline:LibraryFolder"),
                    Item("Autenticazione SharePoint", sharePointConfigured ? $"Configurata · {SafeFileName(_pipeline.CertificatePath)}" : "Incompleta", "Pipeline:ClientId / Tenant / CertificatePath"),
                    Item("Coda", _pipeline.QueueName, "Pipeline:QueueName"),
                    Item("Storage coda", _queue.CanEnqueue ? "Configurato" : "Non configurato", "Pipeline:StorageConnectionString"),
                    Item("File inviato", _pipeline.DispatchFile is "auto" or ""
                        ? "Automatico: JPG per i raster, EPS e SVG per i vettoriali"
                        : _pipeline.DispatchFile, "Pipeline:DispatchFile"),
                    Item("Callback dashboard", callbackProtected ? "Protetta" : "Senza shared secret", "Pipeline:CallbackSecret"),
                },
            },
            new
            {
                key = "persistence",
                title = "Persistenza",
                summary = tableKind,
                state = "ok",
                items = new object[]
                {
                    Item("Backend", tableKind, "Tables:ConnectionString"),
                    Item("Tabella", _tables.TableName, "Tables:TableName"),
                    Item("File generati", "Disco locale sotto Storage/", "Storage locale"),
                },
            },
            new
            {
                key = "security",
                title = "Sicurezza e runtime",
                summary = authEnabled ? "Accesso con account aziendale" : "Nessuna autenticazione",
                state = authEnabled ? "ok" : _environment.IsDevelopment() ? "warning" : "off",
                items = new object[]
                {
                    Item("Ambiente", _environment.EnvironmentName, "ASPNETCORE_ENVIRONMENT"),
                    Item("Autenticazione",
                         authEnabled ? "App Service authentication (Entra ID)" : "Disattivata",
                         "authsettingsV2"),
                    Item("Utente corrente",
                         string.IsNullOrWhiteSpace(signedInAs) ? "anonimo" : signedInAs,
                         "X-MS-CLIENT-PRINCIPAL-NAME"),
                    Item("Callback pipeline", callbackProtected ? "Protetta" : "Non protetta", "Pipeline:CallbackSecret"),
                    Item("Key Vault", string.IsNullOrWhiteSpace(keyVaultUri) ? "Non configurato" : $"Attivo · {SafeHost(keyVaultUri)}", "KeyVault:Uri"),
                },
            },
        };

        var possibilities = new object[]
        {
            Area("Modalità contenuto",
                "Non è una configurazione del server: si sceglie a ogni caricamento, nella scheda Carica. " +
                "Per questo nessuna risulta \"in uso\". Il valore predefinito è Vettoriale in bianco e nero.",
                Choice("vector", "Vettoriale in bianco e nero", "Una soglia di luminanza e una passata di tracciato: la silhouette. È la modalità preselezionata.", false,
                    "potrace incluso oppure Adobe Illustrator"),
                Choice("colore", "Vettoriale a colori", $"Riduce l'immagine a poche tinte e traccia una passata per ognuna: {_vector.NumeroColori} al massimo. È un tetto, non una promessa: le tinte che descrivono una frangia di contorno invece di una zona vengono scartate, quindi su un disegno che ha meno colori ne escono meno.", false,
                    "potrace incluso"),
                Choice("raster", "Immagine", "Mantiene il raster e produce il JPEG per la pipeline.", false,
                    "nessun software esterno")),
            Area("Motore vettoriale",
                Choice("opensource", "Open source (potrace)", "Automatico, headless e adatto a server/Azure.", _vector.Engine == "opensource",
                    "tools/potrace/potrace.exe"),
                Choice("illustrator", "Adobe Illustrator", "Replica Action e preset Illustrator tramite COM + JSX.", _vector.Engine == "illustrator",
                    "Windows, Illustrator installato, Action configurata")),
            Area("Motore AI",
                Choice("logicapp", "Logic App (consigliato)",
                    "I metadati arrivano da metadata-generator-001: un solo prompt, condiviso con la pipeline, e nessuna chiave OpenAI da tenere qui.",
                    _ai.Provider == "logicapp", "Ai:GeneratorUrl"),
                Choice("stub", "Nessuna AI locale", "Metadati provvisori deterministici; quelli definitivi arrivano dalla Logic App.", _ai.Provider == "stub",
                    "nessun requisito"),
                Choice("openai", "OpenAI", "Metadati locali e agente opportunità tramite API OpenAI.", _ai.Provider == "openai",
                    "Ai:ApiKey, Ai:Model"),
                Choice("azure", "Azure OpenAI", "Metadati locali e agente opportunità tramite deployment Azure.", _ai.Provider == "azure",
                    "Ai:Endpoint, Ai:Deployment, Ai:ApiKey"),
                Choice("compatible", "Endpoint OpenAI-compatible", "Agente con Ollama, LM Studio, vLLM o servizio compatibile.", _ai.Provider == "compatible",
                    "Ai:Endpoint, Ai:Model; supporto tool calling")),
            Area("Trigger pipeline",
                Choice("queue", "Azure Queue", "Il sito accoda direttamente image-to-classify.", _queue.CanEnqueue,
                    "Pipeline:StorageConnectionString"),
                Choice("sharepoint", "SharePoint / Logic App", "Il deposito nella libreria innesca la pipeline esistente.", _pipeline.Enabled && !_queue.CanEnqueue,
                    "credenziali SharePoint e trigger esterno")),
            Area("Formato inviato",
                _pipeline.DispatchFile is "auto" or ""
                    ? "Dipende dalla modalità del job, non è una scelta fissa: un raster viene inviato come JPG, " +
                      "un vettoriale come EPS e SVG insieme, che è ciò che Adobe Stock e Freepik si aspettano. " +
                      "Impostare Pipeline:DispatchFile a un formato specifico forza invece quello per tutti i job."
                    : $"Forzato a '{_pipeline.DispatchFile}' da Pipeline:DispatchFile per ogni job. " +
                      "Rimuovendo la chiave (o impostandola ad 'auto') torna a dipendere dalla modalità.",
                Choice("auto", "Automatico per modalità", "Raster → JPG. Vettoriale → EPS e SVG.", _pipeline.DispatchFile is "auto" or "",
                    "nessun requisito"),
                Choice("jpg", "Solo JPG", "Forza il JPG anche per i vettoriali.", _pipeline.DispatchFile == "jpg",
                    "Pipeline:DispatchFile = jpg"),
                Choice("eps", "Solo EPS", "Forza l'EPS anche per i raster.", _pipeline.DispatchFile == "eps",
                    "Pipeline:DispatchFile = eps"),
                Choice("svg", "Solo SVG", "Forza l'SVG anche per i raster.", _pipeline.DispatchFile == "svg",
                    "Pipeline:DispatchFile = svg")),
            Area("Sorgente dei secret",
                "User Secrets e variabili ambiente non sono distinguibili a runtime: la configurazione " +
                "arriva già risolta, quindi restano sempre non evidenziate. Solo Key Vault è rilevabile.",
                Choice("user-secrets", "User Secrets", "Scelta locale consigliata; non entra nei file del progetto.", false,
                    "dotnet user-secrets"),
                Choice("environment", "Variabili ambiente", "Adatte a container, CI/CD e hosting.", false,
                    "nomi gerarchici con doppio underscore"),
                Choice("key-vault", "Azure Key Vault", "Scelta production centralizzata con Managed Identity e RBAC.", !string.IsNullOrWhiteSpace(keyVaultUri),
                    "KeyVault:Uri e RBAC")),
        };

        return Ok(new
        {
            generatedAt = DateTime.UtcNow.ToString("o"),
            environment = _environment.EnvironmentName,
            safetyNote = "Valori sensibili non esposti: la pagina mostra solo presenza, host o nome file.",
            groups,
            possibilities,
        });
    }

    private static object Item(string label, string value, string configKey) =>
        new { label, value, configKey };

    private static object Area(string title, params object[] choices) =>
        new { title, note = (string?)null, choices };

    /// <summary>
    /// Area with an explanatory note, for the cases where "nessuna scelta in uso" is the correct
    /// state and would otherwise read as a fault.
    /// </summary>
    private static object Area(string title, string note, params object[] choices) =>
        new { title, note, choices };

    private static object Choice(
        string value, string label, string description, bool active, string requirements) =>
        new { value, label, description, active, requirements };

    private static string YesNo(bool value) => value ? "Sì" : "No";

    private static string? SafeHost(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static string SafeFileName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "certificato assente" : Path.GetFileName(value);

    private static bool IsDevelopmentStorage(string? connectionString) =>
        string.Equals(connectionString?.Trim(), "UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase)
        || connectionString?.Contains("devstoreaccount1", StringComparison.OrdinalIgnoreCase) == true;
}
