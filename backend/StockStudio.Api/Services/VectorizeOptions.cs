using StockStudio.Shared.Vettoriale;

namespace StockStudio.Api.Services;

/// <summary>Tuning for the open-source (potrace) vectorization pipeline.</summary>
public class VectorizeOptions
{
    /// <summary>Vectorization engine: "opensource" (potrace, default) or "illustrator" (COM + JSX).</summary>
    public string Engine { get; set; } = "opensource";

    /// <summary>Path to potrace executable. Relative paths resolve against the content root.</summary>
    public string PotracePath { get; set; } = "tools/potrace/potrace.exe";

    /// <summary>Use Otsu automatic threshold instead of the fixed <see cref="Threshold"/>.</summary>
    public bool AutoThreshold { get; set; } = true;

    /// <summary>Fixed luminance threshold (0-255) used when <see cref="AutoThreshold"/> is false.</summary>
    public int Threshold { get; set; } = 128;

    /// <summary>Invert the traced shapes if the thresholded image is mostly black.</summary>
    public bool InvertIfMostlyDark { get; set; } = true;

    /// <summary>potrace -t : suppress speckles up to this many pixels.</summary>
    public int TurdSize { get; set; } = 2;

    /// <summary>potrace -a : corner smoothing (0 = sharp, 1.33 = smooth).</summary>
    public double AlphaMax { get; set; } = 1.0;

    /// <summary>potrace -O : curve optimization tolerance.</summary>
    public double OptTolerance { get; set; } = 0.2;

    /// <summary>
    /// Quante tinte nel tracciato a colori.
    ///
    /// Ventiquattro, e non e' un numero generoso ma il numero che serve: un'illustrazione di quelle
    /// che si vendono ha guance rosa, miele giallo, bollicine azzurre -- dettagli piccoli ma quelli
    /// che l'occhio cerca per primi. Con otto tinte sparivano tutti; con sedici le guance uscivano
    /// ancora arancioni.
    ///
    /// Otto era prudenza contro un difetto che non c'e' piu': quando le frange di contorno
    /// rubavano una tinta su tre, aggiungerne significava aggiungere aloni. Ora che la tavolozza si
    /// costruisce sui soli pixel interni, chiederne di piu' non produce sporcizia. Misurato sulla
    /// stessa illustrazione, guardando dove finisce il corallo delle guance (#ee8368 nell'originale):
    ///     chieste 16 -> 11 tinte, guancia #ef964e (arancione) al 70%, 232 KB
    ///     chieste 24 -> 12 tinte, guancia #ea7f64 (corallo)   al 98%, 229 KB
    ///
    /// **Chiederne di piu' non li inventa, ma non e' nemmeno gratis.** Il taglio mediano non si
    /// ferma: continua a dividere finche' ha caselle da dividere. Quel che si ferma e' la
    /// tavolozza, che scarta le tinte le quali descrivono una frangia di contorno invece di una
    /// zona -- e su certi disegni sono quasi tutte:
    ///     un disegno a sei colori:  chieste 48 -> 6 tinte,   8 KB   (senza lo scarto: 22 e 75 KB)
    /// Su un'illustrazione con oggetti ombreggiati invece le tinte in piu' sono vere, ma sono
    /// bande sempre piu' sottili della stessa ombreggiatura, e si pagano:
    ///     8 megapixel:              chieste 16 -> 15 tinte, 453 KB,  8,8 s
    ///                               chieste 24 -> 20 tinte, 624 KB, 11,7 s
    ///                               chieste 48 -> 23 tinte, 485 KB, 13,3 s
    /// Su un logo a tinte piatte non ne viene scartata nessuna: chiedendone 48 ne escono 32, vere.
    ///
    /// Il numero resta quindi un tetto, non una promessa: dice quante tinte al massimo, non quante
    /// se ne otterranno.
    /// </summary>
    public int NumeroColori { get; set; } = 24;

    /// <summary>
    /// Quanto insistere nel rimettere insieme le tinte che descrivono la stessa cosa: un manto
    /// ombreggiato spaccato fra due marroni indistinguibili, un contorno tracciato due volte.
    ///
    /// E' l'unico numero **empirico** della vettorializzazione a colori: gli altri sono limiti
    /// percettivi o cambi di segno, questo e' un taglio scelto guardando i dati. Misurato su 52
    /// illustrazioni del portfolio -- le coppie da fondere arrivavano a 861, quelle da tenere
    /// partivano da 1921 -- e messo in mezzo, a 1300, con un fattore due di margine.
    ///
    /// Sta qui, e si puo' scavalcare dalla pagina di caricamento, proprio perche' e' l'unico che
    /// potrebbe aver bisogno di una revisione su illustrazioni molto diverse. Zero disattiva la
    /// passata: utile per vedere la tavolozza grezza quando si indaga su un difetto.
    /// </summary>
    public double SogliaUnione { get; set; } = Tavolozza.UnionePredefinita;

    /// <summary>Longest edge (px) of the exported JPEG preview/deliverable. 0 = keep original size.</summary>
    public int JpegLongEdge { get; set; } = 4000;

    /// <summary>JPEG quality (1-100).</summary>
    public int JpegQuality { get; set; } = 92;

    /// <summary>Settings for the Illustrator engine (used when Engine == "illustrator").</summary>
    public IllustratorOptions Illustrator { get; set; } = new();
}

/// <summary>Settings for the Adobe Illustrator vectorization engine.</summary>
public class IllustratorOptions
{
    /// <summary>Path to the trace.jsx bridge. Relative paths resolve against the content root.</summary>
    public string ScriptPath { get; set; } = "Illustrator/trace.jsx";

    /// <summary>Recorded Action SET containing the trace action (for exact preset fidelity).</summary>
    public string? ActionSet { get; set; }

    /// <summary>Recorded Action NAME that applies "B&N Silhouette Auto Group" + Expand.</summary>
    public string? ActionName { get; set; }

    /// <summary>Scale applied after tracing, in percent (the manual workflow uses 200).</summary>
    public int ScalePercent { get; set; } = 200;

    /// <summary>Fallback B&W trace threshold (0-255) when no Action is configured.</summary>
    public int Threshold { get; set; } = 128;

    /// <summary>Max seconds to wait for Illustrator to finish one image.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}
