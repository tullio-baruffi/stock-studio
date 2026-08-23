using Newtonsoft.Json;

namespace StockStudio.Shared.Contracts
{
    /// <summary>
    /// Message placed on the "image-to-classify" queue that starts the classification pipeline.
    /// Shared contract: the Web API (producer) and the Azure Functions app (consumer) both use this
    /// exact shape so they stay wire-compatible.
    /// </summary>
    public class ResizeQueueMessage
    {
        public string? IdSharePoint { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? IdBlob { get; set; }

        public string? PathBlob { get; set; }

        public string? BlobName { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? MimeType { get; set; }

        public string? ServerRelativeUrl { get; set; }

        public override string ToString() => JsonConvert.SerializeObject(this);
    }
}
