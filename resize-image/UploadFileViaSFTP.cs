using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using MJ.Classifier.Models;
using MJ.Classifier.Helpers;
using Microsoft.Extensions.Options;
using System.Linq;
using MJ.Classifier.Clients;
using System.Net.Http;
using System.Text;

namespace MJ.Classifier
{
    /// <summary>
    /// Provides functionality to upload a file to SFTP via HTTP trigger function.
    /// </summary>
    public class UploadFileViaSFTP
    {
        private readonly GenericSettings _genericSettings;
        private readonly SharePointSettings _sharePointSettings;
        private readonly List<SFTPSettings> _sftpSettings;
        private readonly List<FTPSettings> _ftpSettings;
        private readonly List<WinSCPFTPSettings> _winscpFtpSettings;
        private readonly SFTPClient _sftpClient;
        private readonly FluentFTPClient _ftpClient;
        private readonly WinSCPFTPClient _winSCPFTPClient;
        private readonly ExiftoolSettings _exifSettings;
        private readonly HttpClient _httpClient;
        private readonly Uri _logicAppUrl;

        /// <summary>
        /// Initializes a new instance of the <see cref="UploadFileViaSFTP"/> class.
        /// </summary>
        /// <param name="sharePointSettings">The SharePoint settings.</param>
        /// <param name="sftpSettings">The SFTP settings.</param>
        public UploadFileViaSFTP(
            SFTPClient sftpClient
            , FluentFTPClient ftpClient
            , WinSCPFTPClient winScpFtpClient
            , IOptions<GenericSettings> genericSettings
            , IOptions<SharePointSettings> sharePointSettings
            , IOptions<List<SFTPSettings>> sftpSettings
            , IOptions<List<FTPSettings>> ftpSettings
            , IOptions<List<WinSCPFTPSettings>> winscpFtpSettings
            , IOptions<ExiftoolSettings> exifSettings
            , IHttpClientFactory httpClientFactory
        )
        {
            _genericSettings = genericSettings.Value;
            _sharePointSettings = sharePointSettings.Value;
            _sftpSettings = sftpSettings.Value;
            _ftpSettings = ftpSettings.Value;
            _winscpFtpSettings = winscpFtpSettings.Value;
            _exifSettings = exifSettings.Value;
            _sftpClient = sftpClient;
            _ftpClient = ftpClient;
            _winSCPFTPClient = winScpFtpClient;
            _httpClient = httpClientFactory.CreateClient(nameof(UploadFileViaSFTP));

            if (_genericSettings != null && !string.IsNullOrEmpty(_genericSettings.LogicAppUrl))
            {
                _logicAppUrl = new Uri(_genericSettings.LogicAppUrl);
            }
        }

        private const string QueueContainerName = "images-to-send";
        private const string ConnectionName = "rgclassifier8f3e_STORAGE";

