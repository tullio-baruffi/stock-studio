namespace StockStudio.Api.Services;

/// <param name="AColori">
/// Vero se l'immagine è stata tracciata a colori, falso se come silhouette in bianco e nero,
/// nullo se chi ha tracciato non lo riporta. Serve a chi guarda il risultato: la scelta la fa
/// l'immagine, non chi la carica, quindi è l'unico modo di sapere quale delle due è avvenuta.
/// </param>
public record VectorResult(string? SvgFile, string? EpsFile, string? JpgFile, string? AiFile = null,
                           bool? AColori = null);

/// <summary>Optional per-call overrides for a single vectorization (e.g. user tuning the threshold).</summary>
/// <summary>Come vettorizzare questa immagine, quando chi carica vuole decidere invece di lasciar fare.</summary>
/// <param name="AutoThreshold">Soglia automatica (Otsu) invece di quella fissa.</param>
/// <param name="Threshold">Soglia di luminanza scelta a mano, 0-255.</param>
/// <param name="Colore">
/// Vero forza il tracciato a colori, falso la silhouette in bianco e nero.
/// Null lascia decidere all'immagine: e' il caso normale, perche' se l'immagine ha colori si vede
/// guardandola e non c'e' motivo di chiederlo.
/// </param>
/// <param name="NumeroColori">Quante tinte nel tracciato a colori. Null usa il valore configurato.</param>
/// <param name="SogliaUnione">
/// Quanto unire le tinte che descrivono la stessa cosa. Null usa il valore configurato, zero
/// disattiva la passata.
/// </param>
public record VectorizeOverride(bool? AutoThreshold = null, int? Threshold = null,
                                bool? Colore = null, int? NumeroColori = null,
                                double? SogliaUnione = null);

/// <summary>
/// Turns a raster image into vector deliverables (SVG/EPS + JPEG preview).
/// Implementations: open-source potrace, or Adobe Illustrator automation.
/// </summary>
public interface IVectorizer
{
    string Name { get; }

    Task<VectorResult> VectorizeAsync(string inputImagePath, string outputDir, string baseName, CancellationToken ct, VectorizeOverride? overrides = null);
}

