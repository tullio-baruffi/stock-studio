import { useCallback, useEffect, useRef, useState } from "react";
import { api, fetchBlobUrl, type BackofficeItem } from "../api";
import AuthImage from "../components/AuthImage";

const STAGES = [
  { library: "ImagesToClassify", label: "Da revisionare", hint: "L'AI ha proposto i metadati: correggili e passali allo stadio successivo." },
  { library: "ImagesToSend", label: "Pronti per l'invio", hint: "Spunta Invia per far partire il caricamento sui marketplace." },
  { library: "ImagesSent", label: "Pubblicati", hint: "Già caricati su Adobe Stock e Freepik." },
];

const NEXT_STAGE: Record<string, string> = {
  ImagesToClassify: "ImagesToSend",
  ImagesToSend: "ImagesSent",
};

/** Rows per page. Kept here because the pager needs it to work out the position in the library. */
const PAGE_SIZE = 24;

/**
 * SharePoint back-office inside the app: the review, approval and clean-up work that previously
 * required opening the SharePoint libraries by hand.
 */
export default function BackofficeView() {
  const [library, setLibrary] = useState("ImagesToClassify");
  const [items, setItems] = useState<BackofficeItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const [field, setField] = useState("name");
  /**
   * Tokens of the pages visited, newest last. SharePoint only ever hands out a cursor to the
   * *next* page, so going back is possible only by remembering where each page started — without
   * this trail the pager could offer nothing but "first" and "next", which is what it did.
   */
  const [trail, setTrail] = useState<(string | null)[]>([null]);
  const [nextToken, setNextToken] = useState<string | null>(null);
  const [total, setTotal] = useState<number | null>(null);
  const [busy, setBusy] = useState<Set<number>>(new Set());
  const [bulk, setBulk] = useState(false);
  const [open, setOpen] = useState<number | null>(null);

  const pageToken = trail[trail.length - 1];
  const pageNumber = trail.length;

  /** Back to page one, used whenever the result set itself changes. */
  const resetPaging = useCallback(() => setTrail([null]), []);

  // Only the most recent request may write to the screen. Clicking through pages quickly used to
  // let a slow earlier page overwrite the one actually being viewed.
  const requestId = useRef(0);

  const load = useCallback(async (lib: string, token: string | null, q: string, fld: string) => {
    const mine = ++requestId.current;
    setLoading(true);
    setError(null);
    try {
      const page = await api.backofficeItems(lib, PAGE_SIZE, token, q || undefined, fld);
      if (mine !== requestId.current) return;

      if (!page.ok) {
        setError(page.error ?? "Lettura non riuscita.");
        setItems([]);
        setNextToken(null);
        return;
      }
      setItems(page.items);
      setNextToken(page.nextPageToken ?? null);
    } catch (e) {
      if (mine !== requestId.current) return;
      // "Failed to fetch" is what the browser says when the server drops the connection; on the
      // Free tier that means it is saturated, which is worth telling the author plainly.
      const raw = (e as Error).message;
      setError(/failed to fetch|networkerror|load failed/i.test(raw)
        ? "Il server non ha risposto: probabilmente è sotto carico. Attendi qualche secondo e premi Aggiorna."
        : raw);
      setNextToken(null);
    } finally {
      if (mine === requestId.current) setLoading(false);
    }
  }, []);

  useEffect(() => { load(library, pageToken, query, field); }, [library, pageToken, query, field, load]);

  // Rough size of the stage, to tell the author where they are in a library of thousands. It comes
  // from the library's item count, which also counts folders, so it is shown as an approximation.
  useEffect(() => {
    let alive = true;
    setTotal(null);
    api.funnel()
      .then((f) => {
        if (!alive || !f.ok) return;
        const key = library === "ImagesToClassify" ? "classify" : library === "ImagesToSend" ? "send" : "sent";
        setTotal(f.stages?.find((s) => s.key === key)?.count ?? null);
      })
      .catch(() => { /* the count is a convenience: its absence must not disturb the grid */ });
    return () => { alive = false; };
  }, [library]);

  const patch = (id: number, p: Partial<BackofficeItem>) =>
    setItems((cur) => cur.map((i) => (i.id === id ? { ...i, ...p } : i)));

  const mark = (id: number, on: boolean) =>
    setBusy((s) => { const n = new Set(s); on ? n.add(id) : n.delete(id); return n; });

  const save = async (it: BackofficeItem) => {
    mark(it.id, true);
    try {
      const r = await api.backofficeUpdate(library, it.id, {
        title: it.title,
        description: it.description,
        tags: it.keywords.join(", "),
      });
      if (!r.ok) setError(r.error ?? "Salvataggio non riuscito.");
      else if (r.item) patch(it.id, r.item);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      mark(it.id, false);
    }
  };

  const send = async (it: BackofficeItem, force = false) => {
    mark(it.id, true);
    setNotice(null);
    try {
      const r = await api.backofficeSend("ImagesToSend", it.id, true, force);
      if (r.blocked) {
        setError(`${r.error} ${(r.issues ?? []).join(" · ")}`);
      } else if (!r.ok) {
        setError(r.error ?? "Invio non riuscito.");
      } else {
        if (r.item) patch(it.id, r.item);
        setNotice(`"${it.fileName}" segnato per l'invio: la pipeline lo prende in carico entro pochi minuti.`);
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      mark(it.id, false);
    }
  };

  const move = async (it: BackofficeItem) => {
    const target = NEXT_STAGE[library];
    if (!target) return;
    mark(it.id, true);
    setNotice(null);
    try {
      const r = await api.backofficeMove(library, it.id, target);
      if (!r.ok) setError(r.error ?? "Spostamento non riuscito.");
      else {
        setItems((cur) => cur.filter((x) => x.id !== it.id));
        setNotice(`"${it.fileName}" spostato in ${target}.`);
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      mark(it.id, false);
    }
  };

  const remove = async (it: BackofficeItem) => {
    // Una riga puo' rappresentare piu' file: dirlo prima evita che l'autore scopra dopo di aver
    // cancellato anche il vettoriale che voleva vendere.
    const extra = it.deliverables?.length ?? 0;
    const cosa = extra > 1
      ? `"${it.fileName}" e le altre ${extra - 1} consegne della stessa immagine (${it.deliverables!.map((d) => d.kind).join(", ")})`
      : `"${it.fileName}"`;
    if (!confirm(`Eliminare definitivamente ${cosa} da ${library}?`)) return;
    mark(it.id, true);
    try {
      const r = await api.backofficeDelete(library, it.id);
      if (!r.ok) setError(r.error ?? "Eliminazione non riuscita.");
      else {
        setItems((cur) => cur.filter((x) => x.id !== it.id));
        setNotice(extra > 1 ? `"${it.fileName}" eliminato con le sue ${extra - 1} consegne.` : `"${it.fileName}" eliminato.`);
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      mark(it.id, false);
    }
  };

  /** Opens the full-size original, fetched through the shared preview queue rather than directly. */
  const openOriginal = async (it: BackofficeItem) => {
    try {
      const url = await fetchBlobUrl(it.previewUrl.replace("w=480&", ""));
      window.open(url, "_blank", "noopener");
      // Give the new tab time to load before releasing the object URL.
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  /**
   * Releases a file someone left checked out in SharePoint. Until that happens SharePoint refuses
   * every metadata write on it, so editing, Invia and the Logic App all fail on that one file.
   */
  const checkIn = async (it: BackofficeItem, discard = false) => {
    if (discard && !confirm(
      `Ignorare l'estrazione di "${it.fileName}"?\n\n` +
      `Le modifiche non salvate di ${it.checkedOutBy} andranno perse e il file tornerà all'ultima versione archiviata.`
    )) return;

    mark(it.id, true);
    setNotice(null);
    try {
      const r = await api.backofficeCheckIn(library, it.id, discard);
      if (!r.ok) setError(r.error ?? "Archiviazione non riuscita.");
      else {
        if (r.item) patch(it.id, r.item);
        setNotice(`"${it.fileName}": ${r.message}`);
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      mark(it.id, false);
    }
  };

  const regenerate = async (it: BackofficeItem) => {
    mark(it.id, true);
    setNotice(null);
    try {
      const r = await api.backofficeRegenerate(library, it.id);
      if (!r.ok) setError(r.error ?? "Rigenerazione non riuscita.");
      else if (r.item) {
        patch(it.id, r.item);
        setNotice(`"${it.fileName}": titolo da ${r.previousTitleLength} a ${r.item.title.length} caratteri, ` +
                  `keyword da ${r.previousKeywords} a ${r.item.keywords.length}.`);
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      mark(it.id, false);
    }
  };

  const regeneratePage = async () => {
    // Checked-out files would burn a paid AI call and then fail on the write, so they stay out.
    const targets = items.filter((i) => !i.inviato && !i.checkedOutBy);
    if (targets.length === 0) return;
    if (!confirm(
      `Rigenerare i metadati di ${targets.length} file con il prompt corrente?\n\n` +
      `Ogni file richiede una chiamata AI a pagamento e i metadati attuali verranno sovrascritti.`
    )) return;

    setBulk(true);
    setNotice(null);
    setError(null);
    try {
      const r = await api.backofficeRegenerateMany(library, targets.map((t) => t.id));
      for (const res of r.results) if (res.ok && res.item) patch(res.id, res.item);
      const failed = r.results.filter((x) => !x.ok);
      setNotice(`Rigenerati ${r.succeeded} file su ${r.requested}.` +
                (failed.length ? ` Non riusciti: ${failed.map((x) => x.fileName || x.id).join(", ")}.` : ""));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBulk(false);
    }
  };

  const stage = STAGES.find((s) => s.library === library)!;

  // Position in the library. The page size is fixed, so the first item's index follows from the
  // page number; the last one from how many rows this page actually returned.
  const first = (pageNumber - 1) * PAGE_SIZE + 1;
  const last = first + Math.max(items.length, 1) - 1;

  return (
    <div className="backoffice">
      <div className="cfg-hero">
        <div>
          <h1>Backoffice</h1>
          <p>
            I file della pipeline su SharePoint, gestibili da qui: rivedi i metadati proposti
            dall'AI, spostali allo stadio successivo e avvia l'invio ai marketplace senza aprire
            SharePoint.
          </p>
        </div>
        <button className="btn" onClick={() => load(library, pageToken, query, field)} disabled={loading}>
          {loading ? "Carico…" : "Aggiorna"}
        </button>
      </div>

      <div className="bo-stages">
        {STAGES.map((s) => (
          <button
            key={s.library}
            className={`bo-stage ${library === s.library ? "active" : ""}`}
            onClick={() => { setLibrary(s.library); resetPaging(); }}
          >
            {s.label}
          </button>
        ))}
        <form
          className="bo-search"
          onSubmit={(e) => { e.preventDefault(); resetPaging(); setQuery(search.trim()); }}
        >
          <select value={field} onChange={(e) => { setField(e.target.value); resetPaging(); }} aria-label="Campo di ricerca">
            <option value="name">Nome file</option>
            <option value="title">Titolo</option>
            <option value="keyword">Keyword</option>
            <option value="stato">Stato</option>
          </select>
          <input
            placeholder={field === "name" || field === "title" ? "inizia per…" : "contiene…"}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <button className="btn small" type="submit">Cerca</button>
          {query && (
            <button className="btn small ghost" type="button" onClick={() => { setSearch(""); setQuery(""); resetPaging(); }}>
              Azzera
            </button>
          )}
        </form>
      </div>

      <div className="muted small" style={{ marginBottom: 10 }}>{stage.hint}</div>

      {library !== "ImagesSent" && items.length > 0 && (
        <div className="bo-bulk">
          <span>
            I file descritti dal prompt precedente hanno titoli lunghi e non seguono le regole Adobe.
            Puoi riscriverli con il prompt corrente.
          </span>
          <button className="btn small" onClick={regeneratePage} disabled={bulk || loading}>
            {bulk ? "Rigenerazione in corso…" : `Rigenera i ${items.filter((i) => !i.inviato && !i.checkedOutBy).length} file di questa pagina`}
          </button>
        </div>
      )}

      {error && <div className="notice err" role="alert">{error}<button className="rr-close" onClick={() => setError(null)}>×</button></div>}
      {notice && <div className="notice" role="status">{notice}<button className="rr-close" onClick={() => setNotice(null)}>×</button></div>}

      {loading && items.length === 0 && <div className="empty">Lettura della libreria in corso…</div>}
      {!loading && items.length === 0 && !error && <div className="empty">Nessun file in questo stadio.</div>}

      <div className="grid">
        {items.map((it) => {
          const working = busy.has(it.id);
          const expanded = open === it.id;
          const locked = Boolean(it.checkedOutBy);
          // A checked-out file rejects every write, so editing it would only produce errors.
          const readOnly = it.inviato || locked;
          return (
            <article key={it.id} className="card">
              <div className="preview">
                <AuthImage src={it.previewUrl} alt={it.title || it.fileName} />
              </div>
              <div className="meta">
                <div className="row between">
                  <span className="fname" title={it.fileName}>{it.fileName}</span>
                  <span className={`bo-score ${it.validation.score >= 90 ? "ok" : it.validation.score >= 70 ? "warn" : "bad"}`}>
                    {it.validation.score}
                  </span>
                </div>
                <div className="bo-state">{it.stato || "—"}</div>
                {it.deliverables && it.deliverables.length > 1 && (
                  <div className="bo-kinds" title={it.deliverables.map((d) => d.fileName).join("\n")}>
                    {it.deliverables.map((d) => (
                      <span key={d.id} className={`bo-kind ${d.carrier ? "carrier" : ""}`}>
                        {d.kind}
                      </span>
                    ))}
                    <span className="muted small">un'unica immagine · i metadati valgono per tutte</span>
                  </div>
                )}
                {locked && (
                  <div className="bo-flag pending">
                    Estratto da {it.checkedOutBy}: SharePoint rifiuta ogni modifica finché non lo archivi.
                  </div>
                )}
                {it.inviato && <div className="bo-flag sent">Già inviato alla pipeline</div>}
                {it.invia && !it.inviato && <div className="bo-flag pending">In attesa della pipeline</div>}

                <label>Titolo ({it.title.length} car.)</label>
                <input
                  value={it.title}
                  onChange={(e) => patch(it.id, { title: e.target.value })}
                  onBlur={() => save(it)}
                  disabled={readOnly}
                />

                <label>Keyword ({it.keywords.length})</label>
                <textarea
                  rows={3}
                  value={it.keywords.join(", ")}
                  onChange={(e) => patch(it.id, { keywords: e.target.value.split(",").map((k) => k.trim()).filter(Boolean) })}
                  onBlur={() => save(it)}
                  disabled={readOnly}
                />

                <button className="btn small ghost" onClick={() => setOpen(expanded ? null : it.id)}>
                  {expanded ? "Nascondi descrizione e controlli" : "Descrizione e controlli"}
                </button>

                {expanded && (
                  <>
                    <label>Descrizione</label>
                    <textarea
                      rows={3}
                      value={it.description}
                      onChange={(e) => patch(it.id, { description: e.target.value })}
                      onBlur={() => save(it)}
                      disabled={readOnly}
                    />
                    {it.validation.issues.length > 0 && (
                      <ul className="bo-issues">
                        {it.validation.issues.map((x, n) => (
                          <li key={n} className={x.severity}>{x.message}</li>
                        ))}
                      </ul>
                    )}
                  </>
                )}

                <div className="bo-actions">
                  {locked && (
                    <>
                      <button
                        className="btn small"
                        onClick={() => checkIn(it)}
                        disabled={working}
                        title="Archivia il file mantenendo il contenuto: torna scrivibile"
                      >
                        {working ? "…" : "Archivia"}
                      </button>
                      <button
                        className="btn small ghost"
                        onClick={() => checkIn(it, true)}
                        disabled={working}
                        title="Scarta la versione in sospeso e ripristina l'ultima archiviata"
                      >
                        Ignora estrazione
                      </button>
                    </>
                  )}
                  {library !== "ImagesSent" && !readOnly && (
                    <button
                      className="btn small ghost"
                      onClick={() => regenerate(it)}
                      disabled={working || bulk}
                      title="Riscrive titolo, keyword e descrizione con il prompt corrente"
                    >
                      {working ? "…" : "Rigenera metadati"}
                    </button>
                  )}
                  {library === "ImagesToSend" && !readOnly && (
                    <button className="btn small" onClick={() => send(it)} disabled={working || it.invia}>
                      {it.invia ? "In coda" : working ? "…" : "Invia ai marketplace"}
                    </button>
                  )}
                  {NEXT_STAGE[library] && !readOnly && (
                    <button className="btn small ghost" onClick={() => move(it)} disabled={working}>
                      → {NEXT_STAGE[library] === "ImagesToSend" ? "Pronti per l'invio" : "Pubblicati"}
                    </button>
                  )}
                  <button className="btn small ghost" onClick={() => openOriginal(it)}>
                    Originale
                  </button>
                  <button className="btn small danger" onClick={() => remove(it)} disabled={working}>
                    Elimina
                  </button>
                </div>
              </div>
            </article>
          );
        })}
      </div>

      {(pageNumber > 1 || nextToken) && (
        <nav className="bo-pager" aria-label="Paginazione">
          <button
            className="btn small ghost"
            onClick={() => resetPaging()}
            disabled={pageNumber === 1 || loading}
            title="Torna alla prima pagina"
          >
            « Prima
          </button>
          <button
            className="btn small"
            onClick={() => setTrail((t) => (t.length > 1 ? t.slice(0, -1) : t))}
            disabled={pageNumber === 1 || loading}
          >
            ‹ Indietro
          </button>

          <span className="bo-pageinfo" aria-live="polite">
            {loading ? "Carico…" : (
              <>
                <strong>Pagina {pageNumber}</strong>
                {items.length > 0 && (
                  <> · elementi {first.toLocaleString("it-IT")}–{last.toLocaleString("it-IT")}</>
                )}
                {total != null && <> di circa {total.toLocaleString("it-IT")}</>}
              </>
            )}
          </span>

          <button
            className="btn small"
            onClick={() => nextToken && setTrail((t) => [...t, nextToken])}
            disabled={!nextToken || loading}
          >
            Avanti ›
          </button>
        </nav>
      )}
    </div>
  );
}
