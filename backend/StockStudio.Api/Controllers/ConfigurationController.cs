using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StockStudio.Api.Services;
using StockStudio.Api.Services.Ai;
using StockStudio.Api.Services.Integration;

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
    private readonly SecurityOptions _security;
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
        IOptions<SecurityOptions> security,
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
        _security = security.Value;
        _configuration = configuration;
        _environment = environment;
        _queue = queue;
        _llm = llm;
        _agentCache = agentCache;
    }

    [HttpGet]
    public IActionResult Get()
    {
        var aiKeyConfigured = !string.IsNullOrWhiteSpace(_configuration["Ai:ApiKey"]);
        var deployment = _configuration["Ai:Deployment"];
        var keyVaultUri = _configuration["KeyVault:Uri"];
        var corsOrigins = _configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                          ?? new[] { "http://localhost:5173" };
        var sharePointConfigured =
            !string.IsNullOrWhiteSpace(_pipeline.SiteUrl)
            && !string.IsNullOrWhiteSpace(_pipeline.ClientId)
            && !string.IsNullOrWhiteSpace(_pipeline.Tenant)
            && !string.IsNullOrWhiteSpace(_pipeline.CertificatePath);
        var callbackProtected = !string.IsNullOrWhiteSpace(_pipeline.CallbackSecret);
        var apiProtected = !string.IsNullOrWhiteSpace(_security.ApiKey);
        var localMetadataAi = (_ai.Provider is "openai" or "azure") && aiKeyConfigured;
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
                    Item("JPEG", $"{_vector.JpegLongEdge}px · qualità {_vector.JpegQuality}", "Vectorize:JpegLongEdge / JpegQuality"),
                    Item("Illustrator", $"{_vector.Illustrator.ScalePercent}% · timeout {_vector.Illustrator.TimeoutSeconds}s", "Vectorize:Illustrator"),
                },
            },
            new
            {
                key = "ai",
                title = "AI e metadati",
                summary = _llm.IsConfigured ? $"Agentica · {_llm.Provider}" : "Agentica non configurata",
                state = _llm.IsConfigured ? "ok" : "warning",
                items = new object[]
                {
                    Item("Provider", string.IsNullOrWhiteSpace(_ai.Provider) ? "stub" : _ai.Provider, "Ai:Provider"),
                    Item("Motore agentico", _llm.IsConfigured ? "Attivo" : "Fallback deterministico", "Ai:Provider / Endpoint / ApiKey"),
                    Item("Metadati locali", localMetadataAi ? "AI locale attiva" : "Provvisori locali; definitivi dalla Logic App", "Ai:Provider"),
                    Item("Modello / deployment", deployment ?? _ai.Model, "Ai:Model / Deployment"),
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
                summary = apiProtected ? "API protetta" : "API key disattivata",
                state = apiProtected ? "ok" : _environment.IsDevelopment() ? "warning" : "off",
                items = new object[]
                {
                    Item("Ambiente", _environment.EnvironmentName, "ASPNETCORE_ENVIRONMENT"),
                    Item("API key", apiProtected ? "Configurata" : "Non configurata", "Security:ApiKey"),
                    Item("Callback pipeline", callbackProtected ? "Protetta" : "Non protetta", "Pipeline:CallbackSecret"),
                    Item("Key Vault", string.IsNullOrWhiteSpace(keyVaultUri) ? "Non configurato" : $"Attivo · {SafeHost(keyVaultUri)}", "KeyVault:Uri"),
                    Item("CORS", string.Join(", ", corsOrigins), "Cors:AllowedOrigins"),
                },
            },
        };

        var possibilities = new object[]
        {
            Area("Modalità contenuto",
                "Non è una configurazione del server: si sceglie a ogni caricamento, nella scheda Carica. " +
                "Per questo nessuna delle due risulta \"in uso\". Il valore predefinito è Vettoriale.",
                Choice("vector", "Vettoriale", "Traccia l'immagine e produce SVG, EPS e JPG. È la modalità preselezionata.", false,
                    "potrace incluso oppure Adobe Illustrator"),
                Choice("raster", "Immagine", "Mantiene il raster e produce il JPEG per la pipeline.", false,
                    "nessun software esterno")),
            Area("Motore vettoriale",
                Choice("opensource", "Open source (potrace)", "Automatico, headless e adatto a server/Azure.", _vector.Engine == "opensource",
                    "tools/potrace/potrace.exe"),
                Choice("illustrator", "Adobe Illustrator", "Replica Action e preset Illustrator tramite COM + JSX.", _vector.Engine == "illustrator",
                    "Windows, Illustrator installato, Action configurata")),
            Area("Motore AI",
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
