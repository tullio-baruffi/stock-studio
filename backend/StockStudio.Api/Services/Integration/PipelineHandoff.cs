using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Options;
using StockStudio.Shared.Contracts;

namespace StockStudio.Api.Services.Integration;

public record HandoffResult(string BlobName, string OriginalFileName);

/// <summary>
/// Hands an uploaded picture to the durable pipeline and steps aside.
///
/// The web application used to trace and describe every upload itself, on an in-memory queue: a
/// restart mid-batch — a plan change, a deploy, a crash — froze the work until the site came back.
/// Here the only job is to put the original in blob storage and post one message; from that moment
/// the batch belongs to the queue, and the site can sleep, restart or be switched off without
/// costing anything.
///
/// Deliberately narrow: no tracing, no model call, no SharePoint. Everything that can fail slowly
/// happens later, in a Function that can be retried.
/// </summary>
public class PipelineHandoff
{
    private const string OriginalsContainer = "originals-to-vectorize";

    /// <summary>Queue the durable path starts from. Public so the monitoring can watch it by name.</summary>
    public const string VectorizeQueue = "images-to-vectorize";

    private readonly PipelineSettings _s;
    private readonly ILogger<PipelineHandoff> _log;

    public PipelineHandoff(IOptions<PipelineSettings> s, ILogger<PipelineHandoff> log)
    {
        _s = s.Value;
        _log = log;
    }

    /// <summary>True when the storage account behind the pipeline is reachable from here.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_s.StorageConnectionString);

    public async Task<HandoffResult> HandOffAsync(string fileName, Stream content, string mode, int? threshold, CancellationToken ct)
    {
        if (!Enabled)
            throw new InvalidOperationException(
                "Storage della pipeline non configurato: senza di esso non c'e' nessuna coda a cui consegnare il lavoro.");

        // A name that cannot collide with another upload: two authors picking the same file name
        // must not overwrite each other's work while it waits in the queue.
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
        var blobName = $"{Guid.NewGuid():N}{ext}";

        var container = new BlobContainerClient(_s.StorageConnectionString, OriginalsContainer);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        if (content.CanSeek) content.Position = 0;
        await container.GetBlobClient(blobName).UploadAsync(content, overwrite: true, cancellationToken: ct);

        // The message goes out only once the bytes are safely stored: the other order would let a
        // Function pick up a job whose picture does not exist yet.
        var queue = new QueueClient(_s.StorageConnectionString, VectorizeQueue,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        await queue.CreateIfNotExistsAsync(cancellationToken: ct);
        await queue.SendMessageAsync(new VectorizeQueueMessage
        {
            BlobName = blobName,
            OriginalFileName = fileName,
            Mode = string.Equals(mode, "raster", StringComparison.OrdinalIgnoreCase) ? "raster" : "vector",
            // Out of range means "no opinion", not "clamp to the edge": a value the author never
            // chose would trace a silhouette nobody asked for, where Otsu at least reads the image.
            Threshold = threshold is >= 0 and <= 255 ? threshold : null,
        }.ToString(), ct);

        _log.LogInformation("Consegnato alla pipeline: {File} come {Blob}", fileName, blobName);
        return new HandoffResult(blobName, fileName);
    }
}
