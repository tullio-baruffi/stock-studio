import { useEffect, useRef, useState } from "react";
import { binarize, loadTraceSource, type TraceSource } from "../trace";

/**
 * Shows what potrace will be handed: the picture reduced to black and white at the chosen cut.
 *
 * The tracing itself happens later, in the Function, and nothing here is sent anywhere — only the
 * number the author settles on travels with the upload. Redrawing works on pixels decoded once, so
 * dragging the slider stays responsive on a batch of twenty cards.
 */
export default function TracePreview({
  file,
  threshold,
  onReady,
}: {
  file: File;
  /** Cut to draw at; null means "show what Otsu decides", which is also what gets sent. */
  threshold: number | null;
  /** Fires once, with the automatic threshold, so the slider can start where the default sits. */
  onReady?: (auto: number) => void;
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const sourceRef = useRef<TraceSource | null>(null);
  const onReadyRef = useRef(onReady);
  onReadyRef.current = onReady;

  const [source, setSource] = useState<TraceSource | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showOriginal, setShowOriginal] = useState(false);

  useEffect(() => {
    let alive = true;
    setSource(null);
    setError(null);
    loadTraceSource(file)
      .then((s) => {
        if (!alive) return;
        sourceRef.current = s;
        setSource(s);
        onReadyRef.current?.(s.auto);
      })
      .catch((e) => alive && setError((e as Error).message));
    return () => {
      alive = false;
    };
  }, [file]);

  useEffect(() => {
    const canvas = canvasRef.current;
    const s = sourceRef.current;
    if (!canvas || !s || !source) return;
    canvas.width = s.width;
    canvas.height = s.height;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    ctx.putImageData(showOriginal ? s.data : binarize(s.data, threshold ?? s.auto), 0, 0);
  }, [threshold, source, showOriginal]);

  if (error) return <div className="noimg" title={error}>anteprima non disponibile</div>;
  if (!source) return <div className="noimg">…</div>;

  return (
    <div className="trace-preview">
      <canvas ref={canvasRef} aria-label={`Anteprima del tracciato di ${file.name}`} />
      <button
        type="button"
        className="trace-toggle"
        onClick={() => setShowOriginal((v) => !v)}
        title="Confronta con l'immagine di partenza"
      >
        {showOriginal ? "tracciato" : "originale"}
      </button>
    </div>
  );
}
