using System;
using System.IO;
using Microsoft.Extensions.Logging;
using MJ.Classifier.Models;
using PnP.Framework;
using Microsoft.SharePoint.Client;

namespace MJ.Classifier.Helpers
{
    public static class SharePointHelper
    {
        /// <summary>
        /// Gets the file from SharePoint.
        /// </summary>
        /// <param name="sharePointSettings">The SharePoint settings.</param>
        /// <param name="data">The data containing the server relative URL of the file.</param>
        /// <returns>The file stream.</returns>
        public static MemoryStream GetFileFromSharePoint(SharePointSettings sharePointSettings, string serverRelativeUrl, string functionDirectory, ILogger log)
        {
            var certificatePath = Path.GetFullPath(Path.Combine(functionDirectory, sharePointSettings.CertificatePath));
            log.LogInformation($"Certificate path: {certificatePath}");

            var authManager = new AuthenticationManager(sharePointSettings.ClientId, certificatePath, string.Empty, sharePointSettings.Tenant);
            using var clientContext = authManager.GetContext(sharePointSettings.SiteUrl);

            var web = clientContext.Web;
            clientContext.Load(web);
            clientContext.ExecuteQuery();

            var fileServerRelativeUrl = serverRelativeUrl;
            if (!serverRelativeUrl.StartsWith(web.ServerRelativeUrl))
                fileServerRelativeUrl = $"{web.ServerRelativeUrl}{serverRelativeUrl}";

            var file = web.GetFileByServerRelativeUrl(fileServerRelativeUrl);
            clientContext.Load(file);
            clientContext.Load(file, x => x.Name);
            clientContext.ExecuteQuery();

            log.LogInformation($"File name: {file.Name}");

            var stream = file.OpenBinaryStream();
            clientContext.ExecuteQuery();

            var outputStream = new MemoryStream();
            stream.Value.CopyTo(outputStream);

            return outputStream;
        }

        /// <summary>Marcatore di presa in carico scritto nella colonna Stato.</summary>
        private const string ClaimPrefix = "PRESO IN CARICO:";

        /// <summary>
        /// Per quanto una presa in carico vale come "ci sta gia' pensando qualcun altro".
        ///
        /// Copre la finestra fra l'accodamento e la scrittura dei metadati, che e' dove nasce il
        /// doppione: oltre quella, a dire che il lavoro e' gia' fatto bastano titolo e descrizione.
        /// Tenerla breve limita il danno di una presa in carico rimasta appesa -- per esempio se il
        /// processo venisse ucciso prima di poterla rilasciare.
        /// </summary>
        private static readonly System.TimeSpan ClaimWindow = System.TimeSpan.FromMinutes(5);

        /// <summary>
        /// Prova a prendere in carico la classificazione dell'elemento.
        ///
        /// La stessa immagine arriva in coda due volte -- una dalla vettorializzazione, che accoda
        /// subito, e una dal poller di SharePoint -- e le due possono distare pochi secondi. Non
        /// basta quindi chiedersi se i metadati esistono gia': quando il doppione arriva non
        /// esistono ancora, e si finisce per pagare due volte la stessa descrizione e per
        /// sovrascrivere quella eventualmente corretta a mano.
        ///
        /// Chi arriva primo lascia un segno nella colonna Stato; chi arriva dopo lo vede e si
        /// ritira. Non e' atomico -- fra la lettura e la scrittura passano millisecondi, e due
        /// messaggi perfettamente simultanei potrebbero passare entrambi -- ma la finestra reale e'
        /// di secondi, non di millisecondi, e il costo di sbagliare e' una classificazione di
        /// troppo, non una persa.
        ///
        /// Non solleva mai: se SharePoint non risponde si procede come prima, perche' un'immagine
        /// descritta due volte e' molto meno grave di un'immagine mai descritta.
        /// </summary>
        public static (bool Proceed, string Reason) TryClaimForClassification(
            SharePointSettings sharePointSettings, string serverRelativeUrl, string functionDirectory, ILogger log)
        {
            try
            {
                using var ctx = CreateClientContext(sharePointSettings, functionDirectory);
                var item = ResolveItem(ctx, serverRelativeUrl);
                ctx.Load(item);
                ctx.ExecuteQuery();

                var title = Field(item, "Title");
                var description = Field(item, "_ExtendedDescription");
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(description))
                    return (false, "gia' descritta");

                var stato = Field(item, "Stato") ?? "";
                if (stato.StartsWith(ClaimPrefix, System.StringComparison.Ordinal)
                    && System.DateTime.TryParse(stato.Substring(ClaimPrefix.Length).Trim(),
                                                null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var claimedAt)
                    && System.DateTime.UtcNow - claimedAt < ClaimWindow)
                    return (false, $"presa in carico {(int)(System.DateTime.UtcNow - claimedAt).TotalSeconds}s fa");

                item["Stato"] = $"{ClaimPrefix} {System.DateTime.UtcNow:o}";
                item.Update();
                ctx.ExecuteQuery();
                return (true, "presa in carico");
            }
            catch (System.Exception ex)
            {
                log.LogWarning(ex, $"Presa in carico non riuscita per {serverRelativeUrl}: procedo comunque.");
                return (true, "stato non leggibile");
            }
        }

