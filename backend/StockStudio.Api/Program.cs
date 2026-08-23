using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using StockStudio.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Keep the convenient local port without overriding ASPNETCORE_URLS in containers/App Service.
if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5080");

// Optional Azure Key Vault as a configuration source. When KeyVault:Uri is set, secrets are pulled
// via DefaultAzureCredential (Managed Identity in Azure, az login / VS locally). Secret names use
// "--" for hierarchy, e.g. "Tables--ConnectionString", "Pipeline--ClientId". When empty, the app
// falls back to user-secrets / environment variables for a friction-free local run.
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUri),
        new Azure.Identity.DefaultAzureCredential());
}

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Telemetry, when a connection string is configured. The Function already reports to the same
// Application Insights resource, so site and pipeline end up on one timeline.
var aiConnection = builder.Configuration["ApplicationInsights:ConnectionString"];
if (!string.IsNullOrWhiteSpace(aiConnection))
{
    builder.Services.AddApplicationInsightsTelemetry(o => o.ConnectionString = aiConnection);
}
builder.Services.AddSwaggerGen();

builder.Services.Configure<VectorizeOptions>(builder.Configuration.GetSection("Vectorize"));
builder.Services.Configure<TableOptions>(builder.Configuration.GetSection("Tables"));
builder.Services.AddSingleton<IJobStore, AzureTableJobStore>();

var vecEngine = (builder.Configuration["Vectorize:Engine"] ?? "opensource").ToLowerInvariant();
if (vecEngine == "illustrator")
    builder.Services.AddSingleton<IVectorizer, IllustratorVectorizer>();
else
    builder.Services.AddSingleton<IVectorizer, OpenSourceVectorizer>();
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection("Ai"));
builder.Services.AddHttpClient();

// Metadata: the description can come from three places. "logicapp" delegates to the
// metadata-generator-001 Logic App, which is where the prompt now lives — one copy instead of the
// two that had already drifted apart. "openai"/"azure" still call the model directly, kept as a
// fallback. Anything else leaves the stub, so a local run needs no credentials at all.
var aiProvider = (builder.Configuration["Ai:Provider"] ?? "stub").ToLowerInvariant();
var aiKey = builder.Configuration["Ai:ApiKey"];
var generatorUrl = builder.Configuration["Ai:GeneratorUrl"];

builder.Services.AddSingleton<MetadataNormalizer>();

if (aiProvider == "logicapp" && !string.IsNullOrWhiteSpace(generatorUrl))
    builder.Services.AddSingleton<IMetadataProvider, LogicAppMetadataProvider>();
else if ((aiProvider is "openai" or "azure") && !string.IsNullOrWhiteSpace(aiKey))
    builder.Services.AddSingleton<IMetadataProvider, AiMetadataProvider>();
else
    builder.Services.AddSingleton<IMetadataProvider, StubMetadataProvider>();

// Review loop: the author's manual corrections (and the notes explaining them) are journalled and
// distilled into extra rules appended to the metadata generation prompt.
builder.Services.AddSingleton<StockStudio.Api.Services.Feedback.MetadataFeedbackStore>();
builder.Services.AddSingleton<StockStudio.Api.Services.Feedback.MetadataGuidance>();
builder.Services.AddSingleton<StockStudio.Api.Services.Feedback.MetadataPromptTuner>();

// Integration with the existing Azure pipeline (SharePoint back-office + image-to-classify queue).
builder.Services.AddOptions<StockStudio.Api.Services.Integration.PipelineSettings>()
    .Bind(builder.Configuration.GetSection("Pipeline"))
    .Validate(
        settings => builder.Environment.IsDevelopment()
                    || !settings.Enabled
                    || !string.IsNullOrWhiteSpace(settings.CallbackSecret),
        "Pipeline:CallbackSecret is required when the pipeline is enabled outside Development.")
    .ValidateOnStart();
builder.Services.AddSingleton<StockStudio.Api.Services.Integration.SharePointStore>();
builder.Services.AddSingleton<StockStudio.Api.Services.Integration.QueueDispatcher>();
builder.Services.AddSingleton<StockStudio.Api.Services.Integration.PipelineHandoff>();
builder.Services.AddScoped<StockStudio.Api.Services.Integration.StockPipelineDispatcher>();
builder.Services.AddSingleton<CsvExporter>();
builder.Services.AddSingleton<StockValidator>();
builder.Services.AddSingleton<WikipediaTrendClient>();
builder.Services.AddSingleton<GoogleTrendsClient>();
builder.Services.AddSingleton<StockRelevanceService>();
builder.Services.AddSingleton<ThemeOpportunityService>();
builder.Services.AddSingleton<StockStudio.Api.Services.Ai.LlmClient>();
builder.Services.AddSingleton<StockStudio.Api.Services.Ai.AgentCache>();
builder.Services.AddSingleton<StockStudio.Api.Services.Ai.AgenticStockIntelligence>();
builder.Services.AddSingleton<StockStudio.Api.Services.Ai.StockIntelligenceRouter>();
builder.Services.AddHostedService<ThemeWarmupService>();
builder.Services.AddSingleton<NewsTrendClient>();
builder.Services.AddSingleton<LiveTrendService>();
builder.Services.AddSingleton<TrendService>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService<JobProcessorService>();
builder.Services.AddScoped<PipelineService>();

// Self-scaling of the API's own plan. The evening Logic App only ever brings the level back down
// to F1; this is what climbs before the free tier's daily CPU quota turns every request into a 403.
builder.Services.Configure<PlanOptions>(builder.Configuration.GetSection("Plan"));
builder.Services.AddSingleton<AppServicePlanScaler>();
builder.Services.AddHostedService<PlanGuardService>();

var app = builder.Build();

// On App Service the writable, persistent location is %HOME%\data; the deployment folder itself is
// read-only under run-from-package. Same helper the job store uses, so the two cannot diverge.
var storageRoot = StoragePaths.Root(app.Environment.ContentRootPath);
Directory.CreateDirectory(storageRoot);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // The sign-in cookie must never cross the wire in clear, and neither must the SharePoint data
    // the pages carry.
    app.UseHsts();
    app.UseHttpsRedirection();
}

// No CORS policy and no API key gate here on purpose. The SPA is served from this same origin
// (see UseDefaultFiles below), so there is no cross-origin request to allow; and authentication
// happens in front of the application, in App Service authentication, which admits only signed-in
// users of the cosdh tenant before a request ever reaches this code. The only route it lets
// through unauthenticated is /api/pipeline/callback, which carries its own shared secret and
// checks it in PipelineController.

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".svg"] = "image/svg+xml";
contentTypes.Mappings[".eps"] = "application/postscript";
contentTypes.Mappings[".ai"] = "application/postscript";

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(storageRoot),
    RequestPath = "/api/files",
    ContentTypeProvider = contentTypes,
});

// The compiled SPA, published into wwwroot alongside this API. Serving both from one origin is
// what lets App Service authentication protect them with a single sign-in cookie: the browser
// attaches it to every /api call by itself, so the frontend never handles a token or a key.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// Client-side routes must fall back to index.html, but /api must keep returning 404 instead of
// silently answering with the HTML shell.
app.MapFallback(context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    var index = Path.Combine(app.Environment.WebRootPath ?? "", "index.html");
    if (!File.Exists(index))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    context.Response.ContentType = "text/html";
    return context.Response.SendFileAsync(index);
});

app.Run();
