using System;
using System.Collections.Concurrent;
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

        public IReadOnlyList<UploadOutcome> UploadFile(Stream fileStream, string fileName)
        {
            if (Settings == null || Settings.Count == 0)
            {
                return Array.Empty<UploadOutcome>();
            }

            // The per-destination copies are made sequentially on purpose: the source stream is
            // shared, so reading it inside the parallel pipeline produced corrupted uploads.
            var uploads = new List<(T Setting, MemoryStream Content)>(Settings.Count);
            var outcomes = new ConcurrentBag<UploadOutcome>();
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
                        outcomes.Add(new UploadOutcome { Destination = upload.Setting.Name, Succeeded = true });
                    }
                    catch (Exception e)
                    {
                        // One failing destination must never stop the others, but it must not be
                        // forgotten either: the outcome travels back to the caller.
                        Log.LogError(e, $"Error uploading file {fileName} to {upload.Setting.Name}: {e.Message}");
                        if (e.InnerException != null)
                        {
                            Log.LogError(e.InnerException, $"Inner exception for file {fileName} to {upload.Setting.Name}: {e.InnerException.Message}");
                        }

                        // The outer message of a transfer library is often a wrapper with no
                        // information ("See InnerException for more info"): the reason is inside.
                        outcomes.Add(new UploadOutcome
                        {
                            Destination = upload.Setting.Name,
                            Succeeded = false,
                            Error = e.InnerException != null ? e.InnerException.Message : e.Message,
                        });
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

            return outcomes.ToList();
        }

        protected abstract void UploadFileToSFTP(T settings, Stream fileStream, string fileName);
    }
}