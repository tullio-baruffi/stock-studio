using System.Globalization;
using System.Text.RegularExpressions;

namespace StockStudio.Api.Services;

/// <summary>
/// Placeholder metadata provider: derives title/keywords from the file name so the pipeline
/// runs end-to-end without an AI key. Replaced by an AI vision provider in phase 3.
///
/// Non dichiara piu' un supporto. Prima scriveva "black and white vector silhouette" per ogni
/// file, e da quando si vettorializza anche a colori quella frase sarebbe una descrizione
/// sbagliata scritta con sicurezza -- il modo piu' rapido di far rifiutare un caricamento, visto
/// che Adobe controlla che i metadati corrispondano al file.
/// </summary>
public class StubMetadataProvider : IMetadataProvider
{
    public string Name => "stub";

    private static readonly string[] BaseKeywords =
    {
        "vector", "illustration", "clip art",
        "isolated", "graphic", "design element", "outline"
    };

    public Task<MetadataResult> GenerateAsync(string imagePath, string baseName, CancellationToken ct)
    {
        var tokens = Regex.Split(baseName, "[^A-Za-z0-9]+")
            .Where(t => t.Length > 1)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();

        var title = tokens.Count > 0
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(string.Join(" ", tokens)) + " Vector Illustration"
            : "Vector Illustration";

        var keywords = tokens.Concat(BaseKeywords).Distinct().Take(49).ToList();
        var description = title + " - vector illustration, isolated on a plain background.";

        return Task.FromResult(new MetadataResult(title, description, keywords, "Graphic Resources"));
    }

    /// <summary>The pixels are irrelevant here: this provider only ever reads the file name.</summary>
    public Task<MetadataResult> GenerateFromBytesAsync(byte[] image, string baseName, CancellationToken ct)
        => GenerateAsync("", baseName, ct);
}
