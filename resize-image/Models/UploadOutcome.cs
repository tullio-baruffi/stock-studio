namespace MJ.Classifier.Models
{
    /// <summary>
    /// How one destination answered.
    ///
    /// Without this the caller could only ever say "done": every failure was logged and
    /// swallowed, so a file that reached one marketplace out of four was still written down as
    /// fully published, moved out of the queue folder and never retried.
    /// </summary>
    public class UploadOutcome
    {
        public string Destination { get; set; }

        public bool Succeeded { get; set; }

        /// <summary>Why the destination refused the file. Empty when it accepted it.</summary>
        public string Error { get; set; }
    }
}
