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
