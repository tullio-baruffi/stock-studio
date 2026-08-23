using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using MJ.Classifier.Models;
using MJ.Classifier.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using StockStudio.Shared.Contracts;

namespace MJ.Classifier
{
    /// <summary>
    /// Traces an uploaded picture into SVG + EPS and hands the result to the pipeline that already
    /// existed.
    ///
    /// Vectorization used to run inside the web application, on an in-memory queue: a restart in
    /// the middle of a batch — a plan change, a deploy, a crash — stopped the work until the site
    /// came back, and nothing progressed while it was down. Here the queue is the durable one, so
    /// the batch survives whatever happens to the site: the web application only drops the file in
    /// storage and posts a message, then it is free to sleep.
    ///
    /// The deliverables land in a per-image subfolder of ImagesToClassify, the same place the old
    /// dispatch used. That matters: the SharePoint poller watches only the root of that library,
    /// so files in subfolders are not picked up twice. The single classification message is posted
    /// here instead, pointing at the JPEG — the vision model downstream cannot read an EPS or SVG.
    /// </summary>
    public class VectorizeImage
    {
        const string QueueTriggerName = "images-to-vectorize";
        const string ClassifyQueueName = "image-to-classify";
        const string OriginalsContainer = "originals-to-vectorize";
        const string ConnectionName = "rgclassifier8f3e_STORAGE";

        /// <summary>Longest edge of the JPEG deliverable. Matches what the web application produced.</summary>
        const int JpegLongEdge = 4000;
        const int JpegQuality = 92;

        /// <summary>Speckles smaller than this are dropped by potrace instead of becoming stray paths.</summary>
        const int TurdSize = 2;
        const double AlphaMax = 1.0;
        const double OptTolerance = 0.2;

        private readonly SharePointSettings _sharePointSettings;

        public VectorizeImage(IOptions<SharePointSettings> sharePointSettings)
        {
            _sharePointSettings = sharePointSettings.Value;
        }

        [FunctionName("VectorizeImage")]
        public async Task Run(
            [QueueTrigger(QueueTriggerName, Connection = ConnectionName)] string myQueueItem
            , ExecutionContext context
            , [Blob(OriginalsContainer, FileAccess.ReadWrite, Connection = ConnectionName)] BlobContainerClient originals
            , [Queue(ClassifyQueueName, Connection = ConnectionName)] QueueClient classifyQueue
            , ILogger log)
        {
            using var scope = LoggerHelper.BeginScope(log);
            log.LogInformation($"Vettorializzazione richiesta: {myQueueItem}");

            VectorizeQueueMessage message = null;
            var work = Path.Combine(Path.GetTempPath(), "vectorize-" + Guid.NewGuid().ToString("N"));

            try
            {
                message = JsonConvert.DeserializeObject<VectorizeQueueMessage>(myQueueItem)
                    ?? throw new InvalidDataException("Messaggio di coda vuoto o non valido.");
                if (string.IsNullOrWhiteSpace(message.BlobName))
                    throw new InvalidDataException("Messaggio senza BlobName.");

                Directory.CreateDirectory(work);
                var baseName = Path.GetFileNameWithoutExtension(message.OriginalFileName ?? message.BlobName);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "image";

                var originalPath = Path.Combine(work, message.BlobName);
                var blob = originals.GetBlobClient(message.BlobName);
                if (!await blob.ExistsAsync())
                    throw new FileNotFoundException($"Originale non trovato nel container: {message.BlobName}");
                await blob.DownloadToAsync(originalPath);
                log.LogInformation($"Originale scaricato: {new FileInfo(originalPath).Length / 1024} KB");

                var raster = string.Equals(message.Mode, "raster", StringComparison.OrdinalIgnoreCase);
                var produced = raster
                    ? PrepareRaster(originalPath, work, baseName, log)
                    : Vectorize(originalPath, work, baseName, context.FunctionAppDirectory, log);

                // SharePoint vuole qui un percorso relativo al web ("ImagesToClassify/nome"), non
                // uno server-relative: passandogli "/sites/Classifier/..." tenta di creare la
                // gerarchia dentro il web e risponde "Accesso negato".
                var library = (_sharePointSettings.LibraryFolder ?? "/sites/Classifier/ImagesToClassify")
                    .TrimEnd('/').Split('/').Last();
                var folder = $"{library}/{baseName}";

                (int itemId, string url) classifyTarget = (0, null);
                foreach (var file in produced)
                {
                    using var ms = new MemoryStream(File.ReadAllBytes(file));
                    // Il certificato e' referenziato come "..\Certificate\pnp.pfx", relativo alla
                    // cartella della singola funzione: FunctionAppDirectory punta un livello piu'
                    // in alto e farebbe cadere il percorso fuori da wwwroot.
                    var uploaded = SharePointHelper.UploadFileToSharePointWithId(
                        _sharePointSettings, ms, Path.GetFileName(file), folder, context.FunctionDirectory, log);

                    // Il JPEG e' l'unico che un modello di visione sappia leggere: e' lui a
                    // rappresentare l'immagine nella classificazione.
                    if (Path.GetExtension(file).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                        classifyTarget = uploaded;
                }

                if (classifyTarget.url == null)
                    throw new InvalidOperationException("Nessun JPEG prodotto: senza di esso la classificazione non puo' partire.");

                var classify = new ResizeQueueMessage
                {
                    IdSharePoint = classifyTarget.itemId.ToString(),
                    ServerRelativeUrl = classifyTarget.url,
                    BlobName = Path.GetFileName(classifyTarget.url),
                    PathBlob = string.Empty,
                };
                await classifyQueue.SendMessageAsync(classify.ToString());
                log.LogInformation($"Accodato per la classificazione: item {classifyTarget.itemId}");

                // L'originale ha esaurito il suo scopo: resta su SharePoint, non serve pagarne
                // due copie.
                await blob.DeleteIfExistsAsync();
                log.LogInformation("Vettorializzazione completata");
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"Vettorializzazione fallita per {message?.BlobName ?? "(sconosciuto)"}");
                // Rilanciare lascia che sia la coda a ritentare e, se il problema persiste, a
                // spostare il messaggio in poison: meglio di un fallimento dichiarato riuscito.
                throw;
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* best effort */ }
            }
        }

