/**
 * La sessione SharePoint del browser.
 *
 * Le anteprime non passano dall'API: il tag <img> le chiede a SharePoint con i cookie di chi
 * guarda. È il motivo per cui la galleria è veloce, ed è anche l'unico modo in cui può rompersi
 * senza che nulla nella pagina lo spieghi -- ogni miniatura diventa un riquadro vuoto e sembra un
 * difetto dell'applicazione, mentre manca soltanto un accesso.
 *
 * Qui si tiene lo stato della sessione e si avvisa chi guarda quando torna a esserci, così le
 * immagini già date per rotte possono riprovare invece di restare vuote fino al ricaricamento.
 */

export type StatoSessione = "ignoto" | "presente" | "assente";

let stato: StatoSessione = "ignoto";
let provaUrl: string | null = null;
let loginUrl: string | null = null;
const ascoltatori = new Set<() => void>();

/** Cambia a ogni ripristino: le immagini lo usano come chiave per rimontarsi e riprovare. */
let generazione = 0;

function avvisa() { ascoltatori.forEach((f) => f()); }

export const sessioneSharePoint = {
  stato: () => stato,
  generazione: () => generazione,
  loginUrl: () => loginUrl,

  abbonati(f: () => void) {
    ascoltatori.add(f);
    return () => { ascoltatori.delete(f); };
  },

  /**
   * Segnala che un'immagine di SharePoint non è arrivata.
   *
   * Un solo fallimento non prova che manchi la sessione -- un file può essere stato cancellato --
   * quindi non si conclude niente finché non sono almeno tre. E anche allora non si conclude: si
   * rifà la prova, perché il giudice deve essere una verifica e non una deduzione.
   *
   * La prima versione dichiarava direttamente "assente" e solo se lo stato non era già "presente".
   * Era un difetto: una sessione che scade mentre si lavora lasciava lo stato bloccato su
   * "presente", le anteprime smettevano di arrivare e l'avviso non compariva più -- cioè proprio
   * nel caso in cui serviva. Trovato collaudando il percorso di fallimento.
   */
  segnalaRottura() {
    if (++rotture < 3 || sondaInCorso) return;
    rotture = 0;
    sessioneSharePoint.prova();
  },

  /** Un'immagine è arrivata: la sessione c'è, qualunque cosa si pensasse prima. */
  segnalaRiuscita() {
    rotture = 0;
    if (stato !== "presente") {
      // Se si veniva da "assente", le immagini gia' date per rotte devono riprovare: questa che
      // e' arrivata dimostra che la sessione c'e'.
      if (stato === "assente") generazione++;
      stato = "presente";
      avvisa();
    }
  },

  /**
   * Prova la sessione caricando una miniatura vera.
   *
   * Chi chiede mentre una prova è già in corso riceve quella prova, non lo stato precedente:
   * restituire il valore vecchio farebbe concludere ad `accedi` che l'accesso è riuscito solo
   * perché lo era prima di scadere.
   */
  prova(): Promise<StatoSessione> {
    if (sondaInCorso) return sondaInCorso;
    sondaInCorso = eseguiProva().finally(() => { sondaInCorso = null; });
    return sondaInCorso;
  },

  /**
   * Apre l'accesso a SharePoint e aspetta che vada a buon fine.
   *
   * Non si può sapere quando l'utente ha finito -- la finestra è di un'altra origine e non si può
   * ispezionare -- quindi si riprova la sessione ogni due secondi finché non compare. Appena c'è,
   * la finestra si chiude da sola e le immagini rotte si rimontano.
   */
  async accedi(): Promise<boolean> {
    if (!loginUrl) await this.prova();
    if (!loginUrl) return false;

    const finestra = window.open(loginUrl, "accesso-sharepoint", "width=1000,height=800");
    const scadenza = Date.now() + 3 * 60 * 1000;

    while (Date.now() < scadenza) {
      await new Promise((r) => setTimeout(r, 2000));
      if (await this.prova() === "presente") {
        try { finestra?.close(); } catch { /* può essere già chiusa */ }
        return true;
      }
      if (finestra?.closed) {
        // Chiusa a mano: un ultimo tentativo, poi si smette di insistere.
        return await this.prova() === "presente";
      }
    }
    return false;
  },
};

