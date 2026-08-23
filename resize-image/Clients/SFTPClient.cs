using System;
using System.IO;
using Microsoft.Extensions.Logging;
using MJ.Classifier.Models;
using Renci.SshNet;

namespace MJ.Classifier.Clients
{
    public class SFTPClient : BaseClient<SFTPSettings>
    {
        protected override void UploadFileToSFTP(SFTPSettings sftpSettings, Stream fileStream, string fileName)
        {
            fileStream.Position = 0;

            // Never serialize the settings object here: it carries the password.
            Log.LogInformation($"Creating client for file {fileName} for {sftpSettings.SafeDescription}");
            var hostUri = new Uri($"sftp://{sftpSettings.Host}:{sftpSettings.Port}");
            using var client = new SftpClient(hostUri.Host, hostUri.Port, sftpSettings.Username, sftpSettings.Password);
            client.Connect();

            // Set the current directory on the SFTP server
            Log.LogInformation($"Changing directory for file {fileName} to {sftpSettings.RemoteDirectory}");
            client.ChangeDirectory(sftpSettings.RemoteDirectory);

            // Upload the file
            var filePath = Path.GetFileName(fileName);
            Log.LogInformation($"Uploading file {fileName} to {sftpSettings.Name} in {filePath}");
            client.UploadFile(fileStream, filePath);

            client.Disconnect();
            Log.LogInformation($"Upload complete for file {fileName} to {sftpSettings.Name}");
        }
    }
}