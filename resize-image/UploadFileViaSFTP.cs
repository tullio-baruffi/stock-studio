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
        /// Queue-triggered function that uploads a file to the configured marketplaces.
        /// </summary>
        /// <param name="myQueueItem">The queue message describing the file to send.</param>
        /// <param name="context">The execution context.</param>
        /// <param name="log">The logger.</param>
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
                log.LogInformation($"Queue trigger function processing file: {data.ServerRelativeUrl}");
                var splittedUrl = data.ServerRelativeUrl.Split("/");
                var fileName = splittedUrl.LastOrDefault();
                var folderName = splittedUrl.Reverse().Skip(1).FirstOrDefault();
                // Il percorso intero, non il solo nome: ogni immagine vive nella sua sottocartella,
                // e con il solo nome la riscrittura su SharePoint finiva contro la radice del sito.
                var folderUrl = data.ServerRelativeUrl.Substring(0, data.ServerRelativeUrl.LastIndexOf('/'));
                log.LogInformation($"Queue trigger function processing file name: {fileName}");
                log.LogInformation($"Queue trigger function processing folder name: {folderName}");

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

            // Le regole per formato valgono solo per le consegne vettoriali, dove il JPEG e'
            // l'anteprima di un lavoro che si vende come curve e mandarlo dove e' gia' andato il
            // vettoriale significherebbe proporre due volte la stessa cosa. Una fotografia il JPEG
            // lo e' e basta: va a tutte le destinazioni, o non si pubblicherebbe da nessuna parte.
            var vettoriale = EVettoriale(fileName)
                          || SharePointHelper.EsisteUnVettorialeAccanto(_sharePointSettings, data.ServerRelativeUrl,
                                                                        context.FunctionDirectory, log);
            if (!vettoriale)
                log.LogInformation($"{fileName}: nessun tracciato accanto, va a tutte le destinazioni");

            var sftp = PerQuestoFormato(_sftpSettings, fileName, vettoriale, log);
            var ftp = PerQuestoFormato(_ftpSettings, fileName, vettoriale, log);
            var winscp = PerQuestoFormato(_winscpFtpSettings, fileName, vettoriale, log);

            log.LogInformation($"Uploading file {data.ServerRelativeUrl} to SFTP");
            _sftpClient.Configure(sftp, log);
            outcomes.AddRange(_sftpClient.UploadFile(fileStreamWithExifMetadata, fileName));

            log.LogInformation($"Uploading file {data.ServerRelativeUrl} to FTP");
            _ftpClient.Configure(ftp, log);
            outcomes.AddRange(_ftpClient.UploadFile(fileStreamWithExifMetadata, fileName));

            log.LogInformation($"Uploading file {data.ServerRelativeUrl} to WinSCP SFTP");
            winscp.ForEach(x => x.FunctionAppDirectory = context.FunctionAppDirectory);
            _winSCPFTPClient.Configure(winscp, log);
            outcomes.AddRange(_winSCPFTPClient.UploadFile(fileStreamWithExifMetadata, fileName));

            return outcomes;
        }

        /// <summary>
        /// Le destinazioni che accettano il formato di questo file. Quelle escluse finiscono nel
        /// log per nome: un invio che non parte deve restare spiegabile, o sembrera' un guasto.
        ///
        /// Fuori da una consegna vettoriale non si filtra niente: le regole servono a non proporre
        /// due volte lo stesso lavoro, e dove il vettoriale non c'e' non c'e' nulla da evitare.
        /// </summary>
        private static List<T> PerQuestoFormato<T>(List<T> destinazioni, string fileName,
                                                   bool vettoriale, ILogger log)
            where T : BaseSettings
        {
            if (destinazioni == null || destinazioni.Count == 0) return new List<T>();
            if (!vettoriale) return new List<T>(destinazioni);

            var ammesse = destinazioni.Where(d => d.AccettaIlFormato(fileName)).ToList();
            var escluse = destinazioni.Where(d => !d.AccettaIlFormato(fileName)).Select(d => d.Name).ToList();
            if (escluse.Count > 0)
                log.LogInformation($"{fileName}: saltate per formato -> {string.Join(", ", escluse)}");
            return ammesse;
        }

        /// <summary>Un file che e' gia' di per se' un tracciato.</summary>
        private static bool EVettoriale(string fileName)
        {
            var e = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            return e == ".svg" || e == ".eps" || e == ".ai";
        }

        /// <summary>
        /// Turns the per-destination answers into the sentence that ends up in the Stato column.
        /// A file that only some marketplaces accepted must say so: "completed" used to be
        /// written even when every single upload had failed.
        /// </summary>
        private static string DescribeOutcomes(List<UploadOutcome> outcomes)
        {
            if (outcomes.Count == 0)
                return "nessuna destinazione tratta questo formato";

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
