// Shared API client + types for the Stock Vector Studio frontend.

export type ItemFiles = { svg?: string; eps?: string; jpg?: string; ai?: string };
export type ItemSteps = {
  queuedAt?: string;
  startedAt?: string;
  vectorizedAt?: string;
  completedAt?: string;
  dispatchedAt?: string;
  publishedAt?: string;
};

export type Item = {
  id: string;
  originalFileName: string;
  baseName: string;
  status: string;
  error?: string;
  title: string;
  description: string;
  keywords: string[];
  category: string;
  files: ItemFiles;
  previewUrl?: string;
  steps: ItemSteps;
  durationMs?: number | null;
  publishStatus?: string | null;
  publishMessage?: string | null;
  metadataSource?: string | null;
  validation: Validation;
  mode?: string;
  /** Optional note in which the author explains why the generated metadata was corrected. */
  feedback?: string | null;
  /** True when the saved metadata differs from what the generator produced. */
  editedFromAi?: boolean;
};

export type Job = { id: string; createdAt: string; mode?: string; items: Item[] };

/**
 * What comes back when pictures are handed to the durable pipeline.
 *
 * Deliberately thin: the handoff creates no job and tracks no state, because from the moment the
 * message is on the queue the work belongs to the Function. All the API can honestly report is how
 * many pictures it managed to deposit.
 */
export type HandoffResponse = {
  ok: boolean;
  accepted: number;
  rejected: number;
  items: { file: string; blob: string; threshold?: number | null }[];
  errors: { file: string; error: string }[];
  message: string;
};

/** Rules the review process distilled from the author's corrections. */
export type Guidance = {
  version: number;
  text: string;
  updatedAt?: string | null;
  basedOnEntries: number;
  engine: string;
  totalFeedback: number;
  pendingFeedback: number;
  agenticAvailable: boolean;
  /** Keywords stripped in code after generation, not merely discouraged in the prompt. */
  bannedKeywords: string[];
};

export type RebuildResult = {
  guidance: Guidance;
  engine: string;
  entriesUsed: number;
  warning?: string | null;
};

export type MetadataSnapshot = {
  title: string;
  description: string;
  keywords: string[];
  category: string;
};

export type FeedbackEntry = {
  id: string;
  at: string;
  jobId: string;
  itemId: string;
  baseName: string;
  note?: string | null;
  generated?: MetadataSnapshot | null;
  corrected: MetadataSnapshot;
  keywordsAdded: string[];
  keywordsRemoved: string[];
  titleChanged: boolean;
  categoryChanged: boolean;
};

/** One file of a delivered set. `url` streams it through the API, so SVG can be shown for real. */
export type Deliverable = {
  id: number;
  fileName: string;
  kind: string;
  carrier: boolean;
  url?: string;
  /** Quando il file è entrato in libreria. */
  created?: string;
  /** L'ultima riscrittura: su un vettoriale è la data del tracciato. */
  modified?: string;
  /** Rifatto dopo il raster che l'accompagna, cioè ritracciato. */
  rifatto?: boolean;
};

/**
 * Dove si trova l'immagine lungo la pipeline.
 *
 * Lo decide il server: qui si mostra e basta. Dedurlo da invia/inviato/stato significherebbe
 * riscrivere quelle regole in ogni schermata, e vederle divergere alla prima modifica.
 */
export type StatoPipeline = {
  stato: "revisione" | "pronto" | "in-attesa" | "in-consegna" | "pubblicato" | "errore";
  etichetta: string;
  spiega: string;
  /** Se in questo stato ha senso chiedere un invio. Deciso dal server, non dedotto qui. */
  puoInviare: boolean;
  /** Se ha senso forzare la partenza immediata. Vale anche da «in attesa», mai da «in pubblicazione». */
  puoForzare: boolean;
  /**
   * Se il file è rimasto fermo in «in pubblicazione» oltre ogni attesa legittima e va rimesso in
   * gioco a mano. Lo decide il server, che conosce la data di modifica.
   */
  puoSbloccare: boolean;
};

/** Esito dell'importazione dell'esportazione Adobe. */
export type SalesImport = {
  ok: boolean;
  error?: string;
  lette?: number;
  importate?: number;
  giaPresenti?: number;
  scartate?: number;
  dal?: string;
  al?: string;
  totaleFile?: number;
  inArchivio?: number;
};

export type SalesGroup = { nome: string; vendite: number; ricavi: number; perDownload: number };

export type SalesSummary = {
  ok: boolean;
  vuoto: boolean;
  inArchivio: number;
  dal?: string;
  al?: string;
  vendite?: number;
  ricavi?: number;
  perDownload?: number;
  fileDistinti?: number;
  fileMetaRicavi?: number;
  perTipo?: SalesGroup[];
  perLicenza?: SalesGroup[];
  perSerie?: SalesGroup[];
  perMese?: { mese: string; vendite: number; ricavi: number }[];
  migliori?: { file: string; titolo: string; tipo: string; vendite: number; ricavi: number }[];
};

export type TuneKeywords = {
  ok: boolean; error?: string; library?: string;
  esaminati?: number; daCambiare?: number; sogliaPercento?: number;
  diffuse?: { parola: string; file: number; quota: number }[];
  righe?: { id: number; file: string; titolo: string; prima: string[]; dopo: string[]; cambia: boolean }[];
};

