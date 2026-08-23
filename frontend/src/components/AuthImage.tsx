import { useEffect, useState } from "react";
import { fetchBlobUrl } from "../api";

/**
 * Image that loads through fetch rather than a plain <img src>.
 *
 * It used to be about the API key, which only a header could carry. Now the sign-in cookie travels
 * on its own and a plain tag would work — but the reason to keep this is the queue behind
 * fetchBlobUrl: a grid of 24 cards would otherwise open 24 parallel SharePoint downloads and
 * exhaust the free-tier worker.
 */
export default function AuthImage({ src, alt }: { src: string; alt: string }) {
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    // Dropping a card — paging, filtering, changing stage — must cancel its download too.
    // Without this the previews of pages nobody is looking at keep occupying the server.
    const abort = new AbortController();
    let objectUrl: string | null = null;

    setUrl(null);
    setFailed(false);

    fetchBlobUrl(src, abort.signal)
      .then((u) => {
        if (abort.signal.aborted) { URL.revokeObjectURL(u); return; }
        objectUrl = u;
        setUrl(u);
      })
      .catch((e) => {
        // A cancelled request is not a broken image: leave the placeholder as it is.
        if ((e as Error).name !== "AbortError") setFailed(true);
      });

    return () => {
      abort.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [src]);

  if (failed) return <div className="noimg">anteprima non disponibile</div>;
  if (!url) return <div className="noimg">…</div>;
  return <img src={url} alt={alt} />;
}
