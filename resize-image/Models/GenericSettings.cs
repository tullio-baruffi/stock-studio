namespace MJ.Classifier.Models
{
    public class GenericSettings
    {
        public string LogicAppUrl { get; set; }

        /// <summary>
        /// Optional URL of the Stock Vector Studio dashboard callback
        /// (e.g. https://your-api/api/pipeline/callback). When set, the function notifies the
        /// dashboard of the final publish result so a file can be shown as "Published" end-to-end.
        /// </summary>
        public string CallbackUrl { get; set; }

        /// <summary>
        /// Shared secret sent to Stock Vector Studio in the X-Callback-Secret header.
        /// Configure the same value as Pipeline:CallbackSecret on the dashboard backend.
        /// </summary>
        public string CallbackSecret { get; set; }
    }
}