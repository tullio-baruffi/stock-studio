using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace StockStudio.Api.Services;

/// <summary>
/// Open-source B&amp;W silhouette vectorizer: luminance threshold (Otsu) -> potrace -> SVG + EPS,
/// plus a rasterized JPEG deliverable. Approximates Illustrator's "B&amp;N Silhouette" image trace.
/// </summary>
public class OpenSourceVectorizer : IVectorizer
{
    private readonly VectorizeOptions _opt;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<OpenSourceVectorizer> _log;

    public string Name => "opensource-potrace";

    public OpenSourceVectorizer(IOptions<VectorizeOptions> opt, IWebHostEnvironment env, ILogger<OpenSourceVectorizer> log)
    {
        _opt = opt.Value;
        _env = env;
        _log = log;
    }

    public async Task<VectorResult> VectorizeAsync(string inputImagePath, string outputDir, string baseName, CancellationToken ct, VectorizeOverride? overrides = null)
    {
        Directory.CreateDirectory(outputDir);
        var bmpPath = Path.Combine(outputDir, $"{baseName}.{Guid.NewGuid():N}.trace.bmp");
        var svgPath = Path.Combine(outputDir, baseName + ".svg");
        var epsPath = Path.Combine(outputDir, baseName + ".eps");
        var jpgPath = Path.Combine(outputDir, baseName + ".jpg");

        var autoThreshold = overrides?.AutoThreshold ?? _opt.AutoThreshold;
        var fixedThreshold = overrides?.Threshold ?? _opt.Threshold;

        using (var src = await Image.LoadAsync<Rgb24>(inputImagePath, ct))
        {
            int thr = autoThreshold ? ComputeOtsu(src) : fixedThreshold;

            using var bw = src.Clone();
            bw.Mutate(x => x.BinaryThreshold(thr / 255f));

            if (_opt.InvertIfMostlyDark && BlackFraction(bw) > 0.5)
                bw.Mutate(x => x.Invert());

            await bw.SaveAsBmpAsync(bmpPath, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 }, ct);

            using var jpg = bw.Clone();
            int longEdge = Math.Max(jpg.Width, jpg.Height);
            if (_opt.JpegLongEdge > 0 && longEdge > _opt.JpegLongEdge)
            {
                double s = (double)_opt.JpegLongEdge / longEdge;
                jpg.Mutate(x => x.Resize(
                    Math.Max(1, (int)Math.Round(jpg.Width * s)),
                    Math.Max(1, (int)Math.Round(jpg.Height * s))));
            }
            await jpg.SaveAsJpegAsync(jpgPath, new JpegEncoder { Quality = _opt.JpegQuality }, ct);
        }

        await RunPotraceAsync(bmpPath, svgPath, "svg", ct);
        await RunPotraceAsync(bmpPath, epsPath, "eps", ct);

        try { File.Delete(bmpPath); } catch { /* best effort */ }

        return new VectorResult(
            File.Exists(svgPath) ? Path.GetFileName(svgPath) : null,
            File.Exists(epsPath) ? Path.GetFileName(epsPath) : null,
            File.Exists(jpgPath) ? Path.GetFileName(jpgPath) : null);
    }

    private string ResolvePotrace()
    {
        var p = _opt.PotracePath;
        return Path.IsPathRooted(p) ? p : Path.Combine(_env.ContentRootPath, p);
    }

    private async Task RunPotraceAsync(string bmpPath, string outPath, string backend, CancellationToken ct)
    {
        var exe = ResolvePotrace();
        if (!File.Exists(exe))
            throw new FileNotFoundException($"potrace non trovato in '{exe}'. Configura Vectorize:PotracePath.", exe);

        var inv = CultureInfo.InvariantCulture;
        var backendFlag = backend == "svg" ? "-s" : "-e";

        // Arguments as a list rather than a command line: paths are built from user-supplied file
        // names and from the install directory, and manual quoting breaks as soon as either
        // contains a quote. Separate argv entries cannot be re-read as options.
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     bmpPath, backendFlag, "-o", outPath,
                     "-t", _opt.TurdSize.ToString(inv),
                     "-a", _opt.AlphaMax.ToString(inv),
                     "-O", _opt.OptTolerance.ToString(inv),
                     "--tight",
                 })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Impossibile avviare potrace.");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"potrace ({backend}) exit {proc.ExitCode}: {stderr}");
    }

    private static int ComputeOtsu(Image<Rgb24> img)
    {
        int[] hist = new int[256];
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var p = ref row[x];
                    int lum = (int)(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);
                    hist[lum]++;
                }
            }
        });

        long total = (long)img.Width * img.Height;
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * (double)hist[i];

        double sumB = 0;
        long wB = 0;
        double maxVar = -1;
        int thr = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += t * (double)hist[t];
            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > maxVar) { maxVar = between; thr = t; }
        }
        return thr;
    }

    private static double BlackFraction(Image<Rgb24> img)
    {
        long black = 0;
        long total = (long)img.Width * img.Height;
        img.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    if (row[x].R < 128) black++;
            }
        });
        return total == 0 ? 0 : (double)black / total;
    }
}
