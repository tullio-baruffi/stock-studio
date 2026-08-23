using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using MJ.Classifier.Interfaces;
using MJ.Classifier.Models;

namespace MJ.Classifier.Clients
{
    public abstract class BaseClient<T> : IClient<T> where T : BaseSettings
    {
        protected ILogger Log;
        protected List<T> Settings;

        public void Configure(List<T> ftpSettings, ILogger log)
        {
            Log = log;

            if (ftpSettings == null)
            {
                Log.LogError("Destination settings are null");
                throw new ArgumentNullException(nameof(ftpSettings));
            }

            Settings = ftpSettings;
        }

        public void UploadFile(Stream fileStream, string fileName)
        {
            if (Settings == null || Settings.Count == 0)
            {
                return;
            }

            // The per-destination copies are made sequentially on purpose: the source stream is
            // shared, so reading it inside the parallel pipeline produced corrupted uploads.
            var uploads = new List<(T Setting, MemoryStream Content)>(Settings.Count);
            try
            {
                foreach (var setting in Settings)
                {
                    var copy = new MemoryStream();
                    fileStream.Position = 0;
                    fileStream.CopyTo(copy);
                    copy.Position = 0;
                    uploads.Add((setting, copy));
                }

                uploads.AsParallel().ForAll(upload =>
                {
                    try
                    {
                        UploadFileToSFTP(upload.Setting, upload.Content, fileName);
                    }
                    catch (Exception e)
                    {
                        // One failing destination must never stop the others.
                        Log.LogError(e, $"Error uploading file {fileName} to {upload.Setting.Name}: {e.Message}");
                        if (e.InnerException != null)
                        {
                            Log.LogError(e.InnerException, $"Inner exception for file {fileName} to {upload.Setting.Name}: {e.InnerException.Message}");
                        }
                    }
                });
            }
            finally
            {
                foreach (var upload in uploads)
                {
                    upload.Content.Dispose();
                }
            }
        }

        protected abstract void UploadFileToSFTP(T settings, Stream fileStream, string fileName);
    }
}