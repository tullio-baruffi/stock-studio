using Newtonsoft.Json;

namespace MJ.Classifier.Models
{
    public abstract class BaseSettings
    {
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string RemoteDirectory { get; set; }

        /// <summary>
        /// Log-safe description of the destination: never contains the password.
        /// </summary>
        [JsonIgnore]
        public string SafeDescription => $"{Name} ({Host}:{Port} -> {RemoteDirectory})";
    }

    public class FTPSettings : BaseSettings
    {
    }

    public class WinSCPFTPSettings : BaseSettings
    {
        [JsonIgnore]
        public string FunctionAppDirectory { get; set; }
    }

    public class SFTPSettings : BaseSettings
    {
    }
}