namespace StockStudio.Api.Services.Integration;

/// <summary>
/// Settings for handing off vectorized assets to the existing Azure pipeline
/// (SharePoint drop + "image-to-classify" queue → Logic App AI → EXIF → FTP).
/// Secrets (ClientId, Storage connection) belong in user-secrets / env, not appsettings.
/// </summary>
public class PipelineSettings
{
    /// <summary>Master switch. When false the site stays standalone (no SharePoint / queue).</summary>
    public bool Enabled { get; set; } = false;

    // --- SharePoint (reuses the resize-image certificate app-only auth) ---
    public string? SiteUrl { get; set; }
    public string? ClientId { get; set; }
    public string? Tenant { get; set; }

    /// <summary>Absolute path to the PnP certificate (.pfx). Empty password, as in resize-image.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// The .pfx as base64, the form a deployed instance uses: a file path baked into configuration
    /// does not exist on App Service. Takes precedence over <see cref="CertificatePath"/>.
    /// </summary>
    public string? CertificateBase64 { get; set; }

    /// <summary>Password protecting the .pfx, when it has one.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>Server-relative library folder to drop assets into, e.g. "/sites/Classifier/Immagini".</summary>
    public string? LibraryFolder { get; set; }

    // --- Azure Storage queue that Function ResizeImageToClassify listens on ---
    /// <summary>Storage account connection string. Leave empty to rely on a SharePoint-triggered Logic App instead.</summary>
    public string? StorageConnectionString { get; set; }

    public string QueueName { get; set; } = "image-to-classify";

    /// <summary>Which vector deliverable is sent through the classify pipeline (AI/EXIF/FTP): jpg | eps | svg.</summary>
    /// <summary>
    /// Which deliverable is sent onward. "auto" (default) picks by job mode — JPG for raster,
    /// EPS and SVG for vector, which is what the stock sites expect — while an explicit value
    /// forces one format for every job.
    /// </summary>
    public string DispatchFile { get; set; } = "auto";

    /// <summary>
    /// Shared secret required on POST /api/pipeline/callback (header X-Callback-Secret).
    /// Optional only in Development; mandatory when the pipeline is enabled elsewhere.
    /// </summary>
    public string? CallbackSecret { get; set; }
}
