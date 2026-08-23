using System;
using Microsoft.Extensions.Logging;

namespace MJ.Classifier.Helpers
{
    public static class LoggerHelper
    {
        public static IDisposable BeginScope(this ILogger logger, [System.Runtime.CompilerServices.CallerMemberName] string memberName = "")
        {
            var guid = Guid.NewGuid();
            return logger.BeginScope($"{memberName} Correlation Id: {guid}", guid);
        }
    }
}