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
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Security"));
builder.Services.AddHttpClient();

// Metadata: in "hook into pipeline" mode the AI title/keywords are produced asynchronously by the
// existing Azure Logic App. The local provider only supplies a provisional suggestion for the UI.
// Set Ai:Provider=openai|azure (+ key in user-secrets) to also generate metadata locally.
var aiProvider = (builder.Configuration["Ai:Provider"] ?? "stub").ToLowerInvariant();
var aiKey = builder.Configuration["Ai:ApiKey"];
if ((aiProvider is "openai" or "azure") && !string.IsNullOrWhiteSpace(aiKey))
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

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                  ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

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
    // The API key travels in a header: without TLS it would cross the wire in clear.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors();

// Optional API key gate (no-op unless Security:ApiKey is configured).
app.UseMiddleware<ApiKeyMiddleware>();

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

// The compiled SPA, when one has been published into wwwroot. Serving it from the same origin
// removes the need for CORS and keeps the API key on a single host.
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
