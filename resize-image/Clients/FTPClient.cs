using System.IO;
using FluentFTP;
using Microsoft.Extensions.Logging;
using MJ.Classifier.Models;

namespace MJ.Classifier.Clients
{
    public class FluentFTPClient : BaseClient<FTPSettings>
    {
        protected override void UploadFileToSFTP(FTPSettings ftpSettings, Stream fileStream, string fileName)
        {
            // Never serialize the settings object here: it carries the password.
            Log.LogInformation($"Creating client for file {fileName} for {ftpSettings.SafeDescription}");
            using var client = new FtpClient(ftpSettings.Host, ftpSettings.Username, ftpSettings.Password, ftpSettings.Port);

            client.Config.RetryAttempts = 3;

            client.AutoConnect();

            var path = Path.Combine(ftpSettings.RemoteDirectory, fileName);

            Log.LogInformation($"Uploading file {fileName} to {ftpSettings.Name} in {path}");
            fileStream.Position = 0;
            client.UploadStream(fileStream, path);

            Log.LogInformation($"Upload complete for file {fileName} to {ftpSettings.Name}");
            client.Disconnect();
        }
    }
}