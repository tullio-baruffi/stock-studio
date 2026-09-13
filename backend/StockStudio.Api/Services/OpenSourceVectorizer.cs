using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using StockStudio.Shared.Vettoriale;
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
public partial class OpenSourceVectorizer : IVectorizer
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
        var svgPath = Path.Combine(outputDir, baseName + ".svg");
        var epsPath = Path.Combine(outputDir, baseName + ".eps");
        var jpgPath = Path.Combine(outputDir, baseName + ".jpg");

        using (var originale = await Image.LoadAsync<Rgba32>(inputImagePath, ct))
        {
            // Il canale alfa si legge **prima** di buttarlo via: dice quali pixel sono disegno e
            // quali sono ritaglio, e senza questa distinzione il ritaglio diventa una tinta.
            var rgba = new byte[originale.Width * originale.Height * 4];
            originale.CopyPixelDataTo(rgba);
            var opachi = Trasparenza.Opachi(rgba);
            var rgb = Trasparenza.SuBianco(rgba);

            using var src = Image.LoadPixelData<Rgb24>(rgb, originale.Width, originale.Height);

            // A colori o in bianco e nero non e' una preferenza: e' una proprieta' dell'immagine.
            // Chi carica puo' imporla, ma se non dice niente la si guarda invece di supporla.
            var aColori = overrides?.Colore ?? Tavolozza.HaColori(rgb, opachi: opachi);

            if (aColori)
            {
                await TracciaAColoriAsync(src, rgb, opachi, svgPath, epsPath, jpgPath,
                                          overrides?.Tracciato ?? _opt.Tracciato, ct);
                return new VectorResult(
                    File.Exists(svgPath) ? Path.GetFileName(svgPath) : null,
                    File.Exists(epsPath) ? Path.GetFileName(epsPath) : null,
                    File.Exists(jpgPath) ? Path.GetFileName(jpgPath) : null,
                    AColori: true);
            }

            await TracciaInBiancoENeroAsync(src, svgPath, epsPath, jpgPath, overrides, outputDir, baseName, ct);
        }

        return new VectorResult(
            File.Exists(svgPath) ? Path.GetFileName(svgPath) : null,
            File.Exists(epsPath) ? Path.GetFileName(epsPath) : null,
            File.Exists(jpgPath) ? Path.GetFileName(jpgPath) : null,
            AColori: false);
    }

    /// <summary>
    /// La silhouette: una soglia di luminanza e una sola passata di potrace.
    /// E' la lavorazione giusta per un disegno gia' in bianco e nero, dove i colori non ci sono.
    /// </summary>
    private async Task TracciaInBiancoENeroAsync(Image<Rgb24> src, string svgPath, string epsPath,
                                                 string jpgPath, VectorizeOverride? overrides,
                                                 string outputDir, string baseName, CancellationToken ct)
    {
        var bmpPath = Path.Combine(outputDir, $"{baseName}.{Guid.NewGuid():N}.trace.bmp");
        var autoThreshold = overrides?.AutoThreshold ?? _opt.AutoThreshold;
        var fixedThreshold = overrides?.Threshold ?? _opt.Threshold;

        int thr = autoThreshold ? ComputeOtsu(src) : fixedThreshold;

        using (var bw = src.Clone())
        {
            bw.Mutate(x => x.BinaryThreshold(thr / 255f));

            if (_opt.InvertIfMostlyDark && BlackFraction(bw) > 0.5)
                bw.Mutate(x => x.Invert());

            await bw.SaveAsBmpAsync(bmpPath, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 }, ct);

            using var jpg = bw.Clone();
            Riduci(jpg);
            await jpg.SaveAsJpegAsync(jpgPath, new JpegEncoder { Quality = _opt.JpegQuality }, ct);
        }

        await RunPotraceAsync(bmpPath, svgPath, "svg", ct);
        await RunPotraceAsync(bmpPath, epsPath, "eps", ct);
        try { File.Delete(bmpPath); } catch { /* best effort */ }
    }

    /// <summary>
    /// Il tracciato a colori: i confini fra le tinte estratti **una volta sola** e condivisi fra le
    /// due campiture che dividono (vedi <see cref="Contorni"/>). Non c'e' piu' una passata di
    /// potrace per colore: quella disegnava ogni confine due volte, e le due copie non combaciavano.
    ///
    /// Il JPEG di consegna qui viene dall'**originale** e non dal tracciato: e' l'immagine che il
    /// cliente vede nei risultati di ricerca, e mostrargli la versione ridotta a poche tinte
    /// venderebbe peggio dell'originale senza alcun vantaggio.
    /// </summary>
    private async Task TracciaAColoriAsync(Image<Rgb24> src, byte[] rgb, bool[] opachi,
                                           string svgPath, string epsPath, string jpgPath,
                                           ParametriTracciato parametri, CancellationToken ct)
    {
        // I numeri arrivano riferiti a una grandezza convenzionale: qui si riportano a quella vera,
        // altrimenti la stessa taratura darebbe due disegni diversi sullo stesso soggetto
        // consegnato a tremila pixel e a seimila.
        var p = parametri.PerImmagine(src.Width, src.Height);

        // Il rumore si toglie **prima** di decidere quali sono le tinte: dopo, l'ondeggiamento del
        // JPEG e' gia' diventato confine, e nessuna lisciatura a valle lo puo' piu' distinguere dal
        // disegno. Vedi Rumore.
        //
        // Di che pasta sia il disegno si guarda pero' **prima della pulizia**: la mediana
        // appiattisce, e misurare dopo farebbe passare per tinte piatte anche un'illustrazione
        // ombreggiata (vedi Disegno).
        var lisciatura = Disegno.LisciaturaPer(rgb, src.Width, src.Height, opachi, p);
        rgb = Rumore.Mediana(rgb, src.Width, src.Height, p.RiduzioneRumore);

        var tavolozza = Tavolozza.Riduci(rgb, src.Width, src.Height, p.NumeroColori, p.SogliaUnione, opachi);
        // I confini si lisciano prima di tracciare: nella mappa dei colori sono scalinate alte un
        // pixel, e ricalcarle darebbe contorni ondulati.
        Tavolozza.LisciaPerTracciato(tavolozza, src.Width, src.Height, lisciatura);
        // Poi si toglie il pulviscolo. Va **dopo** la lisciatura, che nel raddrizzare i bordi puo'
        // staccare qualche granello nuovo -- misurato: invertendo l'ordine ne restavano il triplo.
        Tavolozza.TogliIGranelli(tavolozza, src.Width, src.Height, p.Granelli);
        // Le sfumature si stimano sui pixel **originali**: nella mappa ridotta non ci sono piu'.
        var rampe = Sfumatura.StimaTutte(rgb, tavolozza, src.Width, src.Height);

        ct.ThrowIfCancellationRequested();
        var contorni = Contorni.Estrai(tavolozza.Indici, tavolozza.Opaco,
                                       src.Width, src.Height, tavolozza.Colori.Length, p);
        var tinte = new List<VettorialeCondiviso.Tinta>(tavolozza.Colori.Length);
        for (var i = 0; i < tavolozza.Colori.Length; i++)
            tinte.Add(new VettorialeCondiviso.Tinta { Colore = tavolozza.Colori[i], Rampa = rampe[i] });

        var svgFinale = VettorialeCondiviso.ComponiSvg(contorni, tinte, src.Width, src.Height);
        var epsFinale = VettorialeCondiviso.ComponiEps(contorni, tinte, src.Width, src.Height);
        if (svgFinale != null) await File.WriteAllTextAsync(svgPath, svgFinale, ct);
        if (epsFinale != null) await File.WriteAllTextAsync(epsPath, epsFinale, ct);

        using var jpg = src.Clone();
        Riduci(jpg);
        await jpg.SaveAsJpegAsync(jpgPath, new JpegEncoder { Quality = _opt.JpegQuality }, ct);

        _log.LogInformation("Tracciato a colori: {Colori} tinte, {Archi} confini, {Anelli} contorni, " +
                            "lisciatura {Lisciatura}{Come}",
                            tavolozza.Colori.Length, contorni.Archi.Count,
                            contorni.Zone.Sum(z => z.Count), lisciatura,
                            p.LisciaturaAutomatica ? " (scelta guardando il disegno)" : " (imposta)");
    }

    /// <summary>Riporta il JPEG entro il lato lungo previsto, se lo supera.</summary>
    private void Riduci(Image<Rgb24> img)
    {
        var longEdge = Math.Max(img.Width, img.Height);
        if (_opt.JpegLongEdge <= 0 || longEdge <= _opt.JpegLongEdge) return;
        double s = (double)_opt.JpegLongEdge / longEdge;
        img.Mutate(x => x.Resize(
            Math.Max(1, (int)Math.Round(img.Width * s)),
            Math.Max(1, (int)Math.Round(img.Height * s))));
    }

    private string ResolvePotrace()
    {
        var p = _opt.PotracePath;
        return Path.IsPathRooted(p) ? p : Path.Combine(_env.ContentRootPath, p);
    }

    private async Task RunPotraceAsync(string bmpPath, string outPath, string backend, CancellationToken ct,
                                       int? sogliaGranelli = null)
    {
        var exe = ResolvePotrace();
        if (!File.Exists(exe))
            throw new FileNotFoundException($"potrace non trovato in '{exe}'. Configura Vectorize:PotracePath.", exe);

        var inv = CultureInfo.InvariantCulture;
        // "svg-grezzo" e' un SVG intermedio -- la maschera di un colore -- che verra' ricomposto
        // insieme agli altri: non va rifinito qui, o si riscriverebbero misure e gruppi su un
        // pezzo che da solo non e' un file.
        var grezzo = backend == "svg-grezzo";
        var vettoriale = grezzo || backend == "svg";
        var backendFlag = vettoriale ? "-s" : "-e";

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
                     "-t", (sogliaGranelli ?? _opt.TurdSize).ToString(inv),
                     "-a", _opt.AlphaMax.ToString(inv),
                     "-O", _opt.OptTolerance.ToString(inv),
                     // La risoluzione decide quanto misura la tavola, e le due uscite la esprimono
                     // in modi diversi (vedi RisoluzionePer): a 96 dpi l'EPS misura in punti quel
                     // che l'immagine misura in pixel, a 72 dpi le coordinate dell'SVG SONO i pixel.
                     "-r", RisoluzionePer(vettoriale ? "svg" : backend).ToString(inv),
                 })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Impossibile avviare potrace.");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"potrace ({backend}) exit {proc.ExitCode}: {stderr}");

        if (backend == "svg") await RifinisciSvgAsync(outPath, ct);    }
    /// <summary>
    /// La risoluzione con cui potrace converte i pixel dell'immagine in misure della tavola.
    ///
    /// Non e' una preferenza: e' la conseguenza di come i due formati esprimono le dimensioni.
    /// L'EPS puo' misurare **solo** in punti PostScript, e un punto vale 1/72 di pollice mentre un
    /// pixel ne vale 1/96: chiedendo 96 dpi, un'immagine da 1500x900 pixel diventa una tavola da
    /// 1125x675 punti, che sono esattamente 1500x900 pixel. L'SVG invece i pixel sa dichiararli, e
    /// allora conviene 72 dpi -- cosi' le coordinate dei tracciati coincidono con i pixel
    /// dell'originale e il file resta leggibile da chiunque lo apra.
    /// </summary>
    private static int RisoluzionePer(string backend) => backend == "svg" ? 72 : 96;

    /// <summary>
    /// Riscrive le misure dell'SVG in pixel e divide i tracciati in gruppi.
    ///
    /// Le misure: potrace le scrive in punti -- width="1500pt" su un'immagine da 1500 pixel. Non e'
    /// sbagliato, ma 1500 punti sono 2000 pixel, e la tavola verrebbe piu' grande dell'originale.
    /// Qui si tolgono le unita', che in SVG significa pixel, cosi' la tavola misura quanto
    /// l'immagine da cui viene e chi apre il file legge lo stesso numero che vede nel JPEG.
    ///
    /// I gruppi: potrace mette tutti i tracciati in un blocco unico, e in Illustrator non si
    /// riesce a toccare una figura sola senza selezionarla a mano pezzo per pezzo.
    /// </summary>
    private static async Task RifinisciSvgAsync(string svgPath, CancellationToken ct)
    {
        var testo = await File.ReadAllTextAsync(svgPath, ct);
        var misure = MisureInPixel(testo);
        if (misure != null) testo = misure;

        var gruppi = RaggruppaTracciati.Dividi(testo);
        if (gruppi != null) testo = gruppi;

        if (misure != null || gruppi != null) await File.WriteAllTextAsync(svgPath, testo, ct);
    }

    /// <summary>
    /// Riscrive le misure dell'SVG in pixel.
    ///
    /// potrace le scrive in punti: width="1500pt" su un'immagine da 1500 pixel. Non e' sbagliato --
    /// 1500 punti sono 2000 pixel, e la tavola verrebbe piu' grande dell'originale. Qui si tolgono
    /// le unita', che in SVG significa pixel, cosi' la tavola misura quanto l'immagine da cui viene
    /// e chi apre il file legge lo stesso numero che vede nel JPEG.
    ///
    /// Si tocca solo il primo tag &lt;svg&gt;, e solo se i numeri combaciano con il viewBox: se un
    /// domani potrace cambiasse intestazione, il file resterebbe com'e' invece di essere corrotto.
    /// </summary>
    internal static string? MisureInPixel(string svg)
    {
        var m = IntestazioneSvg().Match(svg);
        if (!m.Success) return null;

        var vb = m.Groups["vb"].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (vb.Length != 4) return null;
        if (!double.TryParse(vb[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ||
            !double.TryParse(vb[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            return null;

        var sostituito = $"width=\"{Math.Round(w)}\" height=\"{Math.Round(h)}\" viewBox=\"{m.Groups["vb"].Value}\"";
        return svg.Remove(m.Index, m.Length).Insert(m.Index, sostituito);
    }

    [GeneratedRegex("width=\"[^\"]*\"\\s+height=\"[^\"]*\"\\s+viewBox=\"(?<vb>[^\"]*)\"")]
    private static partial Regex IntestazioneSvg();

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