let rotture = 0;
let sondaInCorso: Promise<StatoSessione> | null = null;

/**
 * Mentre la sessione manca si riprova da soli.
 *
 * Il pulsante non è l'unico modo di rientrare: capita di accedere a SharePoint in un'altra scheda,
 * o che la sessione si rinnovi per conto suo. Senza questa riprova l'avviso resterebbe lì a dire
 * una cosa non più vera, e l'unico rimedio sarebbe ricaricare la pagina -- cioè esattamente il
 * gesto che questa parte esiste per evitare.
 *
 * Si riprova solo mentre lo stato è "assente" e solo a scheda visibile: insistere su una scheda in
 * secondo piano sarebbe traffico speso per una risposta che nessuno sta guardando.
 */
if (typeof document !== "undefined") {
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden && stato === "assente") sessioneSharePoint.prova();
  });
  setInterval(() => {
    if (stato === "assente" && !document.hidden) sessioneSharePoint.prova();
  }, 30000);
}

/**
 * Carica una miniatura vera e vede se arriva.
 *
 * Si usa un <img> e non una fetch di proposito: una richiesta verso SharePoint da un'altra origine
 * verrebbe fermata dal CORS prima ancora di sapere se l'utente è autenticato, mentre il tag
 * immagine non è soggetto a quella regola e il suo onload/onerror dice esattamente quel che serve.
 *
 * Il bersaglio dev'essere un file protetto: le risorse statiche di _layouts rispondono anche senza
 * sessione e direbbero sempre di sì.
 */
async function eseguiProva(): Promise<StatoSessione> {
  if (!provaUrl) await chiediBersaglio(false);
  if (!provaUrl) return stato;

  let esito = await caricaBersaglio(provaUrl);

  // Un bersaglio che non si disegna -- un EPS, un SVG, una cartella -- fallisce sempre, e la prova
  // direbbe "sessione assente" per ore con la sessione validissima: l'applicazione chiederebbe un
  // accesso che non serve e la finestra non si chiuderebbe mai, perché aspetta un esito che non può
  // arrivare. Prima di credere al fallimento si chiede un altro bersaglio, una volta sola: se
  // fallisce anche quello, allora è davvero la sessione.
  if (!esito && !bersaglioRinnovato) {
    bersaglioRinnovato = true;
    const vecchio = provaUrl;
    await chiediBersaglio(true);
    if (provaUrl && provaUrl !== vecchio) esito = await caricaBersaglio(provaUrl);
  }

  const nuovo: StatoSessione = esito ? "presente" : "assente";
  if (nuovo !== stato) {
    // La generazione cambia solo passando da assente a presente: è l'unico caso in cui c'è
    // qualcosa da riprovare. Incrementarla anche alla prima verifica farebbe rimontare tutte le
    // immagini appena aperta la pagina, buttando via le richieste già in volo.
    if (stato === "assente" && esito) generazione++;
    stato = nuovo;
    rotture = 0;
    avvisa();
  }
  return nuovo;
}

let bersaglioRinnovato = false;

async function chiediBersaglio(nuova: boolean) {
  try {
    const r = await fetch(`/api/pipeline/sessione-sharepoint${nuova ? "?nuova=true" : ""}`).then((x) => x.json());
    provaUrl = r?.provaUrl ?? provaUrl;
    loginUrl = r?.loginUrl ?? r?.siteUrl ?? loginUrl;
  } catch { /* senza indirizzo non si può provare */ }
}

function caricaBersaglio(url: string): Promise<boolean> {
  return new Promise<boolean>((risolvi) => {
    const img = new Image();
    const scadenza = setTimeout(() => risolvi(false), 8000);
    img.onload = () => { clearTimeout(scadenza); risolvi(true); };
    img.onerror = () => { clearTimeout(scadenza); risolvi(false); };
    // Una miniatura già in cache direbbe di sì anche a sessione scaduta: la prova deve viaggiare.
    img.src = `${url}&_p=${Date.now()}`;
  });
}
