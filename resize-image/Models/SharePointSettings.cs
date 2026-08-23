namespace MJ.Classifier.Models
{
    public class SharePointSettings
    {
        public string SiteUrl { get; set; }
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        public string CertificatePath { get; set; }
        public string Tenant { get; set; }

        /// <summary>
        /// Libreria in cui atterrano le immagini da classificare. Ogni immagine finisce in una
        /// propria sottocartella: il poller SharePoint sorveglia solo la radice, quindi cosi' i
        /// file non vengono presi in carico due volte.
        /// </summary>
        public string LibraryFolder { get; set; } = "/sites/Classifier/ImagesToClassify";
    }
}