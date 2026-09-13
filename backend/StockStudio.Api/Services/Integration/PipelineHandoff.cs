using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Extensions.Options;
using StockStudio.Shared.Contracts;

using StockStudio.Shared.Vettoriale;

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

    /// <summary>
    /// Dove la Function conserva gli originali dopo il tracciato. Deve restare uguale a
    /// VectorizeImage.PrefissoConservati: sono due progetti che non si vedono fra loro, e l'unico
    /// legame e' questa stringa.
    /// </summary>
    private const string PrefissoConservati = "conservati/";

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

    public async Task<HandoffResult> HandOffAsync(string fileName, Stream content, string mode, int? threshold,
                                                  int? colori, double? unione,
                                                  ParametriTracciato? tracciato, CancellationToken ct)
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
            Mode = Modalita.Normalizza(mode),
            // Out of range means "no opinion", not "clamp to the edge": a value the author never
            // chose would trace a silhouette nobody asked for, where Otsu at least reads the image.
            Threshold = threshold is >= 0 and <= 255 ? threshold : null,
            // Fuori intervallo vuol dire "nessuna preferenza": si lascia decidere al valore
            // predefinito invece di accostarsi al limite, che sarebbe una scelta di nessuno.
            //
            // Il tetto e' trentadue come nella Function che riceve il messaggio: quando qui era
            // ventiquattro, un valore fra i due veniva scartato in silenzio e chi l'aveva scelto
            // otteneva il predefinito senza che niente glielo dicesse.
            Colori = colori is >= 2 and <= 32 ? colori : null,
            // Zero e' una scelta legittima -- "non unire niente" -- quindi passa; solo i valori
            // assurdi diventano "nessuna preferenza".
            Unione = unione is >= 0 and <= 4000 ? unione : null,
            // Il resto della taratura viaggia gia' convalidato: la Function riceve numeri dentro i
            // limiti in cui hanno senso, e non deve fidarsi di chi ha compilato il modulo.
            Tracciato = tracciato?.Convalidato(),
        }.ToString(), ct);

        _log.LogInformation("Consegnato alla pipeline: {File} come {Blob}", fileName, blobName);
        return new HandoffResult(blobName, fileName);
    }

    /// <summary>
    /// L'originale conservato di un'immagine, se c'e'.
    ///
    /// ## Perche' esiste
    /// Su SharePoint, di un'immagine tracciata, restano un SVG, un EPS e un JPEG a qualita' 92
    /// lungo al massimo 4000 pixel. Nessuno dei tre e' l'originale. Ritracciare dal JPEG vuol dire
    /// ricalcare gli artefatti della compressione -- gli aloni attorno alle linee nere, il
    /// pulviscolo sul bianco -- e consegnarli come se fossero disegno.
    ///
    /// Torna null per le immagini tracciate prima che gli originali si conservassero: quelle si
    /// ritracciano dal JPEG, che e' il meglio che ne resti, e chi guarda deve poterlo sapere.
    /// </summary>
    /// <param name="baseName">Il nome della cartella su SharePoint, che e' anche quello del file.</param>
    public async Task<(Stream Contenuto, string Nome)?> OriginaleAsync(string baseName, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(baseName)) return null;

        var container = new BlobContainerClient(_s.StorageConnectionString, OriginalsContainer);
        if (!await container.ExistsAsync(ct)) return null;

        // L'estensione non e' nota: si prova quella che l'originale poteva avere. Sono poche, e
        // una chiamata che trova subito costa quanto una che non trova.
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff" })
        {
            var blob = container.GetBlobClient(PrefissoConservati + baseName + ext);
            if (!await blob.ExistsAsync(ct)) continue;

            var memoria = new MemoryStream();
            await blob.DownloadToAsync(memoria, ct);
            memoria.Position = 0;
            _log.LogInformation("Originale conservato trovato per {Base}: {Nome}", baseName, blob.Name);
            return (memoria, baseName + ext);
        }

        _log.LogInformation("Nessun originale conservato per {Base}", baseName);
        return null;
    }
}
