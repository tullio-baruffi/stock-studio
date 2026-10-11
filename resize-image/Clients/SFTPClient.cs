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
            var client = new SftpClient(hostUri.Host, hostUri.Port, sftpSettings.Username, sftpSettings.Password);
            try
            {
                // I tempi di serie di SSH.NET sono trenta secondi per aprire la sessione e nessun limite
                // per le operazioni. Il primo era troppo stretto per Freepik nelle ore di carico --
                // "Session operation has timed out" -- e il secondo lascerebbe una richiesta appesa fino
                // al tempo massimo della Function. Qui si allarga il primo e si mette un tetto al
                // secondo; i nuovi tentativi li gestisce Ritenta.
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(45);
                client.OperationTimeout = TimeSpan.FromSeconds(60);
                client.Connect();

                // Set the current directory on the SFTP server
                Log.LogInformation($"Changing directory for file {fileName} to {sftpSettings.RemoteDirectory}");
                client.ChangeDirectory(sftpSettings.RemoteDirectory);

                // Upload the file
                var filePath = Path.GetFileName(fileName);
                Log.LogInformation($"Uploading file {fileName} to {sftpSettings.Name} in {filePath}");
                client.UploadFile(fileStream, filePath);
                Log.LogInformation($"Upload complete for file {fileName} to {sftpSettings.Name}");
            }
            finally
            {
                // Fuori dal blocco dell'invio: vedi Chiudi.
                Chiudi(() =>
                {
                    if (client.IsConnected) client.Disconnect();
                    client.Dispose();
                }, fileName, sftpSettings.Name);
            }
        }
    }
}