        /// <summary>
        /// HTTP trigger function to upload a file to SFTP.
        /// </summary>
        /// <param name="req">The HTTP request.</param>
        /// <param name="config">The configuration.</param>
        /// <param name="log">The logger.</param>
        /// <returns>An IActionResult representing the result of the upload operation.</returns>
        [FunctionName("UploadFileViaSFTP")]
        public async Task Run(
            [QueueTrigger(QueueContainerName, Connection = ConnectionName)] string myQueueItem
            , ExecutionContext context
            , ILogger log)
        {
            using var scope = LoggerHelper.BeginScope(log);
            var data = JsonConvert.DeserializeObject<UploadToSFTPBody>(myQueueItem)
                ?? throw new InvalidDataException("Queue message is empty or invalid.");
            if (string.IsNullOrWhiteSpace(data.ServerRelativeUrl))
                throw new InvalidDataException("Queue message is missing ServerRelativeUrl.");

            var message = string.Empty;
            var statusCode = 0;

            try
            {
                log.LogInformation($"HTTP trigger function processing file: {data.ServerRelativeUrl}");
                var splittedUrl = data.ServerRelativeUrl.Split("/");
                var fileName = splittedUrl.LastOrDefault();
                var folderName = splittedUrl.Reverse().Skip(1).FirstOrDefault();
                // Il percorso intero, non il solo nome: ogni immagine vive nella sua sottocartella,
                // e con il solo nome la riscrittura su SharePoint finiva contro la radice del sito.
                var folderUrl = data.ServerRelativeUrl.Substring(0, data.ServerRelativeUrl.LastIndexOf('/'));
                log.LogInformation($"HTTP trigger function processing file name: {fileName}");
                log.LogInformation($"HTTP trigger function processing folder name: {folderName}");

                // Connection settings contain credentials; log only non-sensitive destination counts.
                log.LogInformation(
                    $"Configured destinations: SFTP={_sftpSettings.Count}, FTP={_ftpSettings.Count}, WinSCP={_winscpFtpSettings.Count}");

                try
                {
                    log.LogInformation($"Retrieving file {data.ServerRelativeUrl} from SharePoint");
                    using var fileAsStream = SharePointHelper.GetFileFromSharePoint(_sharePointSettings, data.ServerRelativeUrl, context.FunctionDirectory, log);

                    log.LogInformation($"Changing metadata properties to file {data.ServerRelativeUrl}");
                    //using var fileStreamWithExifMetadata = FileHelper.UpdateMetadataProperties(fileAsStream, data);
                    using var fileStreamWithExifMetadata = ExtifToolHelper.UpdateMetadataPropertiesFromStream(fileAsStream, data, _exifSettings, context.FunctionDirectory, log);
                    var outcomes = UploadToFTP(log, data, fileName, fileStreamWithExifMetadata, context);

                    // A file nobody accepted is a failure, and has to be treated as one: it must
                    // stay in the queue folder and stay retryable instead of being filed as sent.
                    var summary = DescribeOutcomes(outcomes);
                    if (outcomes.Count > 0 && outcomes.All(o => !o.Succeeded))
                        throw new InvalidOperationException(summary);

                    log.LogInformation($"File {data.ServerRelativeUrl} - {summary}");
                    message = summary;

                    log.LogInformation($"File {data.ServerRelativeUrl} updating file to SharePoint");
                    SharePointHelper.UploadFileToSharePoint(_sharePointSettings, fileStreamWithExifMetadata, fileName, folderUrl, context.FunctionDirectory, log);
                    log.LogInformation($"File {data.ServerRelativeUrl} successfully uploaded file to SharePoint");

                    statusCode = 200;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, $"Error in UploadFileViaSFTP function: {ex.Message}");
                    message = ex.Message;
                    if (ex.InnerException != null)
                    {
                        log.LogError(ex.InnerException, "Error in UploadFileViaSFTP function - InnerException");
                        message = string.Concat(message, " - ", ex.InnerException.Message);
                    }
                    statusCode = 500;
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error in UploadFileViaSFTP function while reading Body of request");
                message = ex.Message;
                statusCode = 500;
            }

            var filePath = data.ServerRelativeUrl.Split("/sites/Classifier").LastOrDefault();
            if (_logicAppUrl != null)
            {
                log.LogInformation($"Preparing Azure Logic App payload to move file {data.ServerRelativeUrl} to specific folder");
                var payload = new { data.ID, data.Identifier, filePath, StatusCode = statusCode, Message = message };
                using var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                log.LogInformation($"Rise Azure Logic App to move file {data.ServerRelativeUrl} to specific folder");
                try
                {
                    using var response = await _httpClient.PostAsync(_logicAppUrl, content);
                    if (!response.IsSuccessStatusCode)
                        log.LogWarning($"Logic App returned {(int)response.StatusCode} for {data.ServerRelativeUrl}");
                    log.LogInformation($"Request sent to move for file {data.ServerRelativeUrl}");
                }
                catch (Exception laEx)
                {
                    // Never let a Logic App outage skip the dashboard callback below.
                    log.LogError(laEx, $"Logic App notification failed for {data.ServerRelativeUrl}");
                }
            }

            // Notify the Stock Vector Studio dashboard of the final publish result + the pipeline-generated
            // metadata (title/description/tags), so the dashboard shows the REAL title/keywords produced by
            // the Logic App instead of a local placeholder. Best-effort, non-blocking.
            if (!string.IsNullOrEmpty(_genericSettings?.CallbackUrl))
            {
                try
                {
                    var callbackPayload = new
                    {
                        data.ID,
                        data.Identifier,
                        filePath,
                        StatusCode = statusCode,
                        Message = message,
                        data.Title,
                        data.Description,
                        data.Tags,
                    };
                    using var callbackRequest = new HttpRequestMessage(HttpMethod.Post, _genericSettings.CallbackUrl)
                    {
                        Content = new StringContent(JsonConvert.SerializeObject(callbackPayload), Encoding.UTF8, "application/json"),
                    };
                    if (!string.IsNullOrWhiteSpace(_genericSettings.CallbackSecret))
                        callbackRequest.Headers.TryAddWithoutValidation("X-Callback-Secret", _genericSettings.CallbackSecret);

                    using var callbackResponse = await _httpClient.SendAsync(callbackRequest);
                    if (!callbackResponse.IsSuccessStatusCode)
                        log.LogWarning($"Dashboard callback returned {(int)callbackResponse.StatusCode} for {filePath}");
                    log.LogInformation($"Dashboard callback sent for {filePath}");
                }
                catch (Exception cbEx)
                {
                    log.LogWarning(cbEx, $"Dashboard callback failed (non-blocking) for {filePath}");
                }
            }
        }

