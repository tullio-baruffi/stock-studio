using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Microsoft.Extensions.Logging;
using MJ.Classifier.Models;

namespace MJ.Classifier.Helpers
{
    public static class ExtifToolHelper
    {
        public static MemoryStream UpdateMetadataPropertiesFromStream(MemoryStream fileAsStream, UploadToSFTPBody data, ExiftoolSettings settings, string directoryPath, ILogger log)
        {
            using var logScope = log.BeginScope($"{nameof(ExtifToolHelper)}.{nameof(UpdateMetadataPropertiesFromStream)}");

            var tempDirectory = $"{settings.TempFileDirectory}_{Guid.NewGuid()}";
            log.LogDebug($"{data.ServerRelativeUrl} - Temp directory: {tempDirectory}");

            var workingPath = Path.Combine(Path.GetTempPath(), tempDirectory);
            log.LogDebug($"{data.ServerRelativeUrl} - Working path: {workingPath}");

            // The image handle must be released as soon as the format is known: keeping it open
            // leaked a GDI+ handle for every processed picture inside the Function host.
            string extension;
            using (var image = Image.FromStream(fileAsStream))
            {
                extension = image.ExtensionFromImageType();
            }
            log.LogDebug($"{data.ServerRelativeUrl} - Extension: {extension}");

            var filePath = Path.Combine(workingPath, $"{settings.TempFilename}.{extension}");
            log.LogDebug($"{data.ServerRelativeUrl} - File path: {filePath}");

            Directory.CreateDirectory(workingPath);
            log.LogDebug($"{data.ServerRelativeUrl} - Temp directory created");

            try
            {
                File.WriteAllBytes(filePath, fileAsStream.ToArray());
                log.LogDebug($"{data.ServerRelativeUrl} - File written to disk");

                UpdateMetadataProperties(filePath, data, directoryPath, settings, log);
                log.LogDebug($"{data.ServerRelativeUrl} - Metadata properties updated");

                var memoryStream = new MemoryStream(File.ReadAllBytes(filePath));
                log.LogDebug($"{data.ServerRelativeUrl} - File read from disk");
                return memoryStream;
            }
            finally
            {
                // Always clean up: a failure used to leave the image and the folder on disk.
                try
                {
                    Directory.Delete(workingPath, true);
                    log.LogDebug($"{data.ServerRelativeUrl} - Temp directory deleted");
                }
                catch (Exception cleanupEx)
                {
                    log.LogWarning(cleanupEx, $"{data.ServerRelativeUrl} - Temp directory not removed: {workingPath}");
                }
            }
        }

        private static void UpdateMetadataProperties(string filePath, UploadToSFTPBody data, string directoryPath, ExiftoolSettings settings, ILogger log)
        {
            using var logScope = log.BeginScope($"{nameof(ExtifToolHelper)}.{nameof(UpdateMetadataProperties)}");

            // Arguments are passed as a list, never as one command line. This is the structural fix
            // for metadata injection: a value containing a double quote used to close its argument
            // early and turn the rest into exiftool options (a title of
            // Cat" -Artist="X really did write the Artist tag). As separate argv entries no
            // character in a value can be read as an option.
            var title = Normalize(data.Title);
            var description = Normalize(data.Description);
            var tags = Normalize(data.Tags);

            // Field mapping matters: IPTC:ObjectName is the *title* field of the IIM standard
            // (2:05 Document Title), and the stock sites read the title from there or from
            // XMP:Title. It used to carry the description, which arrived truncated at the IPTC
            // 64-character limit. The description belongs in IPTC:Caption-Abstract.
            var arguments = new List<string>
            {
                "-overwrite_original",
                $"-Title={title}",
                $"-XPTitle={title}",
                $"-ObjectName={title}",
                $"-Description={description}",
                $"-ImageDescription={description}",
                $"-Caption-Abstract={description}",
                $"-XPSubject={description}",
                "-sep", ",",
                $"-Subject={tags}",
                $"-Keywords={tags}",
                $"-LastKeywordIPTC={tags}",
                $"-LastKeywordXMP={tags}",
                filePath,
            };
            log.LogDebug($"{data.ServerRelativeUrl} - Arguments: {string.Join(" ", arguments)}");

            var fileName = Path.Combine(directoryPath, settings.ExiftoolPath);
            log.LogDebug($"{data.ServerRelativeUrl} - File name: {fileName}");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    // The binary is named exiftool(-k).exe, and the "(-k)" makes it pause on
                    // "-- press ENTER --" before terminating. Owning stdin and closing it hands
                    // it an immediate EOF, instead of relying on the host having no console.
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                }
            };
            foreach (var a in arguments) process.StartInfo.ArgumentList.Add(a);

            log.LogInformation($"{data.ServerRelativeUrl} - Starting process");
            process.Start();
            log.LogInformation($"{data.ServerRelativeUrl} - Process started");

            // Close stdin straight away so the "(-k)" pause reads EOF instead of waiting.
            process.StandardInput.Close();

            // Drain the output before waiting: a full stdout buffer would block exiftool forever.
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(output))
            {
                log.LogInformation(output);
            }

            if (process.ExitCode != 0)
            {
                // The upload continues, but the file may reach the stock sites without metadata.
                log.LogWarning($"{data.ServerRelativeUrl} - exiftool exited with code {process.ExitCode}");
            }

            log.LogInformation($"{data.ServerRelativeUrl} - Process exited");
        }

        /// <summary>
        /// Formatting only, no longer a security measure: arguments travel as separate argv
        /// entries, so quotes are harmless. IPTC fields are single-line by specification, so a
        /// newline inside a value is flattened rather than written.
        /// </summary>
        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }
    }
}