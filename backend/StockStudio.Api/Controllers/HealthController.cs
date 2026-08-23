using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StockStudio.Api.Services;
using StockStudio.Api.Services.Integration;

namespace StockStudio.Api.Controllers;

/// <summary>Fast, local-only health tiles for the System dashboard (no external network calls).</summary>
[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly IJobStore _store;
    private readonly VectorizeOptions _vec;
    private readonly PipelineSettings _pipeline;
    private readonly QueueDispatcher _queue;
    private readonly IWebHostEnvironment _env;

    public HealthController(
        IJobStore store, IOptions<VectorizeOptions> vec, IOptions<PipelineSettings> pipeline,
        QueueDispatcher queue, IWebHostEnvironment env)
    {
        _store = store;
        _vec = vec.Value;
        _pipeline = pipeline.Value;
        _queue = queue;
        _env = env;
    }

    [HttpGet]
    public IActionResult Get()
    {
        bool tableOk;
        string? tableError = null;
        try { _store.List(1); tableOk = true; }
        catch (Exception ex) { tableOk = false; tableError = ex.Message; }

        var potracePath = Path.IsPathRooted(_vec.PotracePath)
            ? _vec.PotracePath
            : Path.Combine(_env.ContentRootPath, _vec.PotracePath);

        return Ok(new
        {
            backend = true,
            jobStore = new { ok = tableOk, backend = "Azure Table Storage", error = tableError },
            vectorizer = new
            {
                engine = _vec.Engine,
                potracePresent = System.IO.File.Exists(potracePath),
            },
            pipeline = new
            {
                enabled = _pipeline.Enabled,
                trigger = _queue.CanEnqueue ? "queue" : "sharepoint-logicapp",
                dispatchFile = _pipeline.DispatchFile,
                libraryFolder = _pipeline.LibraryFolder,
                siteUrl = _pipeline.SiteUrl,
                callbackProtected = !string.IsNullOrWhiteSpace(_pipeline.CallbackSecret),
            },
            timestamp = DateTime.UtcNow.ToString("o"),
        });
    }
}
