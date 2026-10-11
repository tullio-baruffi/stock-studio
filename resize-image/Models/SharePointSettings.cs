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

        /// <summary>
        /// Dove move-sent-files mette ogni file appena inviato: la radice dei pubblicati, senza
        /// sottocartelle. Serve a riconoscere il JPEG di un gruppo vettoriale anche quando SVG ed
        /// EPS sono gia' partiti e non gli stanno piu' accanto.
        /// </summary>
        public string InviatiFolder { get; set; } = "/sites/Classifier/ImagesSent";
    }
}