        /// <summary>
        /// Rilascia la presa in carico, se e' ancora la nostra.
        ///
        /// Va chiamata quando il lavoro fallisce: senza, il messaggio riprovato dalla coda
        /// troverebbe il segno lasciato dal tentativo precedente e si ritirerebbe, e l'immagine non
        /// verrebbe descritta mai piu'. Non tocca uno stato che nel frattempo sia stato scritto da
        /// qualcun altro, che vorrebbe dire che il lavoro e' andato avanti davvero.
        /// </summary>
        public static void ReleaseClassificationClaim(
            SharePointSettings sharePointSettings, string serverRelativeUrl, string functionDirectory, ILogger log)
        {
            try
            {
                using var ctx = CreateClientContext(sharePointSettings, functionDirectory);
                var item = ResolveItem(ctx, serverRelativeUrl);
                ctx.Load(item);
                ctx.ExecuteQuery();

                if (!(Field(item, "Stato") ?? "").StartsWith(ClaimPrefix, System.StringComparison.Ordinal)) return;

                item["Stato"] = "";
                item.Update();
                ctx.ExecuteQuery();
                log.LogInformation($"Presa in carico rilasciata per {serverRelativeUrl}: il ritentativo potra' procedere.");
            }
            catch (System.Exception ex)
            {
                log.LogWarning(ex, $"Presa in carico non rilasciata per {serverRelativeUrl}.");
            }
        }

        private static ClientContext CreateClientContext(SharePointSettings s, string functionDirectory)
        {
            var certificatePath = Path.GetFullPath(Path.Combine(functionDirectory, s.CertificatePath));
            var authManager = new AuthenticationManager(s.ClientId, certificatePath, string.Empty, s.Tenant);
            return authManager.GetContext(s.SiteUrl);
        }

        private static ListItem ResolveItem(ClientContext ctx, string serverRelativeUrl)
        {
            var web = ctx.Web;
            ctx.Load(web);
            ctx.ExecuteQuery();

            var url = serverRelativeUrl.StartsWith(web.ServerRelativeUrl)
                ? serverRelativeUrl
                : $"{web.ServerRelativeUrl}{serverRelativeUrl}";
            return web.GetFileByServerRelativeUrl(url).ListItemAllFields;
        }

        private static string Field(ListItem item, string name) =>
            item.FieldValues.TryGetValue(name, out var v) ? v?.ToString() : null;

        public static void UploadFileToSharePoint(SharePointSettings sharePointSettings, MemoryStream memoryStream, string fileName, string folderUrl, string functionDirectory, ILogger log)
        {
            UploadFileToSharePointWithId(sharePointSettings, memoryStream, fileName, folderUrl, functionDirectory, log);
        }

