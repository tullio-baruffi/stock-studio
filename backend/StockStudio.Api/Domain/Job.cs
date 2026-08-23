namespace StockStudio.Api.Domain;

public class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>"vector" = trace to SVG/EPS; "raster" = keep the image as-is (photos, artwork already final).</summary>
    public string Mode { get; set; } = "vector";

    public List<JobItem> Items { get; set; } = new();
}

public class JobItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalFileName { get; set; } = "";
    public string BaseName { get; set; } = "";
    public string Status { get; set; } = "queued"; // queued|processing|completed|failed|dispatched|published
    public string? Error { get; set; }

    /// <summary>Processing mode inherited from the job: "vector" (traced) or "raster" (image kept as-is).</summary>
    public string Mode { get; set; } = "vector";

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
    public string Category { get; set; } = "";
    public string? MetadataSource { get; set; }  // provider name locally, or "pipeline" once the Logic App metadata arrives

    /// <summary>
    /// What the generator produced before any manual edit. Kept so the review process can compare
    /// it with the author's corrections: the difference is the actual lesson, the note only explains it.
    /// </summary>
    public MetadataSnapshot? AiOriginal { get; set; }

    /// <summary>Optional note in which the author explains why the metadata was corrected.</summary>
    public string? Feedback { get; set; }

    public DateTime? FeedbackAt { get; set; }

    public string? SvgFile { get; set; }
    public string? EpsFile { get; set; }
    public string? JpgFile { get; set; }
    public string? AiFile { get; set; }

    // Per-step timestamps (UTC) powering the step tracker + timing analytics.
    public DateTime? QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? VectorizedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? DispatchedAt { get; set; }

    // Set when the Azure pipeline reports back (callback) that the file was published to the stock sites.
    public DateTime? PublishedAt { get; set; }
    public string? PublishStatus { get; set; }  // published | publish_failed
    public string? PublishMessage { get; set; }
}

/// <summary>Immutable copy of a metadata set, used to compare generated vs corrected values.</summary>
public class MetadataSnapshot
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
    public string Category { get; set; } = "";
}