        /// <summary>Modalita' immagine: nessun tracciato, solo un JPEG pulito come consegna.</summary>
        private static string[] PrepareRaster(string originalPath, string dir, string baseName, ILogger log)
        {
            var jpgPath = Path.Combine(dir, baseName + ".jpg");
            using (var img = Image.Load<Rgb24>(originalPath))
            {
                Downscale(img, JpegLongEdge);
                img.SaveAsJpeg(jpgPath, new JpegEncoder { Quality = JpegQuality });
            }
            log.LogInformation("Modalita' immagine: prodotto solo il JPEG");
            return new[] { jpgPath };
        }

        /// <summary>Soglia di luminanza (Otsu) -> potrace -> SVG + EPS, piu' il JPEG di consegna.</summary>
        private static string[] Vectorize(string originalPath, string dir, string baseName, string functionDir, ILogger log)
        {
            var bmpPath = Path.Combine(dir, baseName + ".trace.bmp");
            var svgPath = Path.Combine(dir, baseName + ".svg");
            var epsPath = Path.Combine(dir, baseName + ".eps");
            var jpgPath = Path.Combine(dir, baseName + ".jpg");

            using (var src = Image.Load<Rgb24>(originalPath))
            {
                var threshold = ComputeOtsu(src);
                using var bw = src.Clone();
                bw.Mutate(x => x.BinaryThreshold(threshold / 255f));

                // potrace traccia il nero su bianco: un'immagine prevalentemente scura darebbe
                // il negativo della silhouette voluta.
                if (BlackFraction(bw) > 0.5) bw.Mutate(x => x.Invert());

                bw.SaveAsBmp(bmpPath, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 });

                using var jpg = bw.Clone();
                Downscale(jpg, JpegLongEdge);
                jpg.SaveAsJpeg(jpgPath, new JpegEncoder { Quality = JpegQuality });
                log.LogInformation($"Soglia Otsu {threshold}, bitmap pronta per il tracciato");
            }

            RunPotrace(bmpPath, svgPath, "svg", functionDir, log);
            RunPotrace(bmpPath, epsPath, "eps", functionDir, log);
            try { File.Delete(bmpPath); } catch { /* best effort */ }

            return new[] { svgPath, epsPath, jpgPath };
        }

        private static void Downscale(Image<Rgb24> img, int maxEdge)
        {
            var longEdge = Math.Max(img.Width, img.Height);
            if (maxEdge <= 0 || longEdge <= maxEdge) return;
            var s = (double)maxEdge / longEdge;
            img.Mutate(x => x.Resize(Math.Max(1, (int)Math.Round(img.Width * s)),
                                     Math.Max(1, (int)Math.Round(img.Height * s))));
        }

        private static void RunPotrace(string bmpPath, string outPath, string backend, string functionDir, ILogger log)
        {
            var exe = Path.Combine(functionDir, "tools", "potrace", "potrace.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException($"potrace non trovato in '{exe}'.", exe);

            var inv = CultureInfo.InvariantCulture;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            // Argomenti come lista e non come riga di comando: i percorsi vengono da nomi di file
            // scelti dall'autore, e una quotatura manuale si rompe al primo apice.
            foreach (var a in new[]
                     {
                         bmpPath, backend == "svg" ? "-s" : "-e", "-o", outPath,
                         "-t", TurdSize.ToString(inv),
                         "-a", AlphaMax.ToString(inv),
                         "-O", OptTolerance.ToString(inv),
                         "--tight",
                     })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Impossibile avviare potrace.");
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"potrace ({backend}) exit {proc.ExitCode}: {stderr}");
            log.LogInformation($"potrace {backend}: {new FileInfo(outPath).Length / 1024} KB");
        }

        private static int ComputeOtsu(Image<Rgb24> img)
        {
            var hist = new int[256];
            img.ProcessPixelRows(acc =>
            {
                for (var y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        ref var p = ref row[x];
                        hist[(int)(0.299 * p.R + 0.587 * p.G + 0.114 * p.B)]++;
                    }
                }
            });

            long total = (long)img.Width * img.Height;
            double sum = 0;
            for (var i = 0; i < 256; i++) sum += i * (double)hist[i];

            double sumB = 0, maxVar = -1;
            long wB = 0;
            var thr = 128;
            for (var t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                var wF = total - wB;
                if (wF == 0) break;
                sumB += t * (double)hist[t];
                var mB = sumB / wB;
                var mF = (sum - sumB) / wF;
                var between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > maxVar) { maxVar = between; thr = t; }
            }
            return thr;
        }

        private static double BlackFraction(Image<Rgb24> img)
        {
            long black = 0;
            long total = (long)img.Width * img.Height;
            img.ProcessPixelRows(acc =>
            {
                for (var y = 0; y < acc.Height; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                        if (row[x].R < 128) black++;
                }
            });
            return total == 0 ? 0 : (double)black / total;
        }
    }
}
