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
        /// I formati che questa destinazione accetta, separati da virgola -- per esempio "svg"
        /// oppure "eps,jpg". Vuoto vuol dire tutti, che e' il comportamento di sempre.
        ///
        /// Ogni marketplace vuole quel che vuole: mandargli il resto non e' generosita', e' un
        /// rifiuto in piu' da spiegare. Adobe Stock prende il vettoriale, Freepik prende l'EPS con
        /// la sua anteprima, e chi non tratta curve prende il solo JPEG.
        /// </summary>
        public string Formati { get; set; }

        /// <summary>
        /// Vero se il file, per come si chiama, e' fra i formati che questa destinazione accetta.
        /// </summary>
        public bool AccettaIlFormato(string fileName)
        {
            if (string.IsNullOrWhiteSpace(Formati)) return true;
            if (string.IsNullOrWhiteSpace(fileName)) return false;

            var punto = fileName.LastIndexOf('.');
            if (punto < 0 || punto == fileName.Length - 1) return false;
            var estensione = fileName.Substring(punto + 1).Trim().ToLowerInvariant();
            if (estensione == "jpeg") estensione = "jpg";

            foreach (var voce in Formati.Split(new[] { ',', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                var v = voce.Trim().TrimStart('.').ToLowerInvariant();
                if (v == "jpeg") v = "jpg";
                if (v == estensione) return true;
            }
            return false;
        }

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