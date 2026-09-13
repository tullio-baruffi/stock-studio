using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
using StockStudio.Shared.Vettoriale;

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

        /// <summary>
        /// Dove restano gli originali dopo il tracciato, dentro lo stesso contenitore.
        /// Il backoffice cerca qui quando deve ritracciare: vedi RivettorializzaOneAsync.
        /// </summary>
        public const string PrefissoConservati = "conservati/";

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
                    : Vectorize(originalPath, work, baseName, context.FunctionAppDirectory,
                                message.Threshold, ColoreRichiesto(message.Mode), Parametri(message), log);

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

                // L'originale non si butta: e' l'unica copia mai compressa che esista, e ogni
                // ritracciamento futuro deve ripartire da li'. Quel che resta su SharePoint e'
                // un JPEG a qualita' 92, quindi ritracciare da quello vorrebbe dire ricalcare i
                // difetti della compressione invece del disegno.
                //
                // Si conserva nello stesso contenitore sotto un prefisso, con il nome della
                // cartella che l'immagine ha su SharePoint: e' l'unica chiave che il backoffice
                // sa ricostruire partendo da un file della libreria.
                //
                // Se non riesce non si alza un'eccezione: a questo punto i file sono gia' su
                // SharePoint e rilanciare farebbe ritentare la coda, che li caricherebbe una
                // seconda volta. Un originale non archiviato costa un ritracciamento peggiore;
                // un doppione in libreria costa la revisione a mano.
                try
                {
                    var conservato = originals.GetBlobClient(
                        PrefissoConservati + baseName + Path.GetExtension(message.BlobName));
                    using var copia = File.OpenRead(originalPath);
                    await conservato.UploadAsync(copia, overwrite: true);
                    log.LogInformation($"Originale conservato come {conservato.Name}");
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, $"Originale non conservato per {baseName}: i ritracciamenti " +
                                        "futuri dovranno ripartire dal JPEG di consegna.");
                }

                // La copia di lavoro invece ha esaurito il suo scopo.
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
            // Il JPEG non ha trasparenza: quel che era ritagliato va composto su bianco, o
            // uscirebbe nero -- che e' quel che accadeva leggendo direttamente in RGB.
            using (var originale = Image.Load<Rgba32>(originalPath))
            {
                var rgba = new byte[originale.Width * originale.Height * 4];
                originale.CopyPixelDataTo(rgba);
                using var img = Image.LoadPixelData<Rgb24>(Trasparenza.SuBianco(rgba),
                                                           originale.Width, originale.Height);
                Downscale(img, JpegLongEdge);
                img.SaveAsJpeg(jpgPath, new JpegEncoder { Quality = JpegQuality });
            }
            log.LogInformation("Modalita' immagine: prodotto solo il JPEG");
            return new[] { jpgPath };
        }

        /// <summary>
        /// Cosa ha chiesto chi ha caricato: colori, bianco e nero, o niente.
        ///
        /// Null non e' un'assenza di risposta ma una risposta precisa -- "guardala tu" -- ed e' il
        /// caso normale: se un'immagine ha colori si vede guardandola, e chiederlo ogni volta
        /// sarebbe far fare a mano un lavoro che la macchina fa meglio.
        ///
        /// La risposta la da' <see cref="Modalita.ColoreImposto"/>, in un posto solo: qui c'era
        /// una seconda lettura delle stesse stringhe, e le due erano gia' divergenti -- questa
        /// rispondeva "guardala tu" a un messaggio vuoto mentre la normalizzazione a monte quel
        /// vuoto l'aveva gia' trasformato in "bianco e nero". Vinceva la prima, e ogni caricamento
        /// senza preferenza usciva in silhouette.
        /// </summary>
        private static bool? ColoreRichiesto(string mode)
        {
            return Modalita.ColoreImposto(mode);
        }

        /// <summary>
        /// La taratura del tracciato che arriva nel messaggio, con i limiti applicati.
        ///
        /// Il tetto sulle tinte resta trentadue anche se <see cref="ParametriTracciato"/> ne
        /// ammette di piu': qui ogni tinta e' lavoro che gira in una Function a consumo, e chi
        /// carica non vede quel costo. Il resto lo convalida <see cref="ParametriTracciato"/>.
        /// </summary>
        private static ParametriTracciato Parametri(VectorizeQueueMessage message)
        {
            var p = message.ParametriDiTracciato();
            if (p.NumeroColori > 32) p.NumeroColori = 32;
            return p;
        }

        /// <summary>
        /// Ventiquattro tinte: e' il numero che serve, non un numero generoso. Un'illustrazione di
        /// quelle che si vendono ha guance rosa, miele giallo, bollicine azzurre -- dettagli
        /// piccoli ma quelli che l'occhio cerca per primi, e con otto tinte sparivano tutti.
        ///
        /// Otto era prudenza contro un difetto che non c'e' piu': quando le frange di contorno
        /// rubavano una tinta su tre, aggiungerne significava aggiungere aloni. Misurato ora sulla
        /// stessa illustrazione, guardando dove finisce il corallo delle guance (#ee8368):
        ///     chieste 16 -> 11 tinte, guancia #ef964e (arancione) al 70%, 232 KB
        ///     chieste 24 -> 12 tinte, guancia #ea7f64 (corallo)   al 98%, 229 KB
        ///
        /// E' un **tetto, non una promessa**: la tavolozza scarta le tinte che descrivono una
        /// frangia di contorno invece di una zona, e su certi disegni sono quasi tutte -- misurato,
        /// chiedendone 48 su un disegno a sei colori ne escono 6 e il file passa da 75 a 8 KB. Su
        /// un'illustrazione con oggetti ombreggiati invece le tinte in piu' sono vere, ma sono
        /// bande sempre piu' sottili della stessa ombreggiatura, e fanno crescere file e tempo.
        ///
        /// Va tenuto allineato al valore della web app (VectorizeOptions.NumeroColori): la stessa
        /// immagine deve dare lo stesso file da qualunque parte sia stata lavorata.
        /// </summary>
        private const int ColoriPredefiniti = 24;

        /// <summary>
        /// Il tracciato: a colori o in bianco e nero, secondo quel che l'immagine e'.
        ///
        /// La soglia arriva da chi ha caricato l'immagine quando l'ha regolata guardando
        /// l'anteprima; altrimenti la calcola Otsu. Il valore scelto a mano vince perche' e' stato
        /// deciso vedendo il risultato, cosa che l'istogramma da solo non puo' sapere.
        ///
        /// A colori o in bianco e nero non e' una preferenza ma una proprieta' dell'immagine: chi
        /// carica puo' imporla, e se non dice niente la si guarda invece di supporla. Una
        /// silhouette tracciata a colori sprecherebbe otto passate per due tinte; un'illustrazione
        /// a colori ridotta a silhouette perderebbe tutto tranne la sagoma.
        /// </summary>
        private static string[] Vectorize(string originalPath, string dir, string baseName, string functionDir,
                                          int? requestedThreshold, bool? forzaColore,
                                          ParametriTracciato parametri, ILogger log)
        {
            var svgPath = Path.Combine(dir, baseName + ".svg");
            var epsPath = Path.Combine(dir, baseName + ".eps");
            var jpgPath = Path.Combine(dir, baseName + ".jpg");

            using (var originale = Image.Load<Rgba32>(originalPath))
            {
                // Il canale alfa si legge **prima** di buttarlo via: dice quali pixel sono disegno
                // e quali sono ritaglio (vedi Trasparenza).
                var rgba = new byte[originale.Width * originale.Height * 4];
                originale.CopyPixelDataTo(rgba);
                var opachi = Trasparenza.Opachi(rgba);
                var rgb = Trasparenza.SuBianco(rgba);

                using var src = Image.LoadPixelData<Rgb24>(rgb, originale.Width, originale.Height);

                var aColori = forzaColore ?? Tavolozza.HaColori(rgb, opachi: opachi);
                if (aColori)
                {
                    TracciaAColori(src, rgb, opachi, svgPath, epsPath, jpgPath, parametri, log);
                    return new[] { svgPath, epsPath, jpgPath };
                }

                var bmpPath = Path.Combine(dir, baseName + ".trace.bmp");
                var manual = requestedThreshold.HasValue
                          && requestedThreshold.Value >= 0
                          && requestedThreshold.Value <= 255;
                var threshold = manual ? requestedThreshold.Value : ComputeOtsu(src);
                var invertito = false;
                using (var bw = src.Clone())
                {
                    bw.Mutate(x => x.BinaryThreshold(threshold / 255f));

                    // potrace traccia il nero su bianco: un'immagine prevalentemente scura darebbe
                    // il negativo della silhouette voluta.
                    if (BlackFraction(bw) > 0.5) { bw.Mutate(x => x.Invert()); invertito = true; }

                    bw.SaveAsBmp(bmpPath, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 });
                }

                // Il JPEG di consegna non esce piu' dalla bitmap della soglia. Quella ha due soli
                // livelli: la sfumatura che l'originale aveva sui bordi -- i pixel grigi che fanno
                // di una diagonale una diagonale e non una scala -- li' e' gia' stata buttata via,
                // e la compressione ci aggiunge il pulviscolo sul bianco.
                //
                // Conta doppio: e' l'immagine che il cliente vede nei risultati di ricerca, ed e'
                // anche quella che si guarda in revisione. Giudicare il tracciato da li' vuol dire
                // giudicarlo dal peggior surrogato che ne esista, e il vettoriale si prende la colpa
                // di un difetto che non ha.
                using (var jpg = src.Clone())
                {
                    SogliaMorbida(jpg, threshold, invertito);
                    Downscale(jpg, JpegLongEdge);
                    jpg.SaveAsJpeg(jpgPath, new JpegEncoder { Quality = JpegQuality });
                }
                log.LogInformation($"Silhouette, soglia {threshold} ({(manual ? "scelta a mano" : "Otsu")})");

                RunPotrace(bmpPath, svgPath, "svg", functionDir, log);
                RunPotrace(bmpPath, epsPath, "eps", functionDir, log);
                try { File.Delete(bmpPath); } catch { /* best effort */ }
            }

            return new[] { svgPath, epsPath, jpgPath };
        }

        /// <summary>
        /// I confini fra le tinte, estratti **una volta sola** e condivisi fra le due campiture che
        /// dividono (vedi Contorni). Non c'e' piu' una passata di potrace per colore: quella
        /// disegnava ogni confine due volte, e le due copie non combaciavano.
        ///
        /// Il JPEG di consegna qui viene dall'**originale** e non dal tracciato: e' l'immagine che
        /// il cliente vede nei risultati di ricerca, e mostrargli la versione ridotta a poche tinte
        /// venderebbe peggio dell'originale senza alcun vantaggio.
        /// </summary>
        private static void TracciaAColori(Image<Rgb24> src, byte[] rgb, bool[] opachi,
                                           string svgPath, string epsPath,
                                           string jpgPath, ParametriTracciato parametri,
                                           ILogger log)
        {
            // I numeri arrivano riferiti a una grandezza convenzionale: qui si riportano a quella
            // vera, altrimenti la stessa taratura darebbe due disegni diversi sullo stesso soggetto
            // consegnato a tremila pixel e a seimila.
            var p = parametri.PerImmagine(src.Width, src.Height);

            // Il rumore si toglie **prima** di decidere quali sono le tinte (vedi Rumore); ma di
            // che pasta sia il disegno si guarda prima ancora, sui pixel come sono arrivati,
            // perche' la mediana appiattisce e falserebbe la misura (vedi Disegno).
            var lisciatura = Disegno.LisciaturaPer(rgb, src.Width, src.Height, opachi, p);
            rgb = Rumore.Mediana(rgb, src.Width, src.Height, p.RiduzioneRumore);

            var tavolozza = Tavolozza.Riduci(rgb, src.Width, src.Height, p.NumeroColori, p.SogliaUnione, opachi);
            // I confini si lisciano prima di tracciare: nella mappa dei colori sono scalinate alte
            // un pixel, e ricalcarle darebbe contorni ondulati.
            Tavolozza.LisciaPerTracciato(tavolozza, src.Width, src.Height, lisciatura);
            // Poi si toglie il pulviscolo. Va **dopo** la lisciatura, che nel raddrizzare i bordi
            // puo' staccare qualche granello nuovo.
            Tavolozza.TogliIGranelli(tavolozza, src.Width, src.Height, p.Granelli);
            // Le sfumature si stimano sui pixel **originali**: nella mappa ridotta non ci sono piu'.
            var rampe = Sfumatura.StimaTutte(rgb, tavolozza, src.Width, src.Height);

            var contorni = Contorni.Estrai(tavolozza.Indici, tavolozza.Opaco,
                                           src.Width, src.Height, tavolozza.Colori.Length, p);
            var tinte = new List<VettorialeCondiviso.Tinta>(tavolozza.Colori.Length);
            for (var i = 0; i < tavolozza.Colori.Length; i++)
                tinte.Add(new VettorialeCondiviso.Tinta { Colore = tavolozza.Colori[i], Rampa = rampe[i] });

            var svgFinale = VettorialeCondiviso.ComponiSvg(contorni, tinte, src.Width, src.Height);
            var epsFinale = VettorialeCondiviso.ComponiEps(contorni, tinte, src.Width, src.Height);
            if (svgFinale != null) File.WriteAllText(svgPath, svgFinale);
            if (epsFinale != null) File.WriteAllText(epsPath, epsFinale);

            using (var jpg = src.Clone())
            {
                Downscale(jpg, JpegLongEdge);
                jpg.SaveAsJpeg(jpgPath, new JpegEncoder { Quality = JpegQuality });
            }

            log.LogInformation($"Tracciato a colori: {tavolozza.Colori.Length} tinte, " +
                               $"{contorni.Archi.Count} confini, lisciatura {lisciatura}" +
                               (p.LisciaturaAutomatica ? " (scelta guardando il disegno)" : " (imposta)"));
        }

        /// <summary>
        /// La soglia applicata come rampa invece che come taglio netto.
        ///
        /// Un taglio netto decide bianco o nero pixel per pixel, e su una diagonale il risultato e'
        /// una scala. Ma i pixel grigi che l'originale ha lungo il bordo sono esattamente
        /// l'informazione che dice **dove passa la linea** fra un pixel e il successivo: tenerli
        /// costa niente e restituisce il bordo morbido che l'occhio si aspetta.
        ///
        /// La figura resta quella che traccia il vettoriale -- stessa soglia, stesso centro della
        /// rampa -- quindi il JPEG continua a corrispondere all'SVG e all'EPS, come i marketplace
        /// pretendono. Cambia solo il bordo, che smette di essere una scalinata.
        /// </summary>
        private const int LarghezzaRampa = 16;

        private static void SogliaMorbida(Image<Rgb24> img, int soglia, bool invertito)
        {
            var lo = Math.Max(0, soglia - LarghezzaRampa);
            var hi = Math.Min(255, soglia + LarghezzaRampa);
            var ampiezza = Math.Max(1, hi - lo);

            img.ProcessPixelRows(righe =>
            {
                for (var y = 0; y < righe.Height; y++)
                {
                    var riga = righe.GetRowSpan(y);
                    for (var x = 0; x < riga.Length; x++)
                    {
                        var p = riga[x];
                        var luminanza = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                        var v = (luminanza - lo) / ampiezza;
                        if (v < 0) v = 0;
                        else if (v > 1) v = 1;
                        if (invertito) v = 1 - v;
                        var b = (byte)Math.Round(v * 255);
                        riga[x] = new Rgb24(b, b, b);
                    }
                }
            });
        }

        private static void Downscale(Image<Rgb24> img, int maxEdge)
        {
            var longEdge = Math.Max(img.Width, img.Height);
            if (maxEdge <= 0 || longEdge <= maxEdge) return;
            var s = (double)maxEdge / longEdge;
            img.Mutate(x => x.Resize(Math.Max(1, (int)Math.Round(img.Width * s)),
                                     Math.Max(1, (int)Math.Round(img.Height * s))));
        }

        private static void RunPotrace(string bmpPath, string outPath, string backend, string functionDir,
                                       ILogger log, int? sogliaGranelli = null)
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
            // "svg-grezzo" e' un SVG intermedio -- la maschera di un colore -- che verra' ricomposto
            // insieme agli altri: non va rifinito qui, o si riscriverebbero misure e gruppi su un
            // pezzo che da solo non e' un file.
            var grezzo = backend == "svg-grezzo";
            var vettoriale = grezzo || backend == "svg";

            // Argomenti come lista e non come riga di comando: i percorsi vengono da nomi di file
            // scelti dall'autore, e una quotatura manuale si rompe al primo apice.
            foreach (var a in new[]
                     {
                         bmpPath, vettoriale ? "-s" : "-e", "-o", outPath,
                         "-t", (sogliaGranelli ?? TurdSize).ToString(inv),
                         "-a", AlphaMax.ToString(inv),
                         "-O", OptTolerance.ToString(inv),
                         // La risoluzione decide quanto misura la tavola (vedi RisoluzionePer).
                         // Qui NON si passa --tight: ritagliava la tavola attorno al disegno, e su
                         // un'immagine da 1500x900 usciva una tavola da 299x198 -- misurato.
                         "-r", RisoluzionePer(vettoriale ? "svg" : backend).ToString(inv),
                     })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Impossibile avviare potrace.");
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"potrace ({backend}) exit {proc.ExitCode}: {stderr}");

            if (backend == "svg") RifinisciSvg(outPath, log);
            log.LogInformation($"potrace {backend}: {new FileInfo(outPath).Length / 1024} KB");
        }

        /// <summary>
        /// La risoluzione con cui potrace converte i pixel dell'immagine in misure della tavola.
        ///
        /// Non e' una preferenza: e' la conseguenza di come i due formati esprimono le dimensioni.
        /// L'EPS puo' misurare **solo** in punti PostScript, e un punto vale 1/72 di pollice mentre
        /// un pixel ne vale 1/96: chiedendo 96 dpi, un'immagine da 1500x900 pixel diventa una
        /// tavola da 1125x675 punti, che sono esattamente 1500x900 pixel. L'SVG invece i pixel sa
        /// dichiararli, e allora conviene 72 dpi -- cosi' le coordinate dei tracciati coincidono
        /// con i pixel dell'originale e il file resta leggibile da chiunque lo apra.
        /// </summary>
        private static int RisoluzionePer(string backend) => backend == "svg" ? 72 : 96;

        /// <summary>
        /// Riscrive le misure dell'SVG in pixel e divide i tracciati in gruppi.
        ///
        /// Le misure: potrace le scrive in punti -- width="1500pt" su un'immagine da 1500 pixel.
        /// Non e' sbagliato, ma 1500 punti sono 2000 pixel, e la tavola verrebbe piu' grande
        /// dell'originale. Qui si tolgono le unita', che in SVG significa pixel, cosi' la tavola
        /// misura quanto l'immagine da cui viene.
        ///
        /// I gruppi: potrace mette tutti i tracciati in un blocco unico, e in Illustrator non si
        /// riesce a toccare una figura sola senza selezionarla a mano pezzo per pezzo.
        /// </summary>
        private static void RifinisciSvg(string svgPath, ILogger log)
        {
            try
            {
                var testo = File.ReadAllText(svgPath);
                var misure = MisureInPixel(testo);
                if (misure != null) testo = misure;

                var gruppi = RaggruppaTracciati.Dividi(testo);
                if (gruppi != null) testo = gruppi;

                if (misure != null || gruppi != null) File.WriteAllText(svgPath, testo);
            }
            catch (Exception ex)
            {
                // L'SVG di potrace e' comunque valido: meglio consegnarlo grezzo che perderlo.
                log.LogWarning(ex, "Rifinitura dell'SVG non riuscita");
            }
        }

        private static readonly Regex IntestazioneSvg =
            new("width=\"[^\"]*\"\\s+height=\"[^\"]*\"\\s+viewBox=\"(?<vb>[^\"]*)\"", RegexOptions.Compiled);

        /// <summary>
        /// Sostituisce width/height in punti con gli stessi numeri in pixel, presi dal viewBox.
        /// Restituisce null quando non c'e' niente da cambiare: se un domani potrace cambiasse
        /// intestazione, il file resterebbe com'e' invece di essere corrotto.
        /// </summary>
        internal static string? MisureInPixel(string svg)
        {
            var m = IntestazioneSvg.Match(svg);
            if (!m.Success) return null;

            var vb = m.Groups["vb"].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (vb.Length != 4) return null;
            if (!double.TryParse(vb[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
                !double.TryParse(vb[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                return null;

            var sostituito = $"width=\"{Math.Round(w)}\" height=\"{Math.Round(h)}\" viewBox=\"{m.Groups["vb"].Value}\"";
            return svg.Remove(m.Index, m.Length).Insert(m.Index, sostituito);
        }

        /// <summary>
        /// Soglia di Otsu sulla stessa luminanza che verra' poi usata per binarizzare.
        ///
        /// Il dettaglio non e' pedanteria: BinaryThreshold di ImageSharp misura la luminanza in
        /// BT.709, e calcolare la soglia in BT.601 significherebbe deciderla su una scala e
        /// applicarla su un'altra. Su un soggetto molto saturo le due scale divergono di decine di
        /// livelli, abbastanza da mangiarsi o gonfiare la silhouette.
        /// </summary>
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
                        hist[(int)(0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B + 0.5)]++;
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