export type TuneOverlap = {
  ok: boolean; error?: string; sogliaPercento?: number;
  serieTrovate?: number; serieOltreSoglia?: number;
  serie?: { serie: string; file: number; keywordMedie: number; condivise: number;
            percentuale: number; oltreSoglia: boolean; esempi: string[] }[];
};

export type TuneMute = {
  ok: boolean; error?: string;
  esaminati?: number; conStorico?: number; senzaStorico?: number;
  righe?: { id: number; file: string; titolo: string; serie: string; venditeSerie: number; ricaviSerie: number }[];
};

export type Strategia = {
  ok: boolean;
  vuoto?: boolean;
  aggiornato?: string;
  archivio?: {
    vendite: number; ricavi: number; dal: string; al: string;
    fileVenduti: number; fileMetaRicavi: number;
  };
  settimana?: { vendite: number; ricavi: number; perTipo: { tipo: string; vendite: number; ricavi: number }[] };
  mese?: { vendite: number; ricavi: number };
  finestra?: {
    mesi: number;
    rapporto?: {
      caricati: number; venditeSeiMesi: number; venditeSettimana: number;
      perFileCaricato: number; perFileCaricatoSettimana: number;
    } | null;
  };
  tendenza?: {
    recente: { vendite: number; ricavi: number; perDownload: number };
    precedente: { vendite: number; ricavi: number; perDownload: number };
    variazioneVendite: number | null;
    variazioneRicavi: number | null;
  };
  perTipoAnno?: { tipo: string; vendite: number; ricavi: number; perDownload: number }[];
};

export type SalesDna = {
  ok: boolean;
  error?: string;
  totaleFile?: number;
  vitali?: number;
  quotaRicaviVitali?: number;
  ricaviTotali?: number;
  venditeMediaVitali?: number;
  venditeMediaResto?: number;
  perDownloadVitali?: number;
  perDownloadResto?: number;
  tipiVitali?: { tipo: string; file: number }[];
  parole?: { parola: string; neiPochi: number; altrove: number; rapporto: number }[];
  elenco?: { file: string; titolo: string; tipo: string; vendite: number; ricavi: number; quotaCustom: number }[];
};

export type SalesWarehouse = {
  ok: boolean;
  error?: string;
  library?: string;
  esaminati?: number;
  conVendite?: number;
  senzaVendite?: number;
  righe?: { file: string; titolo: string; vendite: number; ricavi: number }[];
};

/** A file sitting in one of the SharePoint pipeline stages. */
export type BackofficeItem = {
  id: number;
  fileName: string;
  title: string;
  description: string;
  keywords: string[];
  stato: string;
  invia: boolean;
  inviato: boolean;
  /** Who holds the file checked out in SharePoint. Empty when nobody does. */
  checkedOutBy?: string;
  modified: string;
  created?: string;
  previewUrl: string;
  /** Dove si trova lungo la pipeline, risolto dal server. */
  pipeline?: StatoPipeline;
  /** L'originale su SharePoint. Il browser lo apre con la sessione di chi guarda. */
  fileUrl?: string;
  /**
   * The other files of the same image, when it was delivered as a set (SVG + EPS + JPEG).
   * Absent for a lone file, which is how images arrived before the durable path.
   */
  deliverables?: Deliverable[] | null;
  validation: {
    score: number;
    blocksDispatch: boolean;
    issues: { severity: string; field: string; message: string }[];
  };
};

export type BackofficePage = {
  ok: boolean;
  error?: string;
  library: string;
  label: string;
  items: BackofficeItem[];
  nextPageToken?: string | null;
};

export type BackofficeMutation = {
  ok: boolean;
  error?: string;
  blocked?: boolean;
  issues?: string[];
  item?: BackofficeItem;
};

/** Outcome of releasing a file left checked out in SharePoint. */
export type CheckInResult = {
  ok: boolean;
  changed?: boolean;
  message?: string;
  error?: string;
  item?: BackofficeItem;
};

/** Level of the API's own App Service plan and how much of the free CPU quota today is gone. */
/**
 * Lo stato del deposito dei punteggi in libreria.
 *
 * Serve a rendere visibile un lavoro che per costruzione non si vede: il punteggio viene scritto
 * in sottofondo, apposta per non far aspettare chi guarda la galleria. Senza un posto dove
 * mostrarlo, l'unico modo di sapere se sta lavorando è sperarlo.
 */
export type PunteggioLibreria = {
  libreria: string;
  completa: boolean;
  visitati: number;
  depositati: number;
  totale: number;
  cursore: string | null;
  pagine: number;
  /** Motivo per cui lo scorrimento si è fermato, nullo quando procede. */
  interrotta: string | null;
  inCorso: boolean;
};

export type PunteggioStato = {
  ok: boolean;
  coda: {
    scritti: number;
    scartati: number;
    inCoda: number;
    riempimento: {
      depositati: number;
      visitati: number;
      complete: string[];
      lavoro: string;
      da: string;
      avvio: string;
      librerie: PunteggioLibreria[];
    };
    ultimoErrore: string | null;
  };
  lotti: {
    riusciti: number;
    falliti: number;
    rifiutiPerCarico: number;
    ultimoErrore: string | null;
  };
  colonna: { library: string; completa: boolean } | null;
};

