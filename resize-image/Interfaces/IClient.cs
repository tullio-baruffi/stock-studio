using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using MJ.Classifier.Models;

namespace MJ.Classifier.Interfaces
{
    public interface IClient<T> where T : BaseSettings
    {
        void Configure(List<T> sftpSettings, ILogger log);
        void UploadFile(Stream fileStream, string fileName);
    }
}