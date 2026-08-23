using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using MJ.Classifier.Models;
using MJ.Classifier.Helpers;
using StockStudio.Shared.Contracts;

namespace MJ.Classifier
{
    public class ResizeImageToClassify
    {
        const string QueueContainerShrinkedName = "shrinked-image-to-classify";
        const string QueueContainerName = "image-to-classify";
        const string BlobContainerShrinkedName = "shrinked-images";
        const string ConnectionName = "rgclassifier8f3e_STORAGE";

        /// <summary>
        /// Longest edge of the copy handed to the vision model.
        ///
        /// The blob in "shrinked-images" exists for one reader only: the Logic App passes its public
        /// URL to OpenAI and deletes it afterwards. The SFTP upload works from the SharePoint
        /// original, so nothing downstream depends on these pixels. Sending a 4000px picture costs
        /// about seven times the tokens of a 1024px one and buys no extra accuracy on a silhouette.
        /// </summary>
        const int VisionMaxEdge = 1024;

        /// <summary>JPEG quality for that copy: enough for a description, small enough to be cheap.</summary>
        const long VisionJpegQuality = 82L;

        /// <summary>
        /// Estensioni che un modello di visione sa davvero leggere.
        ///
        /// Serve perche' nella libreria non arrivano solo fotografie: la vettorializzazione deposita
        /// accanto al JPG anche l'SVG e l'EPS, e il poller di SharePoint li raccoglie tutti e tre.
        /// Un vettoriale descrive curve, non pixel: mandarlo avanti significa caricarlo intero nel
        /// blob pubblico e far fallire la generazione dei metadati con l'elenco dei decoder
        /// disponibili, un messaggio che non dice nulla a chi lo legge.
        ///
        /// Elenco di cio' che si accetta e non di cio' che si scarta: un'estensione sconosciuta e'
        /// molto piu' probabilmente un altro formato illeggibile che non un raster inatteso.
        /// </summary>
        static readonly string[] RasterExtensions =
            { ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff", ".bmp", ".gif" };

        private readonly SharePointSettings _sharePointSettings;

        public ResizeImageToClassify(
            IOptions<SharePointSettings> sharePointSettings
            )
        {
            _sharePointSettings = sharePointSettings.Value;
        }

        [FunctionName("ResizeImageToClassify")]
        public async Task Run(
            [QueueTrigger(QueueContainerName, Connection = ConnectionName)] string myQueueItem
            , ExecutionContext context
            , [Blob(BlobContainerShrinkedName, FileAccess.ReadWrite, Connection = ConnectionName)] BlobContainerClient shrinkedContainer
            , [Queue(QueueContainerShrinkedName, Connection = ConnectionName)] QueueClient queueClient
            , ILogger log
        )
        {
            using var scope = LoggerHelper.BeginScope(log);

            log.LogInformation($"Queue trigger function processing: {myQueueItem}");

            ResizeQueueMessage message = null;

            try
            {
                message = JsonConvert.DeserializeObject<ResizeQueueMessage>(myQueueItem)
                    ?? throw new InvalidDataException("Queue message is empty or invalid.");
                if (string.IsNullOrWhiteSpace(message.ServerRelativeUrl))
                    throw new InvalidDataException("Queue message is missing ServerRelativeUrl.");

                // Si esce prima di scaricare: un vettoriale non ha nulla da classificare, e
                // proseguire vorrebbe dire copiarlo nel blob pubblico e far fallire la generazione
                // dei metadati piu' avanti, dove l'errore non si capisce piu' da dove viene.
                // Non e' un fallimento: il file e' arrivato dove doveva, semplicemente non e' lui a
                // portare la descrizione del gruppo.
                var name = message.BlobName ?? System.IO.Path.GetFileName(message.ServerRelativeUrl);
                var ext = System.IO.Path.GetExtension(name ?? "").ToLowerInvariant();
                if (Array.IndexOf(RasterExtensions, ext) < 0)
                {
                    log.LogInformation(
                        $"{name}: non e' un'immagine raster ({(ext.Length > 0 ? ext : "senza estensione")}), " +
                        "niente da classificare. La descrizione del gruppo la porta il JPG.");
                    return;
                }

                log.LogInformation($"Get image to resize: blobPath {message.PathBlob}");
                using var memoryStream = SharePointHelper.GetFileFromSharePoint(_sharePointSettings, message.ServerRelativeUrl, context.FunctionDirectory, log);
                log.LogInformation($"Image found");

                log.LogInformation($"Uploading resized image");
                await UploadResizedImageAsync(memoryStream, shrinkedContainer, message, log);
                log.LogInformation($"Resized image uploaded");

                log.LogInformation($"Enqueue message for resized image");
                await EnqueueMessageAsync(message, queueClient);
                log.LogInformation($"Message for resized image enqueued");

                log.LogInformation("Queue trigger function successfully processed");
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"Error while processing queue item: {message?.IdBlob ?? "(unknown)"}");
                // Let the queue runtime retry and eventually move the poison message instead of
                // acknowledging a failed item as successfully processed.
                throw;
            }
        }