/** Il cruscotto: ogni cifra risponde a una domanda, altrimenti è decorazione. */
export type Insights = {
  ok: boolean;
  error?: string;
  library: string;
  aggiornatoAl: string;
  andamento: Record<"ultimi30" | "ultimi90" | "ultimi365", {
    giorni: number; vendite: number; ricavi: number;
    venditePrima: number; ricaviPrima: number; variazione: number | null;
  }>;
  efficienza: {
    fileInPortfolio: number; fileCheVendono: number; percentualeCheVende: number;
    ricavoPerFileProdotto: number; ricavoPerFileCheVende: number;
    ricaviTotali: number; venditeTotali: number;
  };
  copertura: {
    misurata: boolean; esatta: boolean; scorsaIl?: string | null;
    serieRiconosciute: string[];
    venditeGestite: number; ricaviGestiti: number;
    venditeFuori: number; ricaviFuori: number; fileFuori: number;
    percentualeRicaviGestiti: number;
  };
  concentrazione: {
    fileCheFannoMetaRicavi: number; percentualeDelPortfolio: number;
    migliore: { file: string; vendite: number; ricavi: number }[];
  };
  mercato: {
    perTipo: { tipo: string; vendite: number; ricavi: number; perDownload: number }[];
    perLicenza: { licenza: string; vendite: number; ricavi: number }[];
  };
  stagionalita: {
    mesi: { mese: number; nome: string; vendite: number; ricavi: number }[];
    mediaMensile: number; migliori: string[]; peggiori: string[];
  };
  metadatiFannoVendere: {
    esaminati: number; venduti: number; fermi: number;
    punteggioMedianoVenduti: number; punteggioMedianoFermi: number; differenza: number;
    attendibile: boolean; soglia: number; collisioni: number;
    mediaVenduti: number; mediaFermi: number;
    sopra90Venduti: number; sopra90Fermi: number;
  };
  pazienza: {
    misurate: number; giorniMedianiAllaPrimaVendita: number;
    entroUnMese: number; oltreSeiMesi: number;
  };
  spenti: { file: string; vendite: number; ricavi: number; fermoDa: number }[];
};

/** Il quadro d'insieme del portfolio già pubblicato. */
export type BonificaQuadro = {
  ok: boolean;
  error?: string;
  totale: number;
  cheVendono: number;
  cheNonVendono: number;
  percentualeCheVende: number;
  ricaviTotali: number;
  conMetadatiDeboli: number;
  sogliaMetadatiDeboli: number;
  fasce: { fascia: string; quanti: number; deboli: boolean }[];
};

/** Una riga della coda di bonifica: cosa fare di questa immagine, e perché. */
export type BonificaRiga = {
  id: number;
  fileName: string;
  title: string;
  keywords: string[];
  punteggio: number;
  vendite: number;
  ricavi: number;
  giorniOnline: number | null;
  consiglio: string;
  perche: string;
  problemi: { severity: string; field: string; message: string }[];
  previewUrl: string;
  fileUrl: string;
};

export type PlanState = {
  ok: boolean;
  configured: boolean;
  sku?: string | null;
  tier?: string | null;
  alwaysOn: boolean;
  cpuUsedSeconds: number;
  cpuQuotaSeconds: number;
  cpuPercent?: number | null;
  /** True when Azure does not expose the allowance and the documented F1 quota is assumed. */
  quotaAssumed: boolean;
  autoScaleUp: boolean;
  upSku: string;
  thresholdPercent: number;
  error?: string | null;
};

/** Outcome of re-describing one file with the current metadata prompt. */
export type RegenerateResult = {
  ok: boolean;
  id: number;
  fileName: string;
  error?: string | null;
  provider?: string | null;
  previousTitle?: string | null;
  previousTitleLength: number;
  previousKeywords: number;
  item?: BackofficeItem | null;
};

/** Una consegna vettoriale riscritta, col peso prima e dopo. */
export type ConsegnaRiscritta = {
  tipo: string;
  fileName: string;
  kbPrima: number;
  kbDopo: number;
};

/** Esito del ritracciamento di SVG ed EPS di un'immagine già in libreria. */
export type RivettorializzaResult = {
  ok: boolean;
  id: number;
  fileName: string;
  error?: string | null;
  consegne?: ConsegnaRiscritta[] | null;
  aColori: boolean;
  /** L'immagine è già su Adobe: il file nuovo va ricaricato là a mano. */
  daRiportare: boolean;
};

export type JobSummary = {
  id: string;
  createdAt: string;
  total: number;
  queued: number;
  processing: number;
  completed: number;
  failed: number;
  dispatched: number;
  published: number;
};

export type PipelineStatus = {
  enabled: boolean;
  canEnqueue: boolean;
  siteUrl?: string;
  libraryFolder?: string;
  trigger?: string;
  dispatchFile?: string;
};

export type Health = {
  backend: boolean;
  jobStore: { ok: boolean; backend: string; error?: string | null };
  vectorizer: { engine: string; potracePresent: boolean };
  pipeline: {
    enabled: boolean;
    trigger: string;
    dispatchFile: string;
    libraryFolder?: string;
    siteUrl?: string;
    callbackProtected?: boolean;
  };
  timestamp: string;
};

export type ConfigurationItem = {
  label: string;
  value: string;
  configKey: string;
};

export type ConfigurationGroup = {
  key: string;
  title: string;
  summary: string;
  state: "ok" | "warning" | "off";
  items: ConfigurationItem[];
};

export type ConfigurationChoice = {
  value: string;
  label: string;
  description: string;
  active: boolean;
  requirements: string;
};

export type ConfigurationArea = {
  title: string;
  /** Explains cases where "nothing in use" is the correct state rather than a fault. */
  note?: string | null;
  choices: ConfigurationChoice[];
};

