namespace StockStudio.Api.Services;

public record VectorResult(string? SvgFile, string? EpsFile, string? JpgFile, string? AiFile = null);

/// <summary>Optional per-call overrides for a single vectorization (e.g. user tuning the threshold).</summary>
public record VectorizeOverride(bool? AutoThreshold = null, int? Threshold = null);

/// <summary>
/// Turns a raster image into vector deliverables (SVG/EPS + JPEG preview).
/// Implementations: open-source potrace, or Adobe Illustrator automation.
/// </summary>
public interface IVectorizer
{
    string Name { get; }

    Task<VectorResult> VectorizeAsync(string inputImagePath, string outputDir, string baseName, CancellationToken ct, VectorizeOverride? overrides = null);
}