        private List<UploadOutcome> UploadToFTP(ILogger log, UploadToSFTPBody data, string fileName, Stream fileStreamWithExifMetadata, ExecutionContext context)
        {
            var outcomes = new List<UploadOutcome>();

            log.LogInformation($"Uploading file {data.ServerRelativeUrl} to SFTP");
            _sftpClient.Configure(_sftpSettings, log);
            outcomes.AddRange(_sftpClient.UploadFile(fileStreamWithExifMetadata, fileName));

            log.LogInformation($"Uploading file {data.ServerRelativeUrl} to FTP");
            _ftpClient.Configure(_ftpSettings, log);
            outcomes.AddRange(_ftpClient.UploadFile(fileStreamWithExifMetadata, fileName));

            log.LogInformation($"Uploading file {data.ServerRelativeUrl} to WinSCP SFTP");
            _winscpFtpSettings.ForEach(x => x.FunctionAppDirectory = context.FunctionAppDirectory);
            _winSCPFTPClient.Configure(_winscpFtpSettings, log);
            outcomes.AddRange(_winSCPFTPClient.UploadFile(fileStreamWithExifMetadata, fileName));

            return outcomes;
        }

        /// <summary>
        /// Turns the per-destination answers into the sentence that ends up in the Stato column.
        /// A file that only some marketplaces accepted must say so: "completed" used to be
        /// written even when every single upload had failed.
        /// </summary>
        private static string DescribeOutcomes(List<UploadOutcome> outcomes)
        {
            var delivered = outcomes.Where(o => o.Succeeded).Select(o => o.Destination).ToList();
            var refused = outcomes.Where(o => !o.Succeeded).ToList();

            if (refused.Count == 0)
                return $"caricato su {string.Join(", ", delivered)}";

            var refusedText = string.Join("; ", refused.Select(r => $"{r.Destination} ({r.Error})"));
            return delivered.Count == 0
                ? $"nessuna destinazione ha accettato il file - {refusedText}"
                : $"PARZIALE - caricato su {string.Join(", ", delivered)}; rifiutato da {refusedText}";
        }
    }
}
