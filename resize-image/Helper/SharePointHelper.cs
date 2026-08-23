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

            web.EnsureFolderPath(folderUrl);

            var folder = web.GetFolderByServerRelativeUrl(folderUrl);
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