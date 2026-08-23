using Newtonsoft.Json;

namespace MJ.Classifier.Models
{
    public class UploadToSFTPBody
    {
        [JsonProperty("title")]
        public string Title { get; set; }
        [JsonProperty("description")]
        public string Description { get; set; }
        [JsonProperty("tags")]
        public string Tags { get; set; }
        [JsonProperty("url")]
        public string ServerRelativeUrl { get; set; }
        [JsonProperty("Id")]
        public int ID { get; set; }
        [JsonProperty("Identifier")]
        public string Identifier { get; set; }
    }
}