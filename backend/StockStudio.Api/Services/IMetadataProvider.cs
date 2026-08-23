namespace StockStudio.Api.Services;

public record MetadataResult(string Title, string Description, IReadOnlyList<string> Keywords, string Category);

/// <summary>Generates stock title / keywords / category for an image.</summary>
public interface IMetadataProvider
{
    string Name { get; }

    Task<MetadataResult> GenerateAsync(string imagePath, string baseName, CancellationToken ct);

    /// <summary>
    /// Same, for an image already in memory. Used when the source is not a local file — for
    /// instance a picture fetched back from the SharePoint libraries to be re-described.
    /// </summary>
    Task<MetadataResult> GenerateFromBytesAsync(byte[] image, string baseName, CancellationToken ct);
}