        private async Task UploadResizedImageAsync(MemoryStream sourceStream, BlobContainerClient shrinkedContainer, ResizeQueueMessage message, ILogger log)
        {
            var blobBlockClient = shrinkedContainer.GetBlockBlobClient(message.BlobName);
            // Queue retries are expected. If upload succeeded but enqueue failed, the retry must be
            // able to replace the same blob and continue instead of failing with BlobAlreadyExists.
            await blobBlockClient.DeleteIfExistsAsync();

            // Despite the name, this step used to upload the original untouched. Shrinking it here
            // is what makes the copy in "shrinked-images" live up to the container's name.
            using (var shrunk = ShrinkForVision(sourceStream, message.BlobName, log))
            {
                var toUpload = shrunk ?? sourceStream;
                toUpload.Position = 0;
                await blobBlockClient.UploadAsync(toUpload);
            }
        }

        /// <summary>
        /// Returns a reduced JPEG copy, or null when the picture is already small enough or cannot
        /// be read. Never throws: a failure here must cost a few tokens, not a lost image, so the
        /// caller simply falls back to uploading the original.
        /// </summary>
        private static MemoryStream ShrinkForVision(MemoryStream original, string blobName, ILogger log)
        {
            try
            {
                original.Position = 0;
                using (var image = Image.FromStream(original, useEmbeddedColorManagement: false, validateImageData: false))
                {
                    int longEdge = Math.Max(image.Width, image.Height);
                    if (longEdge <= VisionMaxEdge)
                    {
                        log.LogInformation($"{blobName}: {image.Width}x{image.Height}, gia' entro {VisionMaxEdge}px, invariata.");
                        return null;
                    }

                    double scale = (double)VisionMaxEdge / longEdge;
                    int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                    int height = Math.Max(1, (int)Math.Round(image.Height * scale));

                    using (var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    using (var graphics = Graphics.FromImage(resized))
                    {
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        // JPEG has no alpha: without a white ground a transparent original would
                        // flatten onto black and hide the very silhouette to be described.
                        graphics.Clear(Color.White);
                        graphics.DrawImage(image, 0, 0, width, height);

                        var encoder = ImageCodecInfo.GetImageEncoders()
                            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                        if (encoder == null)
                        {
                            log.LogWarning("Encoder JPEG non disponibile: carico l'originale.");
                            return null;
                        }

                        var output = new MemoryStream();
                        using (var parameters = new EncoderParameters(1))
                        {
                            parameters.Param[0] = new EncoderParameter(Encoder.Quality, VisionJpegQuality);
                            resized.Save(output, encoder, parameters);
                        }

                        log.LogInformation(
                            $"{blobName}: {image.Width}x{image.Height} -> {width}x{height}, " +
                            $"{original.Length / 1024} KB -> {output.Length / 1024} KB.");

                        output.Position = 0;
                        return output;
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Ridimensionamento non riuscito per {blobName}: carico l'originale.");
                return null;
            }
            finally
            {
                original.Position = 0;
            }
        }

        private Task EnqueueMessageAsync(ResizeQueueMessage message, QueueClient queueClient)
        {
            var pathBlob = BlobContainerShrinkedName + "/" + message.BlobName;
            var resizedQueueItem = new ResizeQueueMessage
            {
                BlobName = message.BlobName,
                IdSharePoint = message.IdSharePoint,
                PathBlob = pathBlob
            };
            return queueClient.SendMessageAsync(resizedQueueItem.ToString());
        }
    }
}