export type ConfigurationSummary = {
  generatedAt: string;
  environment: string;
  safetyNote: string;
  groups: ConfigurationGroup[];
  possibilities: ConfigurationArea[];
};

export type Overview = {
  totals: {
    jobs: number;
    items: number;
    queued: number;
    processing: number;
    completed: number;
    failed: number;
    dispatched: number;
  };
  today: { items: number; completed: number; failed: number };
  week: { items: number; completed: number; failed: number };
  successRate: number | null;
  avgDurationMs: number | null;
  scanned: number;
};

export type FunnelStage = { key: string; label: string; count: number };
export type Funnel = { ok: boolean; stages?: FunnelStage[]; error?: string; timestamp?: string };

export type QueueDepth = { name: string; count: number | null; error?: string | null };
export type Queues = { configured: boolean; queues: QueueDepth[] };

export type TrackStage = { stage: string; label: string; found: boolean; modified?: string | null; error?: string };
export type TrackResult = { ok: boolean; name?: string; stages?: TrackStage[]; error?: string };

async function throwResponseError(res: Response): Promise<never> {
  // App Service authentication normally answers an expired session with its redirect page rather
  // than a 401, but a stale token on an XHR can still land here: either way the cure is a sign-in.
  if (res.status === 401 || res.status === 403) redirectToLogin();
  if (!res.ok) {
    let detail = "";
    try {
      detail = (await res.text()).slice(0, 2000);
    } catch {
      detail = "risposta di errore non leggibile";
    }
    throw new Error(`${res.status}${detail ? `: ${detail}` : ""}`);
  }
  throw new Error("Errore HTTP inatteso.");
}

async function jsonOrThrow(res: Response) {
  if (!res.ok) await throwResponseError(res);
  return res.json();
}

/**
 * Where the API lives. Empty when the API is served from the same origin; set at build time via
 * VITE_API_BASE when the API runs on its own App Service, so the two can scale independently.
 */
export const API_BASE = (import.meta.env.VITE_API_BASE ?? "").replace(/\/$/, "");

/** Resolves an app-relative API path against the configured API origin. */
export function apiUrl(path: string): string {
  if (/^https?:\/\//i.test(path)) return path;
  return API_BASE + path;
}

/**
 * Sends the browser through the App Service sign-in and back to where it was.
 *
 * There is no login form of our own to show: authentication happens in front of the application,
 * at the platform, so the only thing the code can do about a missing session is step aside.
 */
function redirectToLogin(): never {
  const back = encodeURIComponent(location.pathname + location.search + location.hash);
  location.assign(`/.auth/login/aad?post_login_redirect_uri=${back}`);
  throw new Error("Sessione scaduta: ti riporto all'accesso.");
}

/**
 * fetch wrapper with a timeout, a cancellable signal, and one guard.
 *
 * The guard is for expired sessions, which never arrive as a 401. App Service authentication
 * answers them with a redirect to the sign-in page, and a redirect is the one thing a fetch must
 * not follow: the browser would chase it to login.microsoftonline.com, be stopped by CORS, and the
 * view would report "Failed to fetch" for what is really an expired session. So redirects are kept
 * manual and read as the sign-out they are. The HTML check behind it covers the variant where the
 * platform answers 200 with its "redirecting to login" page instead of a 302.
 */
async function f(url: string, init: RequestInit = {}): Promise<Response> {
  const controller = new AbortController();
  const timeout = window.setTimeout(() => controller.abort(), 5 * 60 * 1000);

  // A caller-supplied signal must be honoured alongside the timeout: leaving a view has to be able
  // to cancel the requests it started, otherwise abandoned pages keep loading in the background.
  const external = init.signal;
  if (external) {
    if (external.aborted) controller.abort();
    else external.addEventListener("abort", () => controller.abort(), { once: true });
  }

  try {
    const res = await fetch(apiUrl(url), { ...init, signal: controller.signal, redirect: "manual" });
    // Solo un redirect vero, o la pagina interstiziale della piattaforma, valgono come sessione
    // scaduta. Un controllo piu' largo -- per esempio "status 0" -- rischierebbe di mandare al
    // login per una risposta anomala qualsiasi, e un rimbalzo al login e' l'errore piu' fastidioso
    // che si possa infliggere: riparte, torna, riparte.
    if (res.type === "opaqueredirect") redirectToLogin();
    if ((res.headers.get("content-type") ?? "").includes("text/html")) redirectToLogin();
    return res;
  } finally {
    window.clearTimeout(timeout);
  }
}

/** Downloads a file through the same session as the rest of the app, then hands it to the browser. */
export async function downloadFile(url: string, fallbackName: string): Promise<void> {
  const response = await f(url);
  if (!response.ok) await throwResponseError(response);

  const blob = await response.blob();
  const disposition = response.headers.get("Content-Disposition") ?? "";
  const utf8Name = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
  const plainName = disposition.match(/filename="?([^";]+)"?/i)?.[1];
  const filename = utf8Name
    ? decodeURIComponent(utf8Name)
    : plainName ?? fallbackName;

  const objectUrl = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement("a");
    anchor.href = objectUrl;
    anchor.download = filename;
    anchor.style.display = "none";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    window.setTimeout(() => URL.revokeObjectURL(objectUrl), 1000);
  }
}

