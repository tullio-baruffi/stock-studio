// Reproduces, in the browser, the binarization the Function applies before potrace.
//
// The durable path traces inside an Azure Function, so there is no server-rendered preview to look
// at any more: the API deposits the file and steps aside. But choosing a threshold blind is worse
// than not choosing it at all — a value picked without seeing the result is a guess dressed up as
// control. Since binarization is a per-pixel comparison, the browser can show exactly what potrace
// will be given, at no cost to anyone.
//
// "Exactly" is the whole point, so the arithmetic here mirrors VectorizeImage.cs step by step:
//   1. luminance in BT.709, rounded to nearest — what ImageSharp's BinaryThreshold measures
//   2. white when luminance >= threshold, black below
//   3. invert when more than half the result is black, because potrace traces black on white and a
//      mostly dark picture would otherwise yield the negative of the wanted silhouette
// Diverging on any of the three would make the preview a pleasant lie.

/** Longest edge of the preview. Large enough to judge a silhouette, small enough to redraw live. */
const PREVIEW_EDGE = 460;

export type TraceSource = {
  /** Downscaled pixels, kept so moving the slider costs a pass over ~200k pixels and no decoding. */
  data: ImageData;
  width: number;
  height: number;
  /** Threshold Otsu picks for this picture — the same choice the Function makes when none is sent. */
  auto: number;
};

/** BT.709 luminance, rounded to nearest, as ImageSharp measures it. */
export function luminance709(r: number, g: number, b: number): number {
  return (0.2126 * r + 0.7152 * g + 0.0722 * b + 0.5) | 0;
}

/** Decodes the file and downscales it once, then computes the automatic threshold. */
export async function loadTraceSource(file: File): Promise<TraceSource> {
  const url = URL.createObjectURL(file);
  try {
    const img = await new Promise<HTMLImageElement>((resolve, reject) => {
      const el = new Image();
      el.onload = () => resolve(el);
      el.onerror = () => reject(new Error("Immagine non leggibile dal browser."));
      el.src = url;
    });

    const longEdge = Math.max(img.naturalWidth, img.naturalHeight) || 1;
    const scale = longEdge > PREVIEW_EDGE ? PREVIEW_EDGE / longEdge : 1;
    const width = Math.max(1, Math.round(img.naturalWidth * scale));
    const height = Math.max(1, Math.round(img.naturalHeight * scale));

    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;
    const ctx = canvas.getContext("2d", { willReadFrequently: true });
    if (!ctx) throw new Error("Canvas non disponibile in questo browser.");
    ctx.drawImage(img, 0, 0, width, height);

    const data = ctx.getImageData(0, 0, width, height);
    return { data, width, height, auto: otsuFromRgba(data.data) };
  } finally {
    URL.revokeObjectURL(url);
  }
}

/**
 * Otsu's threshold: the cut that leaves the two halves of the histogram furthest apart.
 *
 * Computed on the downscaled copy, so it can land a level or two off what the Function computes on
 * the full-size original. That is fine for what it is used for — a starting position for the slider
 * and a preview of the default — and it is never the value sent: "auto" travels as auto, and the
 * Function decides on the real pixels.
 *
 * Takes raw RGBA rather than an ImageData so the arithmetic can be checked against the C# side
 * outside a browser, which is the only way to know the preview is not quietly lying.
 */
export function otsuFromRgba(px: Uint8ClampedArray | Uint8Array): number {
  const hist = new Uint32Array(256);
  for (let i = 0; i < px.length; i += 4) hist[luminance709(px[i], px[i + 1], px[i + 2])]++;

  const total = px.length / 4;
  let sum = 0;
  for (let i = 0; i < 256; i++) sum += i * hist[i];

  let sumB = 0;
  let wB = 0;
  let maxVar = -1;
  let threshold = 128;
  for (let t = 0; t < 256; t++) {
    wB += hist[t];
    if (wB === 0) continue;
    const wF = total - wB;
    if (wF === 0) break;
    sumB += t * hist[t];
    const mB = sumB / wB;
    const mF = (sum - sumB) / wF;
    const between = wB * wF * (mB - mF) * (mB - mF);
    if (between > maxVar) {
      maxVar = between;
      threshold = t;
    }
  }
  return threshold;
}

/** Fraction of the binarized image that is black, which decides whether it gets inverted. */
function blackFraction(px: Uint8ClampedArray | Uint8Array): number {
  let black = 0;
  for (let i = 0; i < px.length; i += 4) if (px[i] < 128) black++;
  return px.length === 0 ? 0 : black / (px.length / 4);
}

/**
 * Binarizes src into dst, inverting when potrace would otherwise trace the negative.
 *
 * White at exactly the threshold, not below it: ImageSharp compares luminance >= threshold, and an
 * off-by-one here would show a silhouette one level away from the one actually traced.
 */
export function binarizeRgba(src: Uint8ClampedArray | Uint8Array, dst: Uint8ClampedArray | Uint8Array, threshold: number): void {
  for (let i = 0; i < src.length; i += 4) {
    const v = luminance709(src[i], src[i + 1], src[i + 2]) >= threshold ? 255 : 0;
    dst[i] = v;
    dst[i + 1] = v;
    dst[i + 2] = v;
    dst[i + 3] = 255;
  }

  if (blackFraction(dst) > 0.5) {
    for (let i = 0; i < dst.length; i += 4) {
      dst[i] = 255 - dst[i];
      dst[i + 1] = 255 - dst[i + 1];
      dst[i + 2] = 255 - dst[i + 2];
    }
  }
}

export function binarize(source: ImageData, threshold: number): ImageData {
  const out = new ImageData(source.width, source.height);
  binarizeRgba(source.data, out.data, threshold);
  return out;
}