        /// <summary>
        /// Uploads a file and returns the identifiers the pipeline needs to carry on: the list item
        /// id and the server-relative url. Without them the classification message could not point
        /// back at the file that was just written.
        /// </summary>
        /// <summary>
        /// Se accanto a questo file, nella stessa cartella e con lo stesso nome, c'e' un
        /// vettoriale.
        ///
        /// Serve a distinguere il JPEG di un gruppo vettoriale -- dove e' l'anteprima di una
        /// consegna che si vende come curve -- dalla fotografia, che il JPEG lo e' e basta. Le due
        /// cose vanno a destinazioni diverse, e dal solo nome del file non si distinguono.
        ///
        /// Si chiedono i due nomi precisi invece di elencare la cartella: nella radice di una
        /// libreria da migliaia di righe un elenco costerebbe quanto tutto il resto dell'invio.
        /// </summary>
        public static bool EsisteUnVettorialeAccanto(SharePointSettings sharePointSettings,
                                                     string serverRelativeUrl,
                                                     string functionDirectory, ILogger log)
        {
            var punto = serverRelativeUrl.LastIndexOf('.');
            if (punto < 0) return false;
            var senzaEstensione = serverRelativeUrl.Substring(0, punto);

            try
            {
                var certificatePath = Path.GetFullPath(Path.Combine(functionDirectory, sharePointSettings.CertificatePath));
                var authManager = new AuthenticationManager(sharePointSettings.ClientId, certificatePath, string.Empty, sharePointSettings.Tenant);
                using var clientContext = authManager.GetContext(sharePointSettings.SiteUrl);

                var web = clientContext.Web;
                clientContext.Load(web);
                clientContext.ExecuteQuery();

                foreach (var estensione in new[] { ".svg", ".eps", ".ai" })
                {
                    var url = senzaEstensione + estensione;
                    if (!url.StartsWith(web.ServerRelativeUrl)) url = $"{web.ServerRelativeUrl}{url}";

                    if (EsisteIlFile(clientContext, web, url, log))
                    {
                        log.LogInformation($"{Path.GetFileName(serverRelativeUrl)}: gruppo vettoriale (trovato {estensione})");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                // Non sapere e' diverso da sapere di no. Se la verifica non riesce si tratta il file
                // come una consegna vettoriale, cioe' si applicano le regole per formato: meglio un
                // invio in meno che lo stesso lavoro mandato due volte allo stesso marketplace.
                log.LogWarning(ex, $"{Path.GetFileName(serverRelativeUrl)}: impossibile stabilire se sia un gruppo vettoriale, lo tratto come tale");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Se un file esiste davvero, distinguendo "non c'e'" da "non si e' potuto sapere".
        ///
        /// ## Il difetto che questa distinzione chiude
        /// Qui prima c'era un <c>catch</c> senza tipo, con scritto accanto "non esiste: si prova
        /// l'estensione successiva". Quel commento dava per scontato che l'unico motivo per cui
        /// SharePoint possa rifiutare una richiesta sia l'assenza del file. Non e' cosi': risponde
        /// con un errore anche quando limita le richieste, e con quattro consegne che partono
        /// insieme succede. Ogni errore diventava allora un "non c'e'", tutte e tre le estensioni
        /// fallivano in fila, e il JPEG di un gruppo vettoriale veniva scambiato per una fotografia
        /// e mandato **anche** ad Adobe Stock, dove il vettoriale era gia' arrivato per conto suo.
        ///
        /// Il ripiego prudente scritto nel chiamante -- "non sapere e' diverso da sapere di no" --
        /// non veniva mai raggiunto, perche' l'errore era gia' stato inghiottito qui dentro.
        ///
        /// ## Cosa fa adesso
        /// Solo un "file non trovato" vale come assenza. Qualunque altro errore si ritenta un paio
        /// di volte -- le limitazioni sono quasi sempre passeggere -- e se insiste si lascia salire,
        /// perche' sia il chiamante a decidere: e lui sceglie la strada prudente.
        /// </summary>
        private static bool EsisteIlFile(ClientContext clientContext, Web web, string url, ILogger log)
        {
            const int Tentativi = 3;

            for (var giro = 1; ; giro++)
            {
                try
                {
                    var file = web.GetFileByServerRelativeUrl(url);
                    clientContext.Load(file, f => f.Exists);
                    clientContext.ExecuteQuery();
                    return file.Exists;
                }
                catch (ServerException ex) when (NonTrovato(ex))
                {
                    return false;
                }
                catch (Exception ex) when (giro < Tentativi)
                {
                    log.LogWarning($"{Path.GetFileName(url)}: verifica non riuscita al tentativo {giro} di {Tentativi} ({ex.Message}), riprovo");
                    System.Threading.Thread.Sleep(400 * giro);
                }
            }
        }

        /// <summary>
        /// Se l'errore di SharePoint dice "questo file non esiste" e non qualcos'altro.
        ///
        /// Il nome del tipo lato server e' il segnale affidabile; il testo del messaggio serve solo
        /// come rete di sicurezza, perche' cambia con la lingua del tenant.
        /// </summary>
        private static bool NonTrovato(ServerException ex)
        {
            if (string.Equals(ex.ServerErrorTypeName, "System.IO.FileNotFoundException", StringComparison.OrdinalIgnoreCase))
                return true;

            var messaggio = ex.Message ?? string.Empty;
            return messaggio.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
                || messaggio.IndexOf("non esiste", StringComparison.OrdinalIgnoreCase) >= 0
                || messaggio.IndexOf("File Not Found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static (int ItemId, string ServerRelativeUrl) UploadFileToSharePointWithId(
            SharePointSettings sharePointSettings, MemoryStream memoryStream, string fileName,
            string folderUrl, string functionDirectory, ILogger log)
        {
            log.LogInformation($"Input value: Filename: {fileName} - FolderUrl: {folderUrl}");

            var certificatePath = Path.GetFullPath(Path.Combine(functionDirectory, sharePointSettings.CertificatePath));
            log.LogInformation($"Certificate path: {certificatePath}");

            var authManager = new AuthenticationManager(sharePointSettings.ClientId, certificatePath, string.Empty, sharePointSettings.Tenant);
            using var clientContext = authManager.GetContext(sharePointSettings.SiteUrl);
            log.LogInformation("Authentication context created");

            var web = clientContext.Web;
            clientContext.Load(web);
            clientContext.ExecuteQuery();
            log.LogInformation("Web context loaded");

            // Le due chiamate qui sotto vogliono forme diverse dello stesso percorso: EnsureFolderPath
            // lo vuole relativo al web, GetFolderByServerRelativeUrl lo vuole assoluto rispetto al
            // server. Passare la stessa stringa a entrambe ha funzionato finche' i file stavano nella
            // radice della libreria, perche' li' il nome della cartella coincideva con il suo percorso.
            // Da quando ogni immagine ha la sua sottocartella, il chiamante passava il solo nome e
            // EnsureFolderPath tentava di crearlo nella radice del sito, dove l'identita' applicativa
            // non puo' scrivere: "Accesso negato" a caricamento sui marketplace gia' avvenuto.
            var webRoot = web.ServerRelativeUrl.TrimEnd('/');
            var serverRelativeFolder = folderUrl.StartsWith(webRoot + "/", StringComparison.OrdinalIgnoreCase)
                ? folderUrl.TrimEnd('/')
                : $"{webRoot}/{folderUrl.Trim('/')}";
            var webRelativeFolder = serverRelativeFolder.Substring(webRoot.Length).Trim('/');
            log.LogInformation($"Folder: web-relative '{webRelativeFolder}' - server-relative '{serverRelativeFolder}'");

            web.EnsureFolderPath(webRelativeFolder);

            var folder = web.GetFolderByServerRelativeUrl(serverRelativeFolder);
            clientContext.Load(folder);
            clientContext.ExecuteQuery();
            log.LogInformation("List folder context loaded");

            memoryStream.Position = 0;

            FileCreationInformation fileCreationInfo = new()
            {
                ContentStream = memoryStream,
                Url = fileName,
                Overwrite = true
            };

            Microsoft.SharePoint.Client.File uploadFile = folder.Files.Add(fileCreationInfo);
            clientContext.Load(uploadFile, f => f.ServerRelativeUrl, f => f.Name);
            clientContext.Load(uploadFile.ListItemAllFields);
            clientContext.ExecuteQuery();

            var itemId = uploadFile.ListItemAllFields.Id;
            log.LogInformation($"File '{fileName}' caricato con successo in SharePoint: {folderUrl} (item {itemId})");
            return (itemId, uploadFile.ServerRelativeUrl);
        }
    }
}