export const api = {
  createJob(files: File[], mode: "vector" | "raster" = "vector"): Promise<Job> {
    const fd = new FormData();
    files.forEach((f) => fd.append("files", f, f.name));
    fd.append("mode", mode);
    return f("/api/jobs", { method: "POST", body: fd }).then(jsonOrThrow);
  },
  /**
   * Hands the pictures to the durable pipeline and returns as soon as they are deposited.
   *
   * Unlike createJob, nothing is processed while this call is open: the API stores each original
   * and posts one queue message, then it is out of the way. That is the point — the batch keeps
   * going through a deploy, a plan change or the site being switched off.
   *
   * `thresholds` carries the tracing cut the author picked per picture, one entry per file in the
   * same order; "auto" leaves that picture to Otsu inside the Function.
   */
  handoff(files: File[], mode: "vector" | "colore" | "raster" = "vector",
          thresholds?: (number | null)[], colori?: number, unione?: number): Promise<HandoffResponse> {
    const fd = new FormData();
    files.forEach((file) => fd.append("files", file, file.name));
    fd.append("mode", mode);
    if (colori != null) fd.append("colori", String(colori));
    // Zero è una scelta legittima ("non unire niente"), quindi si confronta con null e non con
    // falsy: `if (unione)` scarterebbe proprio il caso che serve per diagnosticare un difetto.
    if (unione != null) fd.append("unione", String(unione));
    // Appended after the files and in the same order: the server pairs the two lists by position,
    // which is the only pairing that survives two uploads sharing a file name.
    files.forEach((_, i) => {
      const t = thresholds?.[i];
      fd.append("thresholds", t == null ? "auto" : String(Math.round(t)));
    });
    return f("/api/jobs/handoff", { method: "POST", body: fd }).then(jsonOrThrow);
  },
  getJob(id: string): Promise<Job> {
    return f(`/api/jobs/${id}`).then(jsonOrThrow);
  },
  listJobs(limit = 50): Promise<JobSummary[]> {
    return f(`/api/jobs?limit=${limit}`).then(jsonOrThrow);
  },
  updateItem(jobId: string, item: Item, feedback?: string | null): Promise<Item> {
    return f(`/api/jobs/${jobId}/items/${item.id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        title: item.title,
        description: item.description,
        keywords: item.keywords,
        category: item.category,
        feedback: feedback ?? null,
      }),
    }).then(jsonOrThrow);
  },
  retryItem(jobId: string, itemId: string): Promise<Item> {
    return f(`/api/jobs/${jobId}/items/${itemId}/retry`, { method: "POST" }).then(jsonOrThrow);
  },
  dispatch(jobId: string): Promise<{ job: Job; results: { ok: boolean; error?: string }[] }> {
    return f(`/api/jobs/${jobId}/dispatch`, { method: "POST" })
      .then(jsonOrThrow)
      .then((d) => ({ job: d.job as Job, results: (d.results ?? []) as { ok: boolean; error?: string }[] }));
  },
  pipelineStatus(): Promise<PipelineStatus> {
    return f("/api/pipeline/status").then(jsonOrThrow);
  },
  pipelineProbe(): Promise<{ ok: boolean; web?: string; error?: string }> {
    return f("/api/pipeline/probe", { method: "POST" }).then(jsonOrThrow);
  },
  health(): Promise<Health> {
    return f("/api/health").then(jsonOrThrow);
  },
  configuration(): Promise<ConfigurationSummary> {
    return f("/api/configuration").then(jsonOrThrow);
  },
  overview(): Promise<Overview> {
    return f("/api/monitor/overview").then(jsonOrThrow);
  },
  funnel(): Promise<Funnel> {
    return f("/api/pipeline/funnel").then(jsonOrThrow);
  },
  queues(): Promise<Queues> {
    return f("/api/pipeline/queues").then(jsonOrThrow);
  },
  track(name: string): Promise<TrackResult> {
    return f(`/api/pipeline/track?name=${encodeURIComponent(name)}`).then(jsonOrThrow);
  },
  pipelineItems(statuses = "dispatched,published"): Promise<PipelineItem[]> {
    return f(`/api/monitor/items?statuses=${encodeURIComponent(statuses)}`).then(jsonOrThrow);
  },
  deleteJob(id: string): Promise<void> {
    return f(`/api/jobs/${id}`, { method: "DELETE" }).then((r) => {
      if (!r.ok && r.status !== 204) throw new Error(`Eliminazione fallita (${r.status})`);
    });
  },
  revectorize(jobId: string, itemId: string, threshold: number | null): Promise<Item> {
    const body = threshold == null ? { autoThreshold: true } : { autoThreshold: false, threshold };
    return f(`/api/jobs/${jobId}/items/${itemId}/revectorize`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }).then(jsonOrThrow);
  },
  metadataPrompt(): Promise<Guidance> {
    return f("/api/metadata-prompt").then(jsonOrThrow);
  },
  rebuildMetadataPrompt(): Promise<RebuildResult> {
    return f("/api/metadata-prompt/rebuild", { method: "POST" }).then(jsonOrThrow);
  },
  resetMetadataPrompt(): Promise<Guidance> {
    return f("/api/metadata-prompt", { method: "DELETE" }).then(jsonOrThrow);
  },
  metadataFeedback(limit = 50): Promise<FeedbackEntry[]> {
    return f(`/api/metadata-prompt/feedback?limit=${limit}`).then(jsonOrThrow);
  },
  backofficeStages(): Promise<{ library: string; label: string }[]> {
    return f("/api/backoffice/stages").then(jsonOrThrow);
  },
  /** Il cruscotto: andamento, efficienza, stagionalità e la prova sui metadati. */
  insights(library = "ImagesSent", campione = 600, aggiorna = false): Promise<Insights> {
    return f(`/api/insights/quadro?library=${encodeURIComponent(library)}&campione=${campione}`
             + (aggiorna ? "&aggiorna=true" : "")).then(jsonOrThrow);
  },
  /** Rifà l'aggancio fra vendite e libreria adesso, invece di aspettare il giro dell'ora. */
  insightsRiaggancia(): Promise<{ ok: boolean; inCorso: boolean; ultimo?: string | null }> {
    return f("/api/insights/riaggancia", { method: "POST" }).then(jsonOrThrow);
  },
  /** Il quadro d'insieme di ciò che è già online: quanto vende, quanto no, quanto è migliorabile. */
  bonificaQuadro(library = "ImagesSent"): Promise<BonificaQuadro> {
    return f(`/api/bonifica/quadro?library=${encodeURIComponent(library)}`).then(jsonOrThrow);
  },
  /** La coda di lavoro: le immagini più deboli, con il consiglio e la sua motivazione. */
  bonificaCoda(punteggioMax = 79, pageToken?: string | null, library = "ImagesSent"): Promise<{
    ok: boolean; error?: string; righe: BonificaRiga[]; nextPageToken?: string | null;
  }> {
    const q = new URLSearchParams({ library, take: "24", punteggioMax: String(punteggioMax) });
    if (pageToken) q.set("pageToken", pageToken);
    return f(`/api/bonifica/coda?${q}`).then(jsonOrThrow);
  },
  /** Se la colonna del punteggio è piena: finché non lo è, i filtri per punteggio vedono meno. */
  backofficePunteggioStato(library?: string): Promise<PunteggioStato> {
    const q = library ? `?library=${encodeURIComponent(library)}` : "";
    return f(`/api/backoffice/punteggio/stato${q}`).then(jsonOrThrow);
  },
  backofficeItems(library: string, take = 24, pageToken?: string | null, search?: string, field?: string,
                  punteggioMin?: number, punteggioMax?: number): Promise<BackofficePage> {
    const q = new URLSearchParams({ library, take: String(take) });
    if (pageToken) q.set("pageToken", pageToken);
    if (search) q.set("search", search);
    if (field) q.set("field", field);
    if (punteggioMin !== undefined) q.set("punteggioMin", String(punteggioMin));
    if (punteggioMax !== undefined) q.set("punteggioMax", String(punteggioMax));
    return f(`/api/backoffice/items?${q}`).then(jsonOrThrow);
  },
  backofficeUpdate(library: string, id: number, body: { title?: string; description?: string; tags?: string }): Promise<BackofficeMutation> {
    return f(`/api/backoffice/items/${id}?library=${encodeURIComponent(library)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }).then(jsonOrThrow);
  },
  /**
   * Registra la correzione appena salvata perché la revisione del prompt possa impararla.
   * `generated` sono i valori trovati aprendo l'immagine, cioè quelli proposti dall'AI.
   */
  backofficeFeedback(library: string, id: number, body: {
    generatedTitle?: string;
    generatedDescription?: string;
    generatedKeywords?: string;
    title?: string;
    description?: string;
    keywords?: string;
    note?: string;
  }): Promise<{ ok: boolean; recorded?: boolean; message?: string; error?: string;
                keywordsAdded?: string[]; keywordsRemoved?: string[]; pending?: number }> {
    return f(`/api/backoffice/items/${id}/feedback?library=${encodeURIComponent(library)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }).then(jsonOrThrow);
  },
  backofficeSend(library: string, id: number, value = true, force = false): Promise<BackofficeMutation> {
    const q = new URLSearchParams({ library, value: String(value), force: String(force) });
    return f(`/api/backoffice/items/${id}/send?${q}`, { method: "POST" }).then(jsonOrThrow);
  },
  /**
   * Fa partire la pubblicazione adesso, invece di aspettare il giro di sorveglianza.
   *
   * Alza il flag come l'invio normale, ma scrive anche sulla coda: il file parte in secondi
   * invece che al prossimo quarto d'ora.
   */
  backofficePubblicaOra(library: string, id: number, force = false):
      Promise<BackofficeMutation & { accodate?: number; nonRiuscite?: number; gruppo?: number }> {
    const q = new URLSearchParams({ library, force: String(force) });
    return f(`/api/backoffice/items/${id}/pubblica-ora?${q}`, { method: "POST" }).then(jsonOrThrow);
  },
  /**
   * Rimette in gioco un file rimasto fermo in «in pubblicazione».
   *
   * Chi accoda segna il file come preso in carico prima di mettere il messaggio in coda, e da quel
   * momento nessuno può più scrivere su quell'elemento: è la protezione contro il doppio invio. Se
   * però la catena non si chiude, il file resta fermo e invisibile a tutti. Il server accetta lo
   * sblocco solo oltre una certa anzianità, perché toglierlo a una consegna in volo la farebbe
   * partire due volte.
   */
  backofficeSblocca(library: string, id: number): Promise<BackofficeMutation> {
    return f(`/api/backoffice/items/${id}/sblocca?library=${encodeURIComponent(library)}`,
             { method: "POST" }).then(jsonOrThrow);
  },
  backofficeMove(library: string, id: number, targetLibrary: string): Promise<{ ok: boolean; movedTo?: string; error?: string }> {
    return f(`/api/backoffice/items/${id}/move?library=${encodeURIComponent(library)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ targetLibrary }),
    }).then(jsonOrThrow);
  },
  backofficeDelete(library: string, id: number): Promise<{ ok: boolean; error?: string }> {
    return f(`/api/backoffice/items/${id}?library=${encodeURIComponent(library)}`, { method: "DELETE" }).then(jsonOrThrow);
  },
  /** Releases a file left checked out; discard throws the pending version away instead of keeping it. */
  backofficeCheckIn(library: string, id: number, discard = false): Promise<CheckInResult> {
    const q = new URLSearchParams({ library, discard: String(discard) });
    return f(`/api/backoffice/items/${id}/checkin?${q}`, { method: "POST" }).then(jsonOrThrow);
  },
  systemPlan(): Promise<PlanState> {
    return f("/api/system/plan").then(jsonOrThrow);
  },
  setSystemPlan(sku: string): Promise<{ ok: boolean; sku?: string; tier?: string; alwaysOn?: boolean; error?: string }> {
    return f(`/api/system/plan?sku=${encodeURIComponent(sku)}`, { method: "POST" }).then(jsonOrThrow);
  },
  backofficeRegenerate(library: string, id: number): Promise<RegenerateResult> {
    return f(`/api/backoffice/items/${id}/regenerate?library=${encodeURIComponent(library)}`, { method: "POST" }).then(jsonOrThrow);
  },
  backofficeRegenerateMany(library: string, ids: number[]): Promise<{ ok: boolean; requested: number; succeeded: number; results: RegenerateResult[] }> {
    return f(`/api/backoffice/regenerate?library=${encodeURIComponent(library)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids }),
    }).then(jsonOrThrow);
  },
  /** Ritraccia SVG ed EPS di un'immagine già in libreria, riscrivendoli al loro posto. */
  backofficeRivettorializza(library: string, id: number): Promise<RivettorializzaResult> {
    return f(`/api/backoffice/items/${id}/rivettorializza?library=${encodeURIComponent(library)}`, { method: "POST" }).then(jsonOrThrow);
  },
  trends(horizonDays = 150, style = "silhouette"): Promise<Trends> {
    return f(`/api/trends?horizonDays=${horizonDays}&style=${encodeURIComponent(style)}`).then(jsonOrThrow);
  },
  themeOpportunities(style = "silhouette", category?: string, geo = "US", count = 12, refresh = false): Promise<Themes> {
    const cat = category ? `&category=${encodeURIComponent(category)}` : "";
    return f(`/api/trends/themes?style=${encodeURIComponent(style)}&geo=${geo}&count=${count}&refresh=${refresh}${cat}`).then(jsonOrThrow);
  },
  checkTopic(topic: string, promptStyle = "silhouette", refresh = false): Promise<{
    topic: string; engine?: string; engineLabel?: string; warning?: string | null;
    fromCache?: boolean; generatedAt?: string; relevance: StockRelevance;
  }> {
    return f(`/api/trends/relevance?topic=${encodeURIComponent(topic)}&promptStyle=${encodeURIComponent(promptStyle)}&refresh=${refresh}`).then(jsonOrThrow);
  },
  aiEngine(): Promise<{ agentic: boolean; label: string; cacheHours: number }> {
    return f(`/api/trends/engine`).then(jsonOrThrow);
  },
  clearAiCache(): Promise<{ ok: boolean; removed: number; message: string }> {
    return f(`/api/trends/cache/clear`, { method: "POST" }).then(jsonOrThrow);
  },
  liveTrends(geo = "US", take = 12, style = "silhouette", onlyUsable = false, promptStyle = "silhouette"): Promise<LiveTrends> {
    return f(`/api/trends/live?geo=${geo}&take=${take}&style=${encodeURIComponent(style)}` +
             `&onlyUsable=${onlyUsable}&promptStyle=${encodeURIComponent(promptStyle)}`).then(jsonOrThrow);
  },
  predictedTrends(yearsAhead = 2, promptStyle = "silhouette"): Promise<Predictions> {
    return f(`/api/trends/predicted?yearsAhead=${yearsAhead}&promptStyle=${encodeURIComponent(promptStyle)}`).then(jsonOrThrow);
  },
  /** Manda al server l'esportazione del portale Adobe, così com'è scaricata. */
  importSales(csv: string): Promise<SalesImport> {
    return f(`/api/sales/import`, {
      method: "POST",
      headers: { "Content-Type": "text/csv" },
      body: csv,
    }).then(jsonOrThrow);
  },
  salesSummary(from?: string, to?: string): Promise<SalesSummary> {
    const q = new URLSearchParams();
    if (from) q.set("from", from);
    if (to) q.set("to", to);
    return f(`/api/sales/summary${q.toString() ? `?${q}` : ""}`).then(jsonOrThrow);
  },
  salesWarehouse(library = "ImagesSent", take = 200): Promise<SalesWarehouse> {
    return f(`/api/sales/warehouse?library=${encodeURIComponent(library)}&take=${take}`).then(jsonOrThrow);
  },
  salesDna(minimo = 3): Promise<SalesDna> {
    return f(`/api/sales/dna?minimo=${minimo}`).then(jsonOrThrow);
  },
  strategia(caricatiUltimiSeiMesi = 0): Promise<Strategia> {
    return f(`/api/strategy/quadro?caricatiUltimiSeiMesi=${caricatiUltimiSeiMesi}`).then(jsonOrThrow);
  },
  tuneKeywordsPreview(library = "ImagesToSend", take = 50): Promise<TuneKeywords> {
    return f(`/api/tune/keywords/preview?library=${encodeURIComponent(library)}&take=${take}`).then(jsonOrThrow);
  },
  tuneKeywordsApply(library: string, ids: number[]): Promise<{ ok: boolean; scritti?: number; invariati?: number; error?: string }> {
    return f(`/api/tune/keywords/apply?library=${encodeURIComponent(library)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(ids),
    }).then(jsonOrThrow);
  },
  tuneOverlap(library = "ImagesToSend", take = 150): Promise<TuneOverlap> {
    return f(`/api/tune/overlap?library=${encodeURIComponent(library)}&take=${take}`).then(jsonOrThrow);
  },
  tuneMute(library = "ImagesToSend", take = 150): Promise<TuneMute> {
    return f(`/api/tune/mute?library=${encodeURIComponent(library)}&take=${take}`).then(jsonOrThrow);
  },
  checkDuplicate(name: string): Promise<{ ok: boolean; duplicate?: boolean; error?: string }> {
    return f(`/api/pipeline/duplicate?name=${encodeURIComponent(name)}`).then(jsonOrThrow);
  },
};

