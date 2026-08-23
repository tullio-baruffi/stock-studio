using System.IO;
using Microsoft.Extensions.Logging;
using MJ.Classifier.Models;
using WinSCP;

namespace MJ.Classifier.Clients
{
    public class WinSCPFTPClient : BaseClient<WinSCPFTPSettings>
    {
        protected override void UploadFileToSFTP(WinSCPFTPSettings settings, Stream fileStream, string fileName)
        {
            // Never serialize the settings object here: it carries the password.
            Log.LogInformation($"Creating client for file {fileName} for {settings.SafeDescription}");
            var sessionOptions = new SessionOptions
            {
                Protocol = Protocol.Ftp,
                HostName = settings.Host,
                UserName = settings.Username,
                Password = settings.Password
            };

            using Session session = new();
            session.ExecutablePath = Path.Combine(settings.FunctionAppDirectory, "winscp.exe");
            //Connect
            Log.LogInformation($"Create connection for file {fileName} to {settings.Name}");
            session.Open(sessionOptions);
            Log.LogInformation($"Connection opened for file {fileName} to {settings.Name}");

            var transferOptions = new TransferOptions()
            {
                TransferMode = TransferMode.Binary
            };

            var path = Path.Combine(settings.RemoteDirectory, fileName);
            Log.LogInformation($"Uploading file {fileName} to {settings.Name} in {path}");

            fileStream.Position = 0;
            session.PutFile(fileStream, path, transferOptions);
            Log.LogInformation($"Upload complete for file {fileName} to {settings.Name}");
        }
    }
}