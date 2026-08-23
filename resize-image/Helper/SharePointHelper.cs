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