export const isTerminal = (s: string) => s === "completed" || s === "failed" || s === "dispatched" || s === "published";

export type PipelineItem = {
  jobId: string;
  itemId: string;
  baseName: string;
  title: string;
  status: string;
  publishStatus?: string | null;
  metadataSource?: string | null;
  dispatchedAt?: string | null;
  publishedAt?: string | null;
  trackName: string;
};

export type StatusInfo = { label: string; cls: string; icon: string };

export type ValidationIssue = { severity: string; field: string; message: string; site?: string | null };
export type SiteScore = { site: string; score: number };
export type Validation = { score: number; blocksDispatch: boolean; sites: SiteScore[]; issues: ValidationIssue[] };

export type TopicSuggestion = {
  name: string;
  category: string;
  peakDate: string;
  daysUntilPeak: number;
  inSubmissionWindow: boolean;
  opportunityScore: number;
  keywords: string[];
  adobeSearchUrl: string;
  freepikSearchUrl: string;
  advice: string;
  demandSource?: string;
  seasonalityRatio?: number | null;
  momentum?: number | null;
  peakViews?: number | null;
  measuredPeakMonth?: number | null;
};
export type Trends = { generatedAt: string; horizonDays: number; style: string; demandSource?: string; note: string; suggestions: TopicSuggestion[] };

