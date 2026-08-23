using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StockStudio.Api.Services;

/// <summary>
/// Vectorizes via Adobe Illustrator automation (Windows COM + ExtendScript). Reproduces the manual
/// workflow (Image Trace "B&amp;N Silhouette Auto Group" + Expand, +200% scale) and saves .ai/.eps/.jpg.
/// Requires Illustrator installed on the host. Selected when Vectorize:Engine == "illustrator".
/// </summary>
public class IllustratorVectorizer : IVectorizer
{
    private readonly VectorizeOptions _opt;
    private readonly IllustratorOptions _ai;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<IllustratorVectorizer> _log;

    public string Name => "illustrator";

    public IllustratorVectorizer(IOptions<VectorizeOptions> opt, IWebHostEnvironment env, ILogger<IllustratorVectorizer> log)
    {
        _opt = opt.Value;
        _ai = _opt.Illustrator;
        _env = env;
        _log = log;
    }

    public Task<VectorResult> VectorizeAsync(string inputImagePath, string outputDir, string baseName, CancellationToken ct, VectorizeOverride? overrides = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Il motore Illustrator richiede Windows + Illustrator installato.");

        Directory.CreateDirectory(outputDir);

        // Stage the JSX + a params.json sidecar in an isolated working dir (script reads/writes there).
        var scriptSrc = Path.IsPathRooted(_ai.ScriptPath) ? _ai.ScriptPath : Path.Combine(_env.ContentRootPath, _ai.ScriptPath);
        if (!File.Exists(scriptSrc))
            throw new FileNotFoundException($"Script Illustrator non trovato: {scriptSrc}", scriptSrc);

        var workDir = Path.Combine(Path.GetTempPath(), "stockstudio_ai_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var scriptPath = Path.Combine(workDir, "trace.jsx");
        File.Copy(scriptSrc, scriptPath, true);

        var paramsObj = new
        {
            input = inputImagePath,
            output = outputDir,
            baseName,
            actionSet = _ai.ActionSet,
            actionName = _ai.ActionName,
            scalePercent = _ai.ScalePercent,
            threshold = _ai.Threshold,
            jpegQuality = _opt.JpegQuality,
        };
        File.WriteAllText(Path.Combine(workDir, "params.json"), JsonSerializer.Serialize(paramsObj));

        var resultPath = Path.Combine(workDir, "result.json");
        if (File.Exists(resultPath)) File.Delete(resultPath);

        RunIllustrator(scriptPath, ct);

        // The script writes result.json when done; poll until it appears or we time out.
        var deadline = DateTime.UtcNow.AddSeconds(_ai.TimeoutSeconds);
        while (!File.Exists(resultPath) && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(500);
        }
        if (!File.Exists(resultPath))
            throw new TimeoutException($"Illustrator non ha completato entro {_ai.TimeoutSeconds}s per {baseName}.");

        using (var doc = JsonDocument.Parse(File.ReadAllText(resultPath)))
        {
            if (!doc.RootElement.GetProperty("ok").GetBoolean())
            {
                var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "errore sconosciuto";
                throw new InvalidOperationException($"Illustrator trace fallito: {err}");
            }
        }

        try { Directory.Delete(workDir, true); } catch { /* best effort */ }

        string? Exists(string ext) => File.Exists(Path.Combine(outputDir, baseName + ext)) ? baseName + ext : null;
        return Task.FromResult(new VectorResult(
            SvgFile: Exists(".svg"),
            EpsFile: Exists(".eps"),
            JpgFile: Exists(".jpg"),
            AiFile: Exists(".ai")));
    }

    /// <summary>Invokes Illustrator's COM automation via late binding (no interop assembly / no -windows TFM).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void RunIllustrator(string scriptPath, CancellationToken ct)
    {
        var progId = "Illustrator.Application";
        var type = Type.GetTypeFromProgID(progId);
        if (type == null)
            throw new InvalidOperationException("Illustrator non è installato o il COM non è registrato (ProgID 'Illustrator.Application').");

        object? app = null;
        try
        {
            app = Activator.CreateInstance(type);
            // app.DoJavaScriptFile(scriptPath)
            type.InvokeMember("DoJavaScriptFile",
                BindingFlags.InvokeMethod, null, app,
                new object[] { scriptPath }, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (app != null && System.Runtime.InteropServices.Marshal.IsComObject(app))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(app);
        }
    }
}
