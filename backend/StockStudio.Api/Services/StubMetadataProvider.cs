using System.Globalization;
using System.Text.RegularExpressions;

namespace StockStudio.Api.Services;

/// <summary>
/// Placeholder metadata provider: derives title/keywords from the file name so the pipeline
/// runs end-to-end without an AI key. Replaced by an AI vision provider in phase 3.
/// </summary>
public class StubMetadataProvider : IMetadataProvider
{
    public string Name => "stub";

    private static readonly string[] BaseKeywords =
    {
        "vector", "silhouette", "black and white", "illustration", "clip art",
        "isolated", "graphic", "design element", "monochrome", "outline"
    };

    public Task<MetadataResult> GenerateAsync(string imagePath, string baseName, CancellationToken ct)
    {
        var tokens = Regex.Split(baseName, "[^A-Za-z0-9]+")
            .Where(t => t.Length > 1)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();

        var title = tokens.Count > 0
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(string.Join(" ", tokens)) + " Vector Silhouette"
            : "Vector Silhouette";

        var keywords = tokens.Concat(BaseKeywords).Distinct().Take(49).ToList();
        var description = title + " - black and white vector silhouette, isolated on white background.";

        return Task.FromResult(new MetadataResult(title, description, keywords, "Graphic Resources"));
    }

    /// <summary>The pixels are irrelevant here: this provider only ever reads the file name.</summary>
    public Task<MetadataResult> GenerateFromBytesAsync(byte[] image, string baseName, CancellationToken ct)
        => GenerateAsync("", baseName, ct);
}
