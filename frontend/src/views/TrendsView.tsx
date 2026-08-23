import { useEffect, useRef, useState } from "react";
import { api, type Trends, type TopicSuggestion, type LiveTrends, type Predictions,
         type StockRelevance, type Themes, type ThemeOpportunity } from "../api";

type Sub = "themes" | "live" | "predicted" | "seasonal";

const scoreClass = (n: number) => (n >= 75 ? "good" : n >= 50 ? "mid" : "bad");

const MONTHS = ["", "gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno",
                "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre"];

const TIMING: Record<string, { label: string; cls: string }> = {
  now:       { label: "▶ Crea ora",     cls: "t-now" },
  soon:      { label: "◔ Preparati",    cls: "t-soon" },
  late:      { label: "◕ In ritardo",   cls: "t-late" },
  plan:      { label: "◷ In calendario", cls: "t-plan" },
  evergreen: { label: "∞ Sempreverde",  cls: "t-ever" },
  nodata:    { label: "· Dato assente", cls: "t-nodata" },
};

const PROMPT_STYLES: Record<string, string> = {
  silhouette: "Silhouette B/N",
  flat: "Vettoriale piatto",
  lineart: "Line art",
};

/** Tiny inline sparkline for the 15-day trajectory. */
function Spark({ series }: { series: number[] }) {
  if (!series || series.length < 2) return null;
  const max = Math.max(...series), min = Math.min(...series);
  const range = max - min || 1;
  const pts = series
    .map((v, i) => `${(i / (series.length - 1)) * 100},${28 - ((v - min) / range) * 26}`)
    .join(" ");
  return (
    <svg className="spark" viewBox="0 0 100 28" preserveAspectRatio="none">
      <polyline points={pts} fill="none" strokeWidth="2" vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

function TrajectoryChip({ t, ratio }: { t: string; ratio?: number | null }) {
  const label =
    t === "rising" ? "in crescita" : t === "peaking" ? "al picco" : t === "fading" ? "in calo" : "n/d";
  return <span className={`traj ${t}`}>{label}{ratio ? ` ${ratio.toFixed(2)}x` : ""}</span>;
}

const VERDICTS: Record<string, { label: string; cls: string }> = {
  ok: { label: "✔ Usabile", cls: "v-ok" },
  adapt: { label: "◑ Da adattare", cls: "v-adapt" },
  reject: { label: "✕ Non usabile", cls: "v-reject" },
  unknown: { label: "? Da valutare", cls: "v-unknown" },
};

/** One prompt line with a copy button — the bridge from a trend to the image generator. */
function PromptRow({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);
  const copy = () => {
    navigator.clipboard?.writeText(text).then(
      () => { setCopied(true); setTimeout(() => setCopied(false), 1600); },
      () => {}
    );
  };
  return (
    <li className="prompt-row">
      <code>{text}</code>
      <button className="btn tiny" onClick={copy} title="Copia negli appunti">
        {copied ? "✓ copiato" : "copia"}
      </button>
    </li>
  );
}

/** Verdict + drawable concepts + ready-to-paste prompts. */
function RelevanceBlock({ r }: { r: StockRelevance }) {
  const v = VERDICTS[r.verdict] ?? VERDICTS.unknown;
  return (
    <div className={`relevance ${v.cls}`}>
      <div className="rel-head">
        <span className={`vbadge ${v.cls}`}>{v.label}</span>
        {r.theme && <span className="rel-theme">tema: {r.theme}</span>}
      </div>
      <div className="muted small">{r.reason}</div>
      {r.concepts.length > 0 && (
        <div className="topic-kw">
          {r.concepts.slice(0, 5).map((c) => <span key={c} className="tkw">{c}</span>)}
        </div>
      )}
      {r.prompts.length > 0 && (
        <details className="evidence prompts">
          <summary>Prompt pronti per il generatore ({r.prompts.length})</summary>
          <ul>{r.prompts.map((p) => <PromptRow key={p} text={p} />)}</ul>
        </details>
      )}
    </div>
  );
}

/** Human-friendly "how long ago" for a cached answer. */
function ago(iso: string): string {
  const mins = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  if (mins < 1) return "appena generato";
  if (mins < 60) return `generato ${mins} min fa`;
  const h = Math.round(mins / 60);
  return h < 24 ? `generato ${h} ${h === 1 ? "ora" : "ore"} fa` : `generato ${Math.round(h / 24)} gg fa`;
}

/** Says which engine produced an answer: the agent, or the built-in fallback vocabulary. */
function EngineBadge({ engine, label }: { engine: string; label?: string }) {
  const agentic = engine === "agentic";
  return (
    <span className={`ebadge ${agentic ? "e-ai" : "e-rules"}`} title={label}>
      {agentic ? "✨ agente AI" : "⚙ regole predefinite"}
    </span>
  );
}

/** A theme card: measured demand, timing and the prompts to start from. */
function ThemeCard({ t }: { t: ThemeOpportunity }) {
  const tm = TIMING[t.timing] ?? TIMING.plan;
  return (
    <div className={`topic theme-card ${tm.cls}`}>
      <div className={`topic-score ${scoreClass(t.opportunityScore)}`}>{t.opportunityScore}</div>
      <div className="topic-main">
        <div className="topic-head">
          <strong>{t.name}</strong>
          <span className={`tbadge ${tm.cls}`}>{tm.label}</span>
          <span className="topic-cat">{t.category}</span>
          {t.inSubmissionWindow && <span className="topic-window">finestra ottimale</span>}
        </div>

        <div className="muted small">{t.advice}</div>

        {t.hotNow.length > 0 && (
          <div className="hotnow">🔥 di tendenza ora: {t.hotNow.join(" · ")}</div>
        )}

        <div className="theme-metrics">
          {t.averageViews > 0 && (
            <span title="Visite medie mensili della voce Wikipedia: misura l'interesse reale del pubblico">
              👁 {t.averageViews.toLocaleString()}/mese
            </span>
          )}
          {t.seasonalityRatio != null && t.seasonalityRatio >= 1.6 && (
            <span title="Quanto il tema si impenna nel mese di picco rispetto alla media">
              📈 picco {t.seasonalityRatio.toFixed(1)}×
            </span>
          )}
          {t.peakMonth ? (
            <span title="Mese in cui l'interesse raggiunge il massimo">
              🗓 {MONTHS[t.peakMonth]}{t.daysToPeak != null ? ` · fra ${t.daysToPeak} gg` : ""}
            </span>
          ) : null}
          {t.trajectory !== "unknown" && (
            <TrajectoryChip t={t.trajectory} ratio={t.trendRatio} />
          )}
        </div>

        {t.series.length > 1 && (
          <div className={`sparkwrap ${t.trajectory}`}>
            <Spark series={t.series} /><span className="muted small">ultimi 15 giorni</span>
          </div>
        )}

        <div className="topic-kw">
          {t.concepts.slice(0, 6).map((c) => <span key={c} className="tkw">{c}</span>)}
        </div>

        <details className="evidence prompts">
          <summary>Prompt pronti per il generatore ({t.prompts.length})</summary>
          <ul>{t.prompts.map((p) => <PromptRow key={p} text={p} />)}</ul>
        </details>

        <div className="topic-actions">
          <a className="btn small" href={t.adobeSearchUrl} target="_blank" rel="noreferrer">Adobe ↗</a>
          <a className="btn small" href={t.freepikSearchUrl} target="_blank" rel="noreferrer">Freepik ↗</a>
        </div>
      </div>
    </div>
  );
}

export default function TrendsView() {
  const [sub, setSub] = useState<Sub>("themes");
  const [style, setStyle] = useState("silhouette");
  const [geo, setGeo] = useState("US");
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const loadSequence = useRef(0);
  const [onlyUsable, setOnlyUsable] = useState(true);
  const [promptStyle, setPromptStyle] = useState("silhouette");

  const [live, setLive] = useState<LiveTrends | null>(null);
  const [pred, setPred] = useState<Predictions | null>(null);
  const [seasonal, setSeasonal] = useState<Trends | null>(null);
  const [themes, setThemes] = useState<Themes | null>(null);
  const [cat, setCat] = useState("");
  const [sat, setSat] = useState<Record<string, string>>({});

  const [idea, setIdea] = useState("");
  const [ideaResult, setIdeaResult] = useState<StockRelevance | null>(null);
  const [ideaEngine, setIdeaEngine] = useState<string | null>(null);
  const [ideaBusy, setIdeaBusy] = useState(false);

  const load = (which: Sub = sub, refresh = false, clear = false) => {
    const sequence = ++loadSequence.current;
    setLoading(true);
    setLoadError(null);
    if (clear) {
      if (which === "themes") setThemes(null);
      else if (which === "live") setLive(null);
      else if (which === "predicted") setPred(null);
      else setSeasonal(null);
    }

    const current = () => sequence === loadSequence.current;
    const p: Promise<void> =
      which === "themes"
        ? api.themeOpportunities(promptStyle, cat || undefined, geo, 12, refresh)
            .then((data) => { if (current()) setThemes(data); })
      : which === "live"
        ? api.liveTrends(geo, 12, style, onlyUsable, promptStyle)
            .then((data) => { if (current()) setLive(data); })
      : which === "predicted"
        ? api.predictedTrends(2, promptStyle)
            .then((data) => { if (current()) setPred(data); })
        : api.trends(200, style)
            .then((data) => { if (current()) setSeasonal(data); });

    p.catch((e) => {
        if (current()) setLoadError((e as Error).message);
      })
      .finally(() => {
        if (current()) setLoading(false);
      });
  };

  // Every tab owns a different set of filters. Reload automatically when one of its effective
  // inputs changes, so switching country/style never shows a stale result that needs a second click.
  const requestKey =
    sub === "themes" ? `${sub}|${promptStyle}|${cat}|${geo}`
    : sub === "live" ? `${sub}|${geo}|${style}|${onlyUsable}|${promptStyle}`
    : sub === "predicted" ? `${sub}|${promptStyle}`
    : `${sub}|${style}`;

  useEffect(() => {
    const timer = window.setTimeout(() => load(sub, false, true), 250);
    return () => window.clearTimeout(timer);
    // requestKey contains exactly the effective inputs for the active tab.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [requestKey]);

  const go = (s: Sub) => {
    setSub(s);
  };

  const checkIdea = () => {
    if (!idea.trim()) return;
    setIdeaBusy(true);
    api.checkTopic(idea.trim(), promptStyle)
      .then((r) => { setIdeaResult(r.relevance); setIdeaEngine(r.engine ?? null); })
      .catch(() => { setIdeaResult(null); setIdeaEngine(null); })
      .finally(() => setIdeaBusy(false));
  };

  const seasonalScore = (s: TopicSuggestion) => {
    const raw = sat[s.name];
    if (!raw) return s.opportunityScore;
    const n = parseInt(raw.replace(/\D/g, ""), 10);
    if (!n || n <= 0) return s.opportunityScore;
    return Math.max(0, s.opportunityScore - Math.min(60, Math.round(Math.log10(n) * 12)));
  };

  return (
    <div className="trends">
      <div className="trends-hero">
        <h2>Opportunità di mercato</h2>
        <p>
          L'analisi parte dai <strong>temi che puoi davvero vendere</strong> — non dai trend del momento,
          che sono in gran parte persone, marchi e cronaca. Per ognuno misuro la domanda reale, la
          stagionalità e la traiettoria, così sai <em>cosa</em> creare e <em>quando</em>.
        </p>
        <div className="trends-controls">
          <label>Stile<input value={style} onChange={(e) => setStyle(e.target.value)} placeholder="silhouette" /></label>
          <label>Prompt
            <select value={promptStyle} onChange={(e) => setPromptStyle(e.target.value)}>
              {Object.entries(PROMPT_STYLES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select>
          </label>
          {sub === "themes" && (
            <label>Categoria
              <select value={cat} onChange={(e) => setCat(e.target.value)}>
                <option value="">Tutte</option>
                {themes?.categories.map((c) => <option key={c} value={c}>{c}</option>)}
              </select>
            </label>
          )}
          {(sub === "live" || sub === "themes") && (
            <label>Paese
              <select value={geo} onChange={(e) => setGeo(e.target.value)}>
                <option value="US">Stati Uniti</option>
                <option value="IT">Italia</option>
                <option value="GB">Regno Unito</option>
                <option value="DE">Germania</option>
                <option value="FR">Francia</option>
                <option value="ES">Spagna</option>
              </select>
            </label>
          )}
          {sub === "live" && (
            <label className="chk">
              <input type="checkbox" checked={onlyUsable} onChange={(e) => setOnlyUsable(e.target.checked)} />
              Solo temi utilizzabili
            </label>
          )}
          <button className="btn" onClick={() => load()} disabled={loading}>{loading ? "…" : "Aggiorna"}</button>
          {sub === "themes" && themes?.engine === "agentic" && (
            <button className="btn ghost" onClick={() => load("themes", true)} disabled={loading}
                    title="Chiede all'agente un'analisi nuova invece di riusare quella in cache">
              ↻ Rigenera
            </button>
          )}
        </div>

        <div className="idea-check">
          <input
            value={idea}
            placeholder="Hai un'idea? Scrivila e verifica se è vendibile (es. «gatto che corre», «Olimpiadi 2028»)"
            onChange={(e) => setIdea(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && checkIdea()}
          />
          <button className="btn" onClick={checkIdea} disabled={ideaBusy || !idea.trim()}>
            {ideaBusy ? "…" : "Valuta"}
          </button>
        </div>
        {ideaResult && (
          <>
            {ideaEngine && <EngineBadge engine={ideaEngine} />}
            <RelevanceBlock r={ideaResult} />
          </>
        )}
      </div>

      <div className="subtabs">
        <button className={`subtab ${sub === "themes" ? "on" : ""}`} onClick={() => go("themes")}>🎯 Cosa creare</button>
        <button className={`subtab ${sub === "live" ? "on" : ""}`} onClick={() => go("live")}>🔥 Trend attuali</button>
        <button className={`subtab ${sub === "predicted" ? "on" : ""}`} onClick={() => go("predicted")}>🔮 Previsti dalle notizie</button>
        <button className={`subtab ${sub === "seasonal" ? "on" : ""}`} onClick={() => go("seasonal")}>📅 Stagionali</button>
      </div>
      {loadError && <div className="notice err" role="alert">{loadError}</div>}
      {loading && (
        <div className="trend-loading" role="status">
          <span className="trend-spinner" aria-hidden="true" />
          {sub === "live"
            ? "Analizzo i trend attuali e la loro traiettoria…"
            : sub === "predicted"
              ? "Cerco segnali nelle notizie sugli eventi futuri…"
              : sub === "seasonal"
                ? "Calcolo finestre e domanda stagionale…"
                : "Misuro e ordino i temi vendibili…"}
        </div>
      )}

      {/* ---- THEMES: the generalized view ---- */}
      {sub === "themes" && (
        <>
          <div className="muted small src">
            {themes?.engine && <EngineBadge engine={themes.engine} label={themes.engineLabel} />}
            {themes?.fromCache && themes.generatedAt && (
              <span className="cachechip" title={`Le risposte dell'agente restano in cache ${themes.cacheHours ?? 12} ore. Usa Rigenera per una nuova analisi.`}>
                ⏱ {ago(themes.generatedAt)}
              </span>
            )}
            {" "}{themes?.source ?? "…"}
          </div>
          {themes?.warning && <div className="engine-warn">⚠ {themes.warning}</div>}
          {themes && (
            <div className="muted small src">
              {themes.count} temi · {themes.note}
            </div>
          )}
          <div className="topic-list">
            {themes?.themes.map((t) => <ThemeCard key={t.name} t={t} />)}
          </div>
        </>
      )}

      {/* ---- LIVE ---- */}
      {sub === "live" && (
        <>
          <div className="muted small src">{live?.source ?? "…"}{live?.filter ? ` · ${live.filter}` : ""}</div>
          {live?.trends.length === 0 && (
            <div className="empty">
              {onlyUsable
                ? "Oggi nessun trend di questo paese è utilizzabile per contenuti stock (sono per lo più persone, marchi e cronaca). Togli il filtro per vedere tutto, o passa a «Previsti» e «Stagionali», che sono sempre ricchi di temi disegnabili."
                : "Nessun trend disponibile per questo paese."}
            </div>
          )}
          <div className="topic-list">
            {live?.trends.map((t) => (
              <div key={t.title} className="topic">
                <div className={`topic-score ${scoreClass(t.opportunityScore)}`}>{t.opportunityScore}</div>
                <div className="topic-main">
                  <div className="topic-head">
                    <strong>{t.title}</strong>
                    <TrajectoryChip t={t.trajectory} ratio={t.trendRatio} />
                    <span className="topic-cat">{t.searchTraffic.toLocaleString()}+ ricerche</span>
                  </div>
                  <div className="muted small">{t.advice}</div>
                  {t.series.length > 1 && (
                    <div className={`sparkwrap ${t.trajectory}`}><Spark series={t.series} /><span className="muted small">ultimi 15 giorni</span></div>
                  )}
                  {t.relatedNews.length > 0 && (
                    <div className="muted small news">📰 {t.relatedNews[0]}</div>
                  )}
                  {t.relevance && <RelevanceBlock r={t.relevance} />}
                  <div className="topic-actions">
                    <a className="btn small" href={t.adobeSearchUrl} target="_blank" rel="noreferrer">Adobe ↗</a>
                    <a className="btn small" href={t.freepikSearchUrl} target="_blank" rel="noreferrer">Freepik ↗</a>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </>
      )}

      {/* ---- PREDICTED ---- */}
      {sub === "predicted" && (
        <>
          <div className="muted small src">{pred?.source ?? "…"}</div>
          <div className="topic-list">
            {pred?.predictions.map((p) => (
              <div key={p.topic} className="topic">
                <div className={`topic-score ${scoreClass(Math.round(p.confidence * 100))}`}>
                  {Math.round(p.confidence * 100)}
                </div>
                <div className="topic-main">
                  <div className="topic-head">
                    <strong>{p.topic}</strong>
                    <span className="topic-cat">{p.category}</span>
                    {p.targetYear && <span className="topic-window">attesa nel {p.targetYear}</span>}
                    <span className="muted small">{p.mentions} notizie</span>
                  </div>
                  <div className="topic-kw">
                    {p.keywords.slice(0, 6).map((k) => <span key={k} className="tkw">{k}</span>)}
                  </div>
                  <details className="evidence">
                    <summary>Notizie che lo suggeriscono</summary>
                    <ul>
                      {p.evidence.map((e) => (
                        <li key={e.link || `${e.title}-${e.published ?? ""}`}><a href={e.link} target="_blank" rel="noreferrer">{e.title}</a>
                          {e.source && <span className="muted small"> — {e.source}</span>}</li>
                      ))}
                    </ul>
                  </details>
                  {p.relevance && <RelevanceBlock r={p.relevance} />}
                  <div className="topic-actions">
                    <a className="btn small" target="_blank" rel="noreferrer"
                       href={`https://stock.adobe.com/search?k=${encodeURIComponent((p.keywords[0] ?? p.topic) + " " + style)}`}>Adobe ↗</a>
                    <a className="btn small" target="_blank" rel="noreferrer"
                       href={`https://www.freepik.com/search?query=${encodeURIComponent((p.keywords[0] ?? p.topic) + " " + style)}&type=vector`}>Freepik ↗</a>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </>
      )}

      {/* ---- SEASONAL ---- */}
      {sub === "seasonal" && (
        <>
          <div className="muted small src">{seasonal?.demandSource ?? "…"}</div>
          <div className="topic-list">
            {seasonal?.suggestions.map((s) => {
              const score = seasonalScore(s);
              return (
                <div key={s.name} className="topic">
                  <div className={`topic-score ${scoreClass(score)}`}>{score}</div>
                  <div className="topic-main">
                    <div className="topic-head">
                      <strong>{s.name}</strong>
                      <span className="topic-cat">{s.category}</span>
                      {s.inSubmissionWindow && <span className="topic-window">finestra ottimale</span>}
                      {s.demandSource === "live" && s.seasonalityRatio && (
                        <span className="topic-cat">picco {s.seasonalityRatio.toFixed(1)}x</span>
                      )}
                    </div>
                    <div className="muted small">{s.advice} · picco {s.peakDate} ({s.daysUntilPeak} gg)</div>
                    <div className="topic-kw">
                      {s.keywords.slice(0, 8).map((k) => <span key={k} className="tkw">{k}</span>)}
                    </div>
                    <div className="topic-actions">
                      <a className="btn small" href={s.adobeSearchUrl} target="_blank" rel="noreferrer">Adobe ↗</a>
                      <a className="btn small" href={s.freepikSearchUrl} target="_blank" rel="noreferrer">Freepik ↗</a>
                      <input className="sat-input" placeholder="n° risultati"
                        value={sat[s.name] ?? ""} onChange={(e) => setSat((m) => ({ ...m, [s.name]: e.target.value }))} />
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
}
