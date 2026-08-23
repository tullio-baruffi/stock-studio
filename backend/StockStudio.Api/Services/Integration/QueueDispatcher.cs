using Azure.Storage.Queues;
using Microsoft.Extensions.Options;
using StockStudio.Shared.Contracts;

namespace StockStudio.Api.Services.Integration;

/// <summary>
/// Enqueues a ResizeQueueMessage onto the "image-to-classify" queue to start the existing
/// Azure Functions pipeline. Optional: if no Storage connection is configured, a SharePoint-
/// triggered Logic App is assumed to start the pipeline instead.
/// </summary>
public class QueueDispatcher
{
    private readonly PipelineSettings _s;
    private readonly ILogger<QueueDispatcher> _log;

    public QueueDispatcher(IOptions<PipelineSettings> s, ILogger<QueueDispatcher> log)
    {
        _s = s.Value;
        _log = log;
    }

    public bool CanEnqueue => !string.IsNullOrWhiteSpace(_s.StorageConnectionString);

    public async Task EnqueueAsync(ResizeQueueMessage message, CancellationToken ct)
    {
        if (!CanEnqueue)
        {
            _log.LogInformation("Nessuna StorageConnectionString: salto l'enqueue (atteso trigger SharePoint).");
            return;
        }

        var client = new QueueClient(_s.StorageConnectionString, _s.QueueName,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        await client.CreateIfNotExistsAsync(cancellationToken: ct);
        await client.SendMessageAsync(message.ToString(), ct);
        _log.LogInformation("Enqueued {Blob} su '{Queue}'", message.BlobName, _s.QueueName);
    }

    /// <summary>Reads approximate message counts for the pipeline queues (backlog/health).</summary>
    public async Task<IReadOnlyList<object>> GetDepthsAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var result = new List<object>();
        foreach (var name in names)
        {
            if (!CanEnqueue)
            {
                result.Add(new { name, count = (int?)null, error = "Storage non configurato" });
                continue;
            }
            try
            {
                var client = new QueueClient(_s.StorageConnectionString, name);
                if (await client.ExistsAsync(ct))
                {
                    var props = await client.GetPropertiesAsync(ct);
                    result.Add(new { name, count = props.Value.ApproximateMessagesCount, error = (string?)null });
                }
                else
                {
                    result.Add(new { name, count = 0, error = "coda inesistente" });
                }
            }
            catch (Exception ex)
            {
                result.Add(new { name, count = (int?)null, error = ex.Message });
            }
        }
        return result;
    }
}