export type ThemeOpportunity = {
  name: string;
  category: string;
  opportunityScore: number;
  timing: "now" | "soon" | "plan" | "late" | "evergreen" | "nodata";
  advice: string;
  averageViews: number;
  seasonalityRatio?: number | null;
  peakMonth?: number | null;
  daysToPeak?: number | null;
  inSubmissionWindow: boolean;
  momentum?: number | null;
  trendRatio?: number | null;
  trajectory: string;
  series: number[];
  hotNow: string[];
  concepts: string[];
  prompts: string[];
  adobeSearchUrl: string;
  freepikSearchUrl: string;
  wikiArticle: string;
};
export type Themes = {
  generatedAt: string; source: string; note: string;
  engine?: "agentic" | "rules"; engineLabel?: string; warning?: string | null;
  fromCache?: boolean; cacheHours?: number;
  categories: string[]; count: number; themes: ThemeOpportunity[];
};

export type StockRelevance = {
  usable: boolean;
  verdict: "ok" | "adapt" | "reject" | "unknown";
  score: number;
  reason: string;
  theme?: string | null;
  entityKind?: string | null;
  concepts: string[];
  prompts: string[];
};

export type LiveTrend = {
  title: string;
  searchTraffic: number;
  wikiArticle?: string | null;
  trendRatio?: number | null;
  trajectory: string;
  opportunityScore: number;
  series: number[];
  relatedNews: string[];
  adobeSearchUrl: string;
  freepikSearchUrl: string;
  advice: string;
  relevance?: StockRelevance | null;
};
export type LiveTrends = {
  generatedAt: string; geo: string; source: string;
  onlyUsable?: boolean; filter?: string; trends: LiveTrend[];
};

export type NewsHeadline = { title: string; source: string; published?: string | null; link: string };
export type PredictedTrend = {
  topic: string;
  category: string;
  targetYear?: number | null;
  mentions: number;
  confidence: number;
  keywords: string[];
  evidence: NewsHeadline[];
  relevance?: StockRelevance | null;
};
export type Predictions = { generatedAt: string; source: string; predictions: PredictedTrend[] };

export const STATUS_LABELS: Record<string, StatusInfo> = {
  queued: { label: "In coda", cls: "s-queued", icon: "…" },
  processing: { label: "In lavorazione", cls: "s-processing", icon: "⟳" },
  completed: { label: "Pronta", cls: "s-completed", icon: "✓" },
  dispatched: { label: "Inviata alla pipeline", cls: "s-dispatched", icon: "↗" },
  published: { label: "Pubblicata", cls: "s-published", icon: "★" },
  failed: { label: "Errore", cls: "s-failed", icon: "✕" },
};

export const statusInfo = (s: string): StatusInfo =>
  STATUS_LABELS[s] ?? { label: s, cls: "", icon: "•" };
