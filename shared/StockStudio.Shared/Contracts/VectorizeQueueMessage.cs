using Newtonsoft.Json;

namespace StockStudio.Shared.Contracts
{
    /// <summary>
    /// Message that starts the durable path: the web application drops the original in blob
    /// storage and enqueues this, then it is out of the picture.
    ///
    /// Shared contract: the API writes it, the Function reads it. Keeping the shape in one place
    /// is what stops the two from drifting apart silently.
    /// </summary>
    public class VectorizeQueueMessage
    {
        /// <summary>Blob holding the uploaded original, inside the originals container.</summary>
        public string? BlobName { get; set; }

        /// <summary>Name the author uploaded, kept for the deliverables and the metadata hint.</summary>
        public string? OriginalFileName { get; set; }

        /// <summary>"vector" traces the silhouette; "raster" ships the picture as it is.</summary>
        public string? Mode { get; set; }

        public override string ToString() => JsonConvert.SerializeObject(this);
    }
}
