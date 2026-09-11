using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using StockStudio.Api.Domain;

using StockStudio.Shared.Vettoriale;

namespace StockStudio.Api.Services;

/// <summary>
/// Two-phase pipeline: <see cref="CreateJob"/> persists uploads synchronously (streams are only
/// valid during the request); <see cref="ProcessJobAsync"/> runs vectorization + metadata in the
/// background, persisting after each item so progress is pollable and survives restarts.
/// </summary>
public class PipelineService
{
    private readonly IVectorizer _vectorizer;
    private readonly IMetadataProvider _metadata;
    private readonly IJobStore _store;
    private readonly ILogger<PipelineService> _log;

    public PipelineService(IVectorizer vectorizer, IMetadataProvider metadata, IJobStore store, ILogger<PipelineService> log)
    {
        _vectorizer = vectorizer;
        _metadata = metadata;
        _store = store;
        _log = log;
    }

    /// <summary>Persists the uploaded originals and registers a job with all items queued.</summary>
    public async Task<Job> CreateJob(IReadOnlyList<(string fileName, Stream content)> files, string mode, CancellationToken ct)
    {
        var job = new Job { Mode = Modalita.Normalizza(mode) };
        var originalDir = Path.Combine(_store.JobDir(job.Id), "original");
        Directory.CreateDirectory(originalDir);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fileName, content) in files)
        {
            var baseName = MakeUniqueBaseName(fileName, used);
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

            await using (var fs = File.Create(Path.Combine(originalDir, baseName + ext)))
                await content.CopyToAsync(fs, ct);

            job.Items.Add(new JobItem
            {
                OriginalFileName = fileName,
                BaseName = baseName,
                Status = "queued",
                Mode = job.Mode,
                QueuedAt = DateTime.UtcNow,
            });
        }

        _store.Add(job);
        return job;
    }

    /// <summary>Processes every queued item of a job (vectorize + metadata), persisting progress.</summary>
    public async Task ProcessJobAsync(string jobId, CancellationToken ct)
    {
        var job = _store.Get(jobId);
        if (job == null) { _log.LogWarning("Job {Job} non trovato", jobId); return; }

        var jobDir = _store.JobDir(job.Id);
        var originalDir = Path.Combine(jobDir, "original");

        foreach (var item in job.Items.Where(i => i.Status is "queued" or "processing"))
        {
            item.Status = "processing";
            item.StartedAt = DateTime.UtcNow;
            _store.Save(job);

            try
            {
                var originalPath = Directory.GetFiles(originalDir, item.BaseName + ".*").FirstOrDefault()
                    ?? throw new FileNotFoundException($"Originale mancante per {item.BaseName}");

                if (item.Mode == "raster")
                {
                    // No tracing: the uploaded image is the deliverable. Normalize it to a JPEG so
                    // EXIF/FTP downstream behave exactly as with traced output.
                    item.JpgFile = await PrepareRasterAsync(originalPath, jobDir, item.BaseName, ct);
                    item.SvgFile = item.EpsFile = item.AiFile = null;
                }
                else
                {
                    var vr = await _vectorizer.VectorizeAsync(originalPath, jobDir, item.BaseName, ct);
                    item.SvgFile = vr.SvgFile;
                    item.EpsFile = vr.EpsFile;
                    item.JpgFile = vr.JpgFile;
                    item.AiFile = vr.AiFile;
                }
                item.VectorizedAt = DateTime.UtcNow;

                var md = await _metadata.GenerateAsync(originalPath, item.BaseName, ct);
                item.Title = md.Title;
                item.Description = md.Description;
                item.Keywords = md.Keywords.ToList();
                item.Category = md.Category;
                item.MetadataSource = _metadata.Name;
                // Snapshot before the author touches anything: without it the review process could
                // not tell which fields were corrected, only that a note exists.
                item.AiOriginal = new MetadataSnapshot
                {
                    Title = md.Title,
                    Description = md.Description,
                    Keywords = md.Keywords.ToList(),
                    Category = md.Category,
                };

                item.Status = "completed";
                item.CompletedAt = DateTime.UtcNow;
                item.Error = null;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Errore elaborazione {File}", item.OriginalFileName);
                item.Status = "failed";
                item.Error = ex.Message;
            }

            _store.Save(job);
        }
    }

    /// <summary>Raster mode: copy the uploaded image into the job folder as a clean JPEG deliverable.</summary>
    private static async Task<string> PrepareRasterAsync(string originalPath, string jobDir, string baseName, CancellationToken ct)
    {
        Directory.CreateDirectory(jobDir);
        var outName = baseName + ".jpg";
        var outPath = Path.Combine(jobDir, outName);

        if (Path.GetExtension(originalPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(originalPath).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(originalPath, outPath, true);
            return outName;
        }

        using var img = await Image.LoadAsync(originalPath, ct);
        await img.SaveAsJpegAsync(outPath, new JpegEncoder { Quality = 95 }, ct);
        return outName;
    }

    /// <summary>Re-runs vectorization for a single item (e.g. after the user tunes the threshold), preserving metadata.</summary>
    public async Task RevectorizeItemAsync(Job job, JobItem item, bool? autoThreshold, int? threshold, CancellationToken ct)
    {
        var jobDir = _store.JobDir(job.Id);
        var originalDir = Path.Combine(jobDir, "original");
        var originalPath = Directory.GetFiles(originalDir, item.BaseName + ".*").FirstOrDefault()
            ?? throw new FileNotFoundException($"Originale mancante per {item.BaseName}");

        var vr = await _vectorizer.VectorizeAsync(originalPath, jobDir, item.BaseName, ct,
            new VectorizeOverride(autoThreshold, threshold));
        item.SvgFile = vr.SvgFile;
        item.EpsFile = vr.EpsFile;
        item.JpgFile = vr.JpgFile;
        item.AiFile = vr.AiFile;
        item.VectorizedAt = DateTime.UtcNow;
    }

    private static string MakeUniqueBaseName(string fileName, HashSet<string> used)    {
        var raw = Path.GetFileNameWithoutExtension(fileName);
        var clean = new string(raw.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (string.IsNullOrWhiteSpace(clean)) clean = "image";

        var name = clean;
        int i = 1;
        while (!used.Add(name)) name = $"{clean}_{i++}";
        return name;
    }
}
