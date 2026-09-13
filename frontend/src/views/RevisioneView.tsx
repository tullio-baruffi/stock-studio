import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { api, type BackofficeItem, type Deliverable, type ParametriTracciato, type TracciatoConsigliato } from "../api";
import AuthImage from "../components/AuthImage";
import { AvvisoSessioneSharePoint } from "../components/AccessoSharePoint";
import { Attesa, Rotella, Segnaposto } from "../components/Attesa";
import PannelloTracciato from "../components/PannelloTracciato";

/**
 * Revisione in due tempi: prima si guarda l'insieme, poi si entra in una immagine.
 *
 * La prima versione partiva dal dettaglio e teneva le altre immagini in un rullino. Funziona
 * finché ce ne sono venti; con un migliaio il rullino diventa un budello in cui si scorre alla
 * cieca e non si vede mai quante ne restano né quali si somigliano. Chi seleziona per mestiere
 * guarda prima il mucchio, sceglie, e solo allora entra nel particolare.
 *
 * Quindi: **galleria** come porta d'ingresso, con tutte le immagini caricate via via che si
 * scorre, e **dettaglio** che si apre da una card. Nel dettaglio compaiono anche le consegne
 * vettoriali -- l'SVG si vede davvero, l'EPS si scarica -- perché è lì che si scopre un tracciato
 * venuto male, non nella miniatura del JPG.
 *
 * ## Sulle azioni distruttive
 * "Approva" sposta allo stadio successivo, ed è reversibile spostando indietro. Da "Pronti per
 * l'invio" invece non sposta: alza il flag Invia e la pipeline fa partire il caricamento vero,
 * spostando il gruppo fra i Pubblicati solo quando l'invio è riuscito. "Scarta" invece
 * cancellerebbe, e una cancellazione legata a un singolo tasto è un incidente che aspetta di
 * accadere. Quindi X **non cancella**: segna, e basta. Le segnate si eliminano tutte insieme, con
 * un passaggio esplicito che dice quante sono. Fino a quel momento ogni X si annulla premendola.
 */


/**
 * Le tre librerie della pipeline, negli stadi in cui il lavoro le attraversa.
 *
 * La revisione ne guardava una sola, quella d'ingresso, come se le altre due non esistessero: ma
 * un file già pronto per l'invio si corregge esattamente allo stesso modo, e uno già pubblicato si
 * vuole almeno poter rivedere. Lo stadio decide anche cosa significhi «approvare»: dall'ultimo non
 * si va da nessuna parte.
 */
const STADI = [
  { id: "ImagesToClassify", label: "Da revisionare", prossimo: "ImagesToSend" as string | null },
  { id: "ImagesToSend", label: "Pronti per l'invio", prossimo: "ImagesSent" as string | null },
  { id: "ImagesSent", label: "Pubblicati", prossimo: null as string | null },
];

/** Quante immagini per pagina. Ogni anteprima costa uno scarico da SharePoint: meglio a scaglioni. */
const PAGINA = 24;

type Esito = { testo: string; tipo: "ok" | "errore" } | null;

/** Fasce di punteggio. */
type Fascia = "tutte" | "gravi" | "scarse" | "base" | "discrete" | "buone" | "ottime" | "eccellenti";

/**
 * Intervalli veri, e stretti.
 *
 * Prima erano tre fasce larghe, poi -- sbagliando -- soglie cumulative: «sotto 70» e «sotto 80»
 * contenevano le stesse immagini, e in «sotto 70» finiva insieme roba da 40 e da 67, che è come
 * non filtrare.
 *
 * Il vincolo vero non e' il numero di condizioni: SharePoint rifiuta qualsiasi filtro che
 * selezioni piu' di cinquemila elementi. Misurata la distribuzione dei pubblicati, la massa sta
 * addossata fra 70 e 89 -- 70-74 sono 3.313 e 80-84 sono 3.483 -- quindi le fasce vanno strette
 * proprio li' dove le immagini si accalcano, e possono allargarsi agli estremi dove sono poche.
 *
 * Ogni fascia qui sotto resta sotto i cinquemila su una libreria da diecimila. Se un giorno una di
 * esse crescesse oltre, l'applicazione lo dice invece di restituire una griglia vuota.
 */
const FASCE: { id: Fascia; label: string; titolo: string; min?: number; max?: number }[] = [
  { id: "tutte", label: "Tutte", titolo: "Tutte le immagini di questa libreria" },
  { id: "gravi", label: "<60", titolo: "Metadati gravemente insufficienti", max: 59 },
  { id: "scarse", label: "60-69", titolo: "Sotto la sufficienza: da rilavorare", min: 60, max: 69 },
  { id: "base", label: "70-74", titolo: "Appena sufficienti", min: 70, max: 74 },
  { id: "discrete", label: "75-79", titolo: "Discrete, migliorabili con poco", min: 75, max: 79 },
  { id: "buone", label: "80-84", titolo: "Buone", min: 80, max: 84 },
  { id: "ottime", label: "85-89", titolo: "Molto buone", min: 85, max: 89 },
  { id: "eccellenti", label: "90+", titolo: "Le migliori: cosa sta già funzionando", min: 90 },
];

/** I metadati come si sono trovati aprendo l'immagine, cioè quelli proposti dall'AI. */
type Originale = { title: string; keywords: string };

export default function RevisioneView() {
  const [items, setItems] = useState<BackofficeItem[]>([]);
  const [token, setToken] = useState<string | null>(null);
  const [altre, setAltre] = useState(true);
  const [totale, setTotale] = useState<number | null>(null);
  const [caricamento, setCaricamento] = useState(true);
  const [esito, setEsito] = useState<Esito>(null);
  const [occupato, setOccupato] = useState(false);
  /** Quale operazione è in corso, per far girare la rotella solo sul pulsante che l'ha avviata. */
  const [azione, setAzione] = useState<string | null>(null);

  /**
   * L'immagine aperta è tenuta per identificativo e non per posizione.
   *
   * Con i filtri la posizione non è più un riferimento stabile: basta restringere la fascia di
   * punteggio perché l'indice tre indichi un'altra immagine. L'identificativo invece resta valido
   * comunque si riordini o si filtri l'elenco.
   */
  const [apertaId, setApertaId] = useState<number | null>(null);
  const [selezione, setSelezione] = useState<Set<number>>(new Set());
  const [daScartare, setDaScartare] = useState<Set<number>>(new Set());
  const [approvate, setApprovate] = useState(0);
  const [zoom, setZoom] = useState(false);
  const [consegna, setConsegna] = useState<number | null>(null);
  /**
   * Quando un'immagine è stata ritracciata, per contrassegnare l'anteprima del vettoriale.
   *
   * L'indirizzo del file non cambia mai: senza un contrassegno diverso il browser ripescherebbe
   * dalla cache il disegno vecchio, e l'unica prova visibile che il ritracciamento è servito
   * sarebbe invisibile proprio dove la si cerca.
   */
  const [ritracciate, setRitracciate] = useState<Record<number, number>>({});
  /**
   * La finestra con cui si sceglie **come** ritracciare, e quel che ci si è scelto.
   *
   * La scelta resta fra un'immagine e l'altra di proposito: quando una taratura si rivela giusta
   * per un disegno, di solito lo è anche per i suoi fratelli dello stesso lotto, e rimetterla a
   * mano ogni volta sarebbe il modo più sicuro per non usarla.
   */
  const [finestraTracciato, setFinestraTracciato] = useState(false);
  const [tracciatoScelto, setTracciatoScelto] = useState<ParametriTracciato>({});
  /**
   * Che taratura chiede l'immagine aperta, misurata guardandola.
   *
   * Si chiede all'apertura della finestra e non prima: e' una lettura dell'originale conservato,
   * che costa una discesa da blob e una passata sui pixel, e farla per ogni immagine sfogliata
   * vorrebbe dire pagarla anche per le novantanove che non si ritracciano.
   */
  const [consigliato, setConsigliato] = useState<TracciatoConsigliato | "attesa" | null>(null);
  /** Se i valori nel pannello sono quelli proposti: evita di riproporli quando gia' ci sono. */
  const [consigliatiApplicati, setConsigliatiApplicati] = useState(false);
  const [nota, setNota] = useState("");

  const [fascia, setFascia] = useState<Fascia>("tutte");
  const [cerca, setCerca] = useState("");
  const [soloSegnate, setSoloSegnate] = useState(false);

  /**
   * Se in questa libreria restano immagini senza punteggio depositato.
   *
   * Un filtro per punteggio non può vedere ciò che un punteggio non ce l'ha: finché il riempimento
   * non è finito, «sotto 70» mostra meno di quello che c'è, e una griglia vuota sembra una libreria
   * a posto invece di un lavoro a metà. Va detto, non lasciato indovinare.
   */
  const [punteggiIncompleti, setPunteggiIncompleti] = useState(false);

  /** Quale delle tre librerie si sta guardando, e dove porta l'approvazione da lì. */
  const [stadio, setStadio] = useState("ImagesToClassify");
  const prossimo = STADI.find((s) => s.id === stadio)?.prossimo ?? null;

  /**
   * Il testo per cui il server sta effettivamente cercando.
   *
   * È separato da quello che si sta scrivendo perché la ricerca ora avviene su SharePoint, non
   * qui: filtrare a ogni tasto premuto significherebbe una chiamata per lettera. Si cerca quando
   * si preme Invio o il pulsante.
   */
  const [ricerca, setRicerca] = useState("");

  const fondo = useRef<HTMLDivElement>(null);
  const originali = useRef<Map<number, Originale>>(new Map());
  /** Solo la richiesta più recente può scrivere: ricaricare mentre si scorre le mescolerebbe. */
  const richiesta = useRef(0);

  /**
   * L'elenco che si sta effettivamente guardando.
   *
   * Resta qui solo il filtro che il server non può fare: le segnate, che vivono in questa sessione
   * e non esistono in libreria. Ricerca e punteggio sono passati al server, dove valgono su tutte
   * le migliaia di file invece che sulle poche già scaricate.
   */
  const visibili = useMemo(
    () => (soloSegnate ? items.filter((it) => daScartare.has(it.id)) : items),
    [items, soloSegnate, daScartare],
  );

  const posizione = apertaId === null ? -1 : visibili.findIndex((x) => x.id === apertaId);
  const corrente = posizione >= 0 ? visibili[posizione] : undefined;

  const leggi = useCallback(async (pageToken: string | null, azzera: boolean) => {
    const mia = ++richiesta.current;
    setCaricamento(true);
    try {
      const f = FASCE.find((x) => x.id === fascia)!;
      const p = await api.backofficeItems(stadio, PAGINA, pageToken, ricerca || undefined, "name", f.min, f.max);
      if (mia !== richiesta.current) return;

      if (!p.ok) {
        setEsito({ testo: p.error ?? "Lettura non riuscita.", tipo: "errore" });
        setAltre(false);
        if (azzera) setItems([]);
        return;
      }

      // I metadati che arrivano dal server sono la proposta dell'AI: si registrano una volta sola,
      // prima di qualsiasi correzione, perché il feedback possa dire cosa è stato cambiato.
      for (const it of p.items) {
        if (!originali.current.has(it.id))
          originali.current.set(it.id, { title: it.title, keywords: it.keywords.join(", ") });
      }

      setItems((cur) => {
        if (azzera) return p.items;
        const visti = new Set(cur.map((x) => x.id));
        return [...cur, ...p.items.filter((x) => !visti.has(x.id))];
      });
      setToken(p.nextPageToken ?? null);
      setAltre(Boolean(p.nextPageToken));
    } catch (e) {
      if (mia !== richiesta.current) return;
      const raw = (e as Error).message;
      setEsito({
        testo: /failed to fetch|networkerror|load failed/i.test(raw)
          ? "Il server non ha risposto: probabilmente è sotto carico. Attendi qualche secondo e riprova."
          : raw,
        tipo: "errore",
      });
      setAltre(false);
    } finally {
      if (mia === richiesta.current) setCaricamento(false);
    }
  }, [stadio, ricerca, fascia]);

  const ricarica = useCallback(() => {
    originali.current.clear();
    setApertaId(null);
    setSelezione(new Set());
    setDaScartare(new Set());
    setToken(null);
    setAltre(true);
    leggi(null, true);
  }, [leggi]);

  // Cambiare libreria o testo cercato riparte da capo: la pagina di prima non c'entra più niente.
  useEffect(() => {
    originali.current.clear();
    setApertaId(null);
    setSelezione(new Set());
    setToken(null);
    setAltre(true);
    leggi(null, true);
  }, [leggi]);

  // Quanto è grande lo stadio, per dire a che punto si è di un magazzino di migliaia.
  useEffect(() => {
    let vivo = true;
    const chiave = stadio === "ImagesToClassify" ? "classify" : stadio === "ImagesToSend" ? "send" : "sent";
    setTotale(null);
    api.funnel()
      .then((f) => { if (vivo && f.ok) setTotale(f.stages?.find((s) => s.key === chiave)?.count ?? null); })
      .catch(() => { /* il totale è una comodità: la sua assenza non deve disturbare la griglia */ });
    return () => { vivo = false; };
  }, [stadio]);

  // Se la colonna del punteggio è già piena in questa libreria. Si chiede al cambio di libreria e
  // ogni volta che si sceglie una fascia, perché il riempimento intanto va avanti.
  useEffect(() => {
    let vivo = true;
    if (fascia === "tutte") { setPunteggiIncompleti(false); return; }
    api.backofficePunteggioStato(stadio)
      .then((s) => { if (vivo) setPunteggiIncompleti(s.colonna?.completa === false); })
      .catch(() => { /* nel dubbio non si allarma: l'avviso è un aiuto, non un ostacolo */ });
    return () => { vivo = false; };
  }, [stadio, fascia]);

  // Scorrimento continuo: la pagina successiva parte quando il fondo entra in vista. Senza questo
  // "vedere tutto" si ridurrebbe a premere un pulsante ogni ventiquattro immagini.
  useEffect(() => {
    const s = fondo.current;
    if (!s || apertaId !== null || !altre || caricamento) return;
    const io = new IntersectionObserver((e) => {
      if (e[0].isIntersecting) leggi(token, false);
    }, { rootMargin: "300px" });
    io.observe(s);
    return () => io.disconnect();
  }, [apertaId, altre, caricamento, token, leggi]);

  /** Scorre l'elenco visibile, cioè quello filtrato: fuori dal filtro non si va. */
  const vai = useCallback((delta: number) => {
    if (posizione < 0) return;
    const p = Math.min(Math.max(posizione + delta, 0), visibili.length - 1);
    setApertaId(visibili[p]?.id ?? null);
    setZoom(false);
    setConsegna(null);
    setNota("");
  }, [posizione, visibili]);

  const apri = (id: number) => { setApertaId(id); setZoom(false); setConsegna(null); setNota(""); };
  const chiudi = () => { setApertaId(null); setZoom(false); setConsegna(null); setNota(""); };

  const commuta = (set: Set<number>, id: number) => {
    const n = new Set(set);
    if (n.has(id)) n.delete(id); else n.add(id);
    return n;
  };

  /**
   * Da "Pronti per l'invio" approvare significa pubblicare, non promuovere di stadio.
   *
   * Qui c'era il guasto che rendeva vana tutta la catena: il pulsante spostava il file in
   * "Pubblicati" senza mai alzare il flag Invia, cioe' senza accendere niente. Il file spariva
   * dalla coda e compariva fra i pubblicati, ma nessun marketplace lo aveva ricevuto.
   *
   * A spostarlo ci pensa la catena stessa, e solo quando l'invio e' davvero riuscito.
   */
  const pubblica = stadio === "ImagesToSend";

  /** Lo stato che il server ha gia' risolto: qui non si deduce niente. */
  const statoCorrente = corrente?.pipeline?.stato;
  /**
   * Le due domande che decidono i pulsanti, risposte dal server insieme allo stato.
   *
   * Prima si deducevano qui da una sola condizione — «è in attesa?» — e quella condizione non
   * copriva il file già preso in carico dalla coda ma non ancora spostato: per quello l'interfaccia
   * rimostrava «Invia ai marketplace» su qualcosa che stava già salendo.
   *
   * In assenza di risposta (librerie che non hanno lo stato) si lascia fare, com'era prima.
   */
  const puoInviare = corrente?.pipeline ? corrente.pipeline.puoInviare !== false : true;
  const puoForzare = corrente?.pipeline ? corrente.pipeline.puoForzare !== false : true;
  /** Chiesto o già in viaggio: in entrambi i casi l'invio non si ripete, ma il motivo è diverso. */
  const inViaggio = statoCorrente === "in-attesa" || statoCorrente === "in-consegna";

  /**
   * Dopo un'azione andata a buon fine si torna alla galleria.
   *
   * Restare nel dettaglio di un file che ha appena cambiato stato vuol dire guardare qualcosa che
   * non e' piu' vero: l'immagine e' uscita dall'elenco, i pulsanti non hanno piu' senso, e l'unica
   * cosa da fare e' andarsene. Tanto vale farlo da soli.
   */
  const tornaAllaGalleria = useCallback(() => {
    setApertaId(null);
    setZoom(false);
    setConsegna(null);
    setNota("");
  }, []);

  const approva = useCallback(async (id?: number) => {
    const bersaglio = id ?? corrente?.id;
    // Dall'ultima libreria non si va avanti: approvare non vorrebbe dire niente.
    if (!bersaglio || occupato || !prossimo) return;
    const it = items.find((x) => x.id === bersaglio);

    // La scorciatoia da tastiera non passa dal pulsante, quindi la regola va ripetuta qui: senza,
    // premere «A» su un file già in viaggio lo rimetterebbe in coda.
    if (pubblica && it?.pipeline && !it.pipeline.puoInviare) {
      setEsito({ testo: `${it.fileName}: ${it.pipeline.spiega}`, tipo: "errore" });
      return;
    }

    const dalDettaglio = apertaId === bersaglio;
    setOccupato(true);
    setAzione("approva");
    try {
      const esitoOk = () => {
        setApprovate((n) => n + 1);
        setEsito({
          testo: pubblica
            ? `${it?.fileName ?? bersaglio} → in consegna ai marketplace`
            : `${it?.fileName ?? bersaglio} → ${STADI.find((s) => s.id === prossimo)?.label ?? prossimo}`,
          tipo: "ok",
        });
        setItems((cur) => cur.filter((x) => x.id !== bersaglio));
        setSelezione((s) => { const n = new Set(s); n.delete(bersaglio); return n; });
        if (dalDettaglio) tornaAllaGalleria();
      };

      if (pubblica) {
        const r = await api.backofficeSend(stadio, bersaglio, true);
        if (r.blocked) setEsito({ testo: `${r.error ?? "Metadati non validi."} ${(r.issues ?? []).join(" · ")}`, tipo: "errore" });
        else if (r.ok) esitoOk();
        else setEsito({ testo: r.error ?? "Invio non riuscito.", tipo: "errore" });
      } else {
        const r = await api.backofficeMove(stadio, bersaglio, prossimo);
        if (r.ok) esitoOk();
        else setEsito({ testo: r.error ?? "Spostamento non riuscito.", tipo: "errore" });
      }
    } catch (e) {
      setEsito({ testo: (e as Error).message, tipo: "errore" });
    } finally {
      setOccupato(false);
      setAzione(null);
    }
  }, [corrente, occupato, items, visibili, pubblica, prossimo, stadio, apertaId, tornaAllaGalleria]);

  /**
   * Pubblica adesso: alza il flag e mette il gruppo in coda, senza aspettare il giro di
   * sorveglianza.
   */
  const pubblicaOra = useCallback(async (id?: number) => {
    const bersaglio = id ?? corrente?.id;
    if (!bersaglio || occupato || !pubblica) return;
    const it = items.find((x) => x.id === bersaglio);

    // Stessa ragione del pulsante «Invia»: «P» non passa di lì, e forzare una consegna già in
    // corso significherebbe mandare due volte la stessa immagine al marketplace.
    if (it?.pipeline && !it.pipeline.puoForzare) {
      setEsito({ testo: `${it.fileName}: ${it.pipeline.spiega}`, tipo: "errore" });
      return;
    }

    const dalDettaglio = apertaId === bersaglio;
    setOccupato(true);
    setAzione("ora");
    try {
      const r = await api.backofficePubblicaOra(stadio, bersaglio);
      if (r.blocked) {
        setEsito({ testo: `${r.error ?? "Metadati non validi."} ${(r.issues ?? []).join(" · ")}`, tipo: "errore" });
      } else if (r.ok) {
        setApprovate((n) => n + 1);
        const quante = r.accodate ?? 0;
        setEsito({
          testo: `${it?.fileName ?? bersaglio} → in coda adesso (${quante} ${quante === 1 ? "consegna" : "consegne"}), parte entro un minuto`,
          tipo: "ok",
        });
        setItems((cur) => cur.filter((x) => x.id !== bersaglio));
        setSelezione((s) => { const n = new Set(s); n.delete(bersaglio); return n; });
        if (dalDettaglio) tornaAllaGalleria();
      } else {
        // Anche un invio parziale finisce qui: il server dichiara riuscito solo un gruppo partito
        // per intero, e il messaggio dice quante consegne sono rimaste indietro e cosa ne sarà.
        setEsito({ testo: r.error ?? "Accodamento non riuscito.", tipo: "errore" });
        ricarica();
      }
    } catch (e) {
      setEsito({ testo: (e as Error).message, tipo: "errore" });
    } finally {
      setOccupato(false);
      setAzione(null);
    }
  }, [corrente, occupato, items, pubblica, stadio, apertaId, tornaAllaGalleria]);

  /**
   * Rimette in gioco un file rimasto fermo in «in pubblicazione».
   *
   * Compare solo quando il server dice che ha senso, cioè oltre la mezz'ora: prima di allora il
   * file è probabilmente davvero in volo, e sbloccarlo lo farebbe partire due volte.
   */
  const sblocca = useCallback(async (id?: number) => {
    const bersaglio = id ?? corrente?.id;
    if (!bersaglio || occupato) return;
    const it = items.find((x) => x.id === bersaglio);
    if (!confirm(
      `Sbloccare «${it?.fileName ?? bersaglio}»?\n\n` +
      "Risulta preso in carico da più di mezz'ora senza essere arrivato a destinazione. " +
      "Sbloccandolo tornerà inviabile, e la pipeline lo riprenderà."
    )) return;

    setOccupato(true);
    setAzione("sblocca");
    try {
      const r = await api.backofficeSblocca(stadio, bersaglio);
      if (r.ok) {
        setEsito({ testo: `${it?.fileName ?? bersaglio} → sbloccato, torna inviabile`, tipo: "ok" });
        if (apertaId === bersaglio) tornaAllaGalleria();
        ricarica();
      } else {
        setEsito({ testo: r.error ?? "Sblocco non riuscito.", tipo: "errore" });
      }
    } catch (e) {
      setEsito({ testo: (e as Error).message, tipo: "errore" });
    } finally {
      setOccupato(false);
      setAzione(null);
    }
  }, [corrente, occupato, items, stadio, apertaId, tornaAllaGalleria]);

  /** Segna e basta: la cancellazione vera avviene solo dal riepilogo, con conferma. */
  const segnaScarto = useCallback((id?: number) => {
    const bersaglio = id ?? corrente?.id;
    if (!bersaglio) return;
    setDaScartare((s) => commuta(s, bersaglio));
  }, [corrente]);

  const rigenera = useCallback(async (id?: number) => {
    const bersaglio = id ?? corrente?.id;
    if (!bersaglio || occupato) return;
    setOccupato(true);
    setAzione("rigenera");
    setEsito({ testo: "Rigenerazione in corso: è una chiamata a pagamento…", tipo: "ok" });
    try {
      const r = await api.backofficeRegenerate(stadio, bersaglio);
      if (r.ok && r.item) {
        const nuovo = r.item as BackofficeItem;
        setItems((cur) => cur.map((x) => (x.id === bersaglio ? { ...x, ...nuovo } : x)));
        setEsito({ testo: "Metadati rigenerati.", tipo: "ok" });
      } else {
        setEsito({ testo: r.error ?? "Rigenerazione non riuscita.", tipo: "errore" });
      }
    } catch (e) {
      setEsito({ testo: (e as Error).message, tipo: "errore" });
    } finally {
      setOccupato(false);
      setAzione(null);
    }
  }, [corrente, occupato, stadio]);

  /**
   * Rigenera in blocco le selezionate. Il conto lo si dice prima: sono chiamate a pagamento e la
   * differenza fra tre e trecento non si vede guardando la griglia.
   */
  const rigeneraSelezionate = async () => {    const ids = [...selezione];
    if (ids.length === 0 || occupato) return;
    if (!confirm(
      `Rigenerare i metadati di ${ids.length} ${ids.length === 1 ? "immagine" : "immagini"}?\n\n` +
      "Ogni immagine è una chiamata a pagamento al modello. I metadati attuali verranno sostituiti."
    )) return;

    setOccupato(true);
    setAzione("rigenera-blocco");
    setEsito({ testo: `Rigenerazione di ${ids.length} immagini in corso…`, tipo: "ok" });
    try {
      const r = await api.backofficeRegenerateMany(stadio, ids);
      for (const res of r.results)
        if (res.ok && res.item)
          setItems((cur) => cur.map((x) => (x.id === res.id ? { ...x, ...(res.item as BackofficeItem) } : x)));
      const falliti = r.results.filter((x) => !x.ok);
      setEsito({
        testo: `Rigenerate ${r.succeeded} su ${r.requested}.` +
               (falliti.length ? ` Non riuscite: ${falliti.map((x) => x.fileName || x.id).join(", ")}.` : ""),
        tipo: falliti.length ? "errore" : "ok",
      });
    } catch (e) {
      setEsito({ testo: (e as Error).message, tipo: "errore" });
    } finally {
      setOccupato(false);
      setAzione(null);
    }
  };

  /**
   * Apre la finestra del ritracciamento e, insieme, **misura il disegno**.
   *
   * La misura serve a proporre una taratura invece di applicarne una uguale per tutti: guarda
   * quanto l'immagine e' fatta di tinte piatte, quanto sono spessi i tratti, quante tinte
   * distingue, e da li' ricava i numeri. Si chiede qui e non prima perche' costa una discesa
   * dell'originale da blob: farla a ogni immagine sfogliata vorrebbe dire pagarla anche per le
   * novantanove che non si ritracciano.
   */
  const apriFinestraTracciato = useCallback(async () => {
    if (!corrente) return;
    setFinestraTracciato(true);
    setConsigliato("attesa");
    setConsigliatiApplicati(false);
    try {
      const c = await api.tracciatoConsigliato(stadio, corrente.id);
      setConsigliato(c);
      // I valori proposti si mettono subito nel pannello: chi apre la finestra vuole il risultato
      // migliore, non un modulo da compilare. Restano tutti spostabili.
      if (c.ok && c.valori) {
        setTracciatoScelto({ ...c.valori });
        setConsigliatiApplicati(true);
      }
    } catch (e) {
      setConsigliato({ ok: false, error: (e as Error).message });
    }
  }, [corrente, stadio]);

  const applicaConsigliati = useCallback(() => {
    if (!consigliato || consigliato === "attesa" || !consigliato.valori) return;
    setTracciatoScelto({ ...consigliato.valori });
    setConsigliatiApplicati(true);
  }, [consigliato]);

  /**
   * Ritraccia SVG ed EPS di un'immagine già in libreria.
   * Non è la rigenerazione dei metadati: quella riscrive le parole chiamando il modello a
   * pagamento, questa riscrive il disegno e non costa niente in chiamate. Serve perché il
   * vettorizzatore migliora nel tempo mentre le immagini già lavorate restano com'erano.
   *
   * I parametri arrivano dalla finestra che si apre prima, quando si ritraccia una sola immagine:
   * è il momento in cui si **vede** che la taratura di serie non andava bene per quel disegno, ed
   * è l'unico in cui si può dire di meglio. Sul blocco non si chiede niente, perché una taratura
   * scelta guardando un'immagine non vale per le altre ventitré.
   */
  const rivettorializza = useCallback(async (id?: number, tracciato?: ParametriTracciato) => {
    const bersaglio = id ?? corrente?.id;
    if (!bersaglio || occupato) return;
    setOccupato(true);
    setAzione("ritraccia");
    setEsito({ testo: "Ritracciamento in corso: qualche secondo…", tipo: "ok" });
    try {
      const r = await api.backofficeRivettorializza(stadio, bersaglio, tracciato);
      if (!r.ok) {
        setEsito({ testo: r.error ?? "Ritracciamento non riuscito.", tipo: "errore" });
        return;
      }
      // L'anteprima del vettoriale arriva dallo stesso indirizzo di prima: senza un contrassegno
      // nuovo il browser mostrerebbe la copia in cache, e sembrerebbe che non sia cambiato niente.
      setRitracciate((m) => ({ ...m, [bersaglio]: Date.now() }));
      const dettaglio = (r.consegne ?? []).map((c) => `${c.tipo} ${c.kbPrima}→${c.kbDopo} KB`).join(", ");
      setEsito({
        testo: `Ritracciato ${r.aColori ? "a colori" : "in bianco e nero"}` +
               (dettaglio ? `: ${dettaglio}.` : ".") +
               (r.daRiportare ? " Già pubblicata su Adobe: il file nuovo va ricaricato là a mano." : ""),
        tipo: "ok",
      });
    } catch (e) {
      setEsito({ testo: (e as Error).message, tipo: "errore" });
    } finally {
      setOccupato(false);
      setAzione(null);
    }
  }, [corrente, occupato, stadio]);

  /**
   * Ritraccia le selezionate, una richiesta per immagine.
   *
   * Non in un'unica chiamata: un tracciato a colori è qualche secondo di CPU, e un lotto da
   * ventiquattro supererebbe i 230 secondi oltre i quali Azure chiude la connessione, perdendo il
   * lavoro a metà senza dire dove si era arrivati. Così invece si vede l'avanzamento e una
   * immagine che fallisce non ferma le altre.
   */
  const rivettorializzaSelezionate = async () => {
    const bersagli = items.filter((i) => selezione.has(i.id) && conVettoriali(i));
    if (bersagli.length === 0 || occupato) return;
    if (!confirm(
      `Ritracciare SVG ed EPS di ${bersagli.length} ${bersagli.length === 1 ? "immagine" : "immagini"}?\n\n` +
      "I vettoriali attuali vengono sovrascritti; SharePoint ne conserva le versioni precedenti. " +
      "Il JPG e i metadati non si toccano.\n\n" +
      `Servono circa ${Math.max(1, Math.ceil(bersagli.length / 6))} minuti.`
    )) return;

    setOccupato(true);
    setAzione("ritraccia-blocco");
    const falliti: string[] = [];
    let fatte = 0;
    let pubblicate = 0;
    try {
      for (const b of bersagli) {
        setEsito({ testo: `Ritracciamento ${fatte + falliti.length + 1} di ${bersagli.length}: ${b.fileName}…`, tipo: "ok" });
        try {
          const r = await api.backofficeRivettorializza(stadio, b.id);
          if (r.ok) {
            fatte++;
            if (r.daRiportare) pubblicate++;
            setRitracciate((m) => ({ ...m, [b.id]: Date.now() }));
          } else {
            falliti.push(`${b.fileName} (${r.error ?? "motivo non riportato"})`);
          }
        } catch (e) {
          falliti.push(`${b.fileName} (${(e as Error).message})`);
        }
      }
      setEsito({
        testo: `Ritracciate ${fatte} su ${bersagli.length}.` +
               (pubblicate ? ` ${pubblicate} già su Adobe: vanno ricaricate là a mano.` : "") +
               (falliti.length ? ` Non riuscite: ${falliti.join("; ")}.` : ""),
        tipo: falliti.length ? "errore" : "ok",
      });
    } finally {
      setOccupato(false);
      setAzione(null);
    }
  };

  const approvaSelezionate = async () => {
    const tutti = [...selezione];
    if (tutti.length === 0 || occupato || !prossimo) return;

    // Chi seleziona in blocco non guarda lo stato di ogni riga. Tenere fuori quelle già in viaggio
    // qui, prima di chiedere conferma, è diverso dal vederle rifiutare una per una dal server:
    // il conteggio finale torna, e si sa in anticipo quante ne partono davvero.
    const fermi = pubblica
      ? tutti.filter((id) => {
          const it = items.find((x) => x.id === id);
          return !it?.pipeline || it.pipeline.puoInviare;
        })
      : tutti;
    const inViaggio = tutti.length - fermi.length;

    if (fermi.length === 0) {
      setEsito({
        testo: `Nessuna da inviare: ${inViaggio === 1 ? "l'unica selezionata è" : `tutte e ${inViaggio} le selezionate sono`} già in viaggio.`,
        tipo: "errore",
      });
      return;
    }

    const ids = fermi;
    const quante = `${ids.length} ${ids.length === 1 ? "immagine" : "immagini"}`;
    const coda = inViaggio > 0
      ? `\n\n${inViaggio} ${inViaggio === 1 ? "è già in viaggio e resta fuori" : "sono già in viaggio e restano fuori"}.`
      : "";
    if (!confirm((pubblica
      ? `Inviare ${quante} ai marketplace? Il caricamento parte davvero.`
      : `Approvare ${quante} e passarle a «${STADI.find((s) => s.id === prossimo)?.label}»?`) + coda)) return;

    setOccupato(true);
    setAzione("approva-blocco");
    let fatte = 0;
    for (const id of ids) {
      // Il conteggio avanza a ogni file: su un lotto di cento, un'attesa muta di un minuto
      // sembrerebbe un blocco, e chi guarda ricaricherebbe la pagina a metà del lavoro.
      setEsito({ testo: `${pubblica ? "Invio" : "Approvo"}… ${fatte} di ${ids.length}`, tipo: "ok" });
      try {
        const r = pubblica
          ? await api.backofficeSend(stadio, id, true)
          : await api.backofficeMove(stadio, id, prossimo);
        if (r.ok) fatte++;
      }
      catch { /* riferito nel conteggio finale */ }
    }
    // Solo quelle riuscite lasciano l'elenco: togliere anche le altre nasconderebbe i rifiuti
    // della validazione, che sono proprio quelli da rivedere.
    setApprovate((n) => n + fatte);
    setSelezione(new Set());
    setOccupato(false);
    setAzione(null);
    ricarica();
    setEsito({
      testo: `${fatte} ${pubblica ? "inviate" : "approvate"} su ${ids.length}.`
           + (inViaggio > 0 ? ` ${inViaggio} già in viaggio, lasciate stare.` : ""),
      tipo: fatte === ids.length ? "ok" : "errore",
    });
  };

  const eliminaSegnate = async () => {
    const ids = [...daScartare];
    if (ids.length === 0) return;
    if (!confirm(
      `Eliminare definitivamente ${ids.length} ${ids.length === 1 ? "immagine" : "immagini"}?\n\n` +
      "Con le loro consegne (SVG, EPS, JPG). L'azione non è reversibile."
    )) return;

    setOccupato(true);
    setAzione("elimina");
    let tolte = 0;
    for (const id of ids) {
      setEsito({ testo: `Elimino… ${tolte} di ${ids.length}`, tipo: "ok" });
      try { if ((await api.backofficeDelete(stadio, id)).ok) tolte++; }
      catch { /* riferito nel conteggio finale */ }
    }
    setItems((cur) => cur.filter((x) => !daScartare.has(x.id)));
    setDaScartare(new Set());
    setApertaId(null);
    setOccupato(false);
    setAzione(null);
    setEsito({ testo: `${tolte} eliminate su ${ids.length}.`, tipo: tolte === ids.length ? "ok" : "errore" });
  };

  /** Scrive il campo su SharePoint e tiene allineata la copia sullo schermo. */
  const salva = async (campo: "title" | "tags", valore: string) => {
    if (!corrente) return;
    const id = corrente.id;
    setItems((cur) => cur.map((x) => (x.id === id
      ? { ...x, ...(campo === "title"
          ? { title: valore }
          : { keywords: valore.split(",").map((k) => k.trim()).filter(Boolean) }) }
      : x)));
    try {
      const r = await api.backofficeUpdate(stadio, id, { [campo]: valore });
      if (r.ok && r.item) setItems((cur) => cur.map((x) => (x.id === id ? { ...x, ...r.item } : x)));
      else if (!r.ok) setEsito({ testo: r.error ?? "Salvataggio non riuscito.", tipo: "errore" });
    } catch (e) {
      setEsito({ testo: (e as Error).message, tipo: "errore" });
    }
  };

  /**
   * Manda al journal la differenza fra quello che l'AI aveva proposto e quello che c'è adesso,
   * con la nota che la spiega. È il pezzo che chiude il cerchio: senza, le stesse correzioni si
   * ripetono su ogni immagine perché il prompt non impara mai niente.
   */
  const insegna = async () => {
    if (!corrente || occupato) return;
    const orig = originali.current.get(corrente.id);
    setOccupato(true);
    setAzione("insegna");
    try {
      const r = await api.backofficeFeedback(stadio, corrente.id, {
        generatedTitle: orig?.title ?? "",
        generatedKeywords: orig?.keywords ?? "",
        title: corrente.title,
        keywords: corrente.keywords.join(", "),
        note: nota.trim() || undefined,
      });
      if (!r.ok) setEsito({ testo: r.error ?? "Registrazione non riuscita.", tipo: "errore" });
      else if (!r.recorded) setEsito({ testo: r.message ?? "Nessuna differenza da registrare.", tipo: "errore" });
      else {
        const diff = [
          r.keywordsAdded?.length ? `+${r.keywordsAdded.length}` : null,
          r.keywordsRemoved?.length ? `−${r.keywordsRemoved.length}` : null,
        ].filter(Boolean).join(" ");
        setEsito({
          testo: `Correzione registrata${diff ? ` (${diff} keyword)` : ""}. ` +
                 `${r.pending ?? 0} in tutto: la revisione del prompt si avvia da Configurazione.`,
          tipo: "ok",
        });
        setNota("");
      }
    } catch (e) {
      setEsito({ testo: (e as Error).message, tipo: "errore" });
    } finally {
      setOccupato(false);
      setAzione(null);
    }
  };

  // I tasti non devono scattare mentre si scrive in un campo: A e X sono lettere prima che comandi.
  useEffect(() => {
    const scriveInUnCampo = (t: EventTarget | null) =>
      t instanceof HTMLElement && (t.tagName === "INPUT" || t.tagName === "TEXTAREA" || t.isContentEditable);

    const onKey = (e: KeyboardEvent) => {
      if (scriveInUnCampo(e.target) || e.ctrlKey || e.metaKey || e.altKey) return;
      const k = e.key.toLowerCase();
      if (k === "escape" && apertaId !== null) { e.preventDefault(); chiudi(); return; }
      if (apertaId === null) return;
      if (k === "arrowright" || k === "arrowdown") { e.preventDefault(); vai(1); }
      else if (k === "arrowleft" || k === "arrowup") { e.preventDefault(); vai(-1); }
      else if (k === "a") { e.preventDefault(); approva(); }
      else if (k === "p") { e.preventDefault(); pubblicaOra(); }
      else if (k === "x") { e.preventDefault(); segnaScarto(); }
      else if (k === "r") { e.preventDefault(); rigenera(); }
      else if (k === " ") { e.preventDefault(); setZoom((z) => !z); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [apertaId, vai, approva, pubblicaOra, segnaScarto, rigenera]);

  /**
   * Una data leggibile, o niente.
   *
   * Serve a confrontare a colpo d'occhio le consegne di uno stesso gruppo: l'ora conta quanto il
   * giorno, perche' un ritracciamento avviene di solito lo stesso giorno del tracciato.
   */
  const quando = (s?: string) => {
    if (!s) return "";
    const d = new Date(s);
    if (Number.isNaN(d.getTime())) return "";
    return d.toLocaleString(undefined, {
      day: "2-digit", month: "2-digit", year: "2-digit", hour: "2-digit", minute: "2-digit",
    });
  };

  const classePunteggio = (n: number) => (n >= 90 ? "alto" : n >= 70 ? "medio" : "basso");

  const consegne = useMemo<Deliverable[]>(() => {
    if (!corrente) return [];
    if (corrente.deliverables?.length) return corrente.deliverables;
    // Un file solo non ha un gruppo: si presenta comunque come consegna, così il dettaglio ha
    // sempre la stessa forma e non serve un secondo caso da leggere.
    return [{ id: corrente.id, fileName: corrente.fileName, kind: "JPG", carrier: true }];
  }, [corrente]);

  /**
   * Quale consegna si guarda per prima.
   *
   * Il portatore è il JPEG, ma quando l'immagine ha un SVG è quello a dover comparire: il JPEG di
   * un vettoriale è solo un suo surrogato raster, e giudicare il tracciato da lì vuol dire vedere
   * una scala di pixel dove il vettoriale ha una curva -- e dare la colpa al tracciato.
   * Il prodotto che si vende è la curva: si guarda quella.
   */
  const scelta = consegne.find((d) => d.id === consegna)
              ?? consegne.find((d) => d.kind.toUpperCase() === "SVG")
              ?? consegne.find((d) => d.carrier)
              ?? consegne[0];
  return (
    <div className="cernita">
      <AvvisoSessioneSharePoint />
      <div className="cn-top">
        <span className="cn-title">REVISIONE</span>
        <span className="cn-counts">
          {visibili.length < items.length
            ? <>{visibili.length} di {items.length} caricate</>
            : <>{items.length} caricate{totale !== null && totale > items.length ? ` di ~${totale}` : ""}</>}
          {caricamento && " · carico…"}
          {approvate > 0 && <> · <span className="ok">{approvate} approvate</span></>}
          {daScartare.size > 0 && <> · <span className="err">{daScartare.size} da scartare</span></>}
        </span>
        <div className="cn-top-actions">
          {apertaId !== null && (
            <button className="btn small" onClick={chiudi}>← Galleria</button>
          )}
          {daScartare.size > 0 && (
            <button className="btn small danger" onClick={eliminaSegnate} disabled={occupato}>
              {azione === "elimina" ? <><Rotella /> Elimino…</> : `Elimina le ${daScartare.size} segnate`}
            </button>
          )}
          <button className="btn small" onClick={ricarica} disabled={occupato || caricamento}>
            {caricamento && !ricerca ? <><Rotella /> Carico…</> : "Ricarica"}
          </button>
        </div>
      </div>

      {esito && (
        <div className={`cn-esito ${esito.tipo}`} role="status">
          {esito.testo}
          <button className="notice-close" onClick={() => setEsito(null)} aria-label="Chiudi">×</button>
        </div>
      )}

      {!caricamento && items.length === 0 && (
        <div className="empty">Niente da rivedere in questo stadio.</div>
      )}

      {apertaId === null ? (
        <>
          {/*
            I filtri agiscono su ciò che è già stato scaricato, non sul magazzino: il punteggio non
            esiste in SharePoint, viene calcolato qui, quindi non c'è modo di chiedere alla stadio
            "dammi quelle sotto 70". Per restringere su tutto il magazzino occorre prima scorrerlo.
          */}
          <div className="cn-filtri">
            <div className="bo-stages" role="tablist" aria-label="Libreria">
              {STADI.map((s) => (
                <button
                  key={s.id}
                  role="tab"
                  aria-selected={stadio === s.id}
                  className={`bo-stage ${stadio === s.id ? "on" : ""}`}
                  onClick={() => setStadio(s.id)}
                >
                  {s.label}
                </button>
              ))}
            </div>
          </div>

          {/*
            La ricerca è passata al server. Prima filtrava le poche immagini già scaricate, il che
            la rendeva una lente su una manciata di file invece che sul magazzino; ora interroga
            l'indice di SharePoint, che guarda tutte le migliaia e cerca dentro le parole invece che
            solo all'inizio del nome.
          */}
          <div className="cn-filtri">
            {/*
              Le fasce non portano più un conteggio.
              Prima ne avevano uno, e diceva quante immagini di quella fascia c'erano *fra quelle
              già scaricate*: con il filtro passato al server quel numero somiglierebbe a un totale
              di magazzino senza esserlo, ed è il tipo di numero che porta a decisioni sbagliate.
              Il totale vero costerebbe una query per fascia a ogni caricamento.
            */}
            <div className="cn-fascia" role="group" aria-label="Filtra per punteggio">
              {FASCE.map((f) => (
                <button
                  key={f.id}
                  className={`cn-fbtn ${fascia === f.id ? "on" : ""}`}
                  onClick={() => setFascia(f.id)}
                  disabled={caricamento}
                  title={f.titolo}
                >
                  {f.label}
                </button>
              ))}
            </div>

            <input
              className="cn-cerca"
              type="search"
              placeholder="cerca in tutta la libreria, poi Invio"
              value={cerca}
              onChange={(e) => setCerca(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter") setRicerca(cerca.trim()); }}
            />
            <button className="btn small" onClick={() => setRicerca(cerca.trim())} disabled={caricamento}>
              {caricamento && ricerca ? <><Rotella /> Cerco…</> : "Cerca"}
            </button>

            <label className={`cn-solo ${soloSegnate ? "on" : ""}`}>
              <input
                type="checkbox"
                checked={soloSegnate}
                onChange={(e) => setSoloSegnate(e.target.checked)}
              />
              solo le segnate
            </label>

            {(fascia !== "tutte" || ricerca || soloSegnate) && (
              <button
                className="btn small ghost"
                onClick={() => { setFascia("tutte"); setCerca(""); setRicerca(""); setSoloSegnate(false); }}
              >
                Azzera filtri
              </button>
            )}
          </div>

          {punteggiIncompleti && (
            <div className="cn-avviso">
              Il punteggio non è ancora stato depositato su tutte le immagini di questa libreria:
              quelle che non ce l'hanno <strong>non compaiono in questo filtro</strong>. Si riempie da
              sé man mano che le immagini vengono viste, oppure tutto insieme dal riempimento.
            </div>
          )}

          {ricerca && (
            <div className="cn-avviso">
              Risultati per «<strong>{ricerca}</strong>» dall'indice di SharePoint. Un file appena
              modificato può comparire con qualche minuto di ritardo: l'indice si aggiorna per conto suo.
            </div>
          )}

          {selezione.size > 0 && (
            <div className="gl-bulk">
              <span className="gl-bulk-n">{selezione.size} selezionate</span>
              <button className="btn small" onClick={rigeneraSelezionate} disabled={occupato}>
                {azione === "rigenera-blocco"
                  ? <><Rotella /> Rigenero…</>
                  : <>Rigenera metadati <span className="cn-cost">(a pagamento)</span></>}
              </button>
              {items.some((i) => selezione.has(i.id) && conVettoriali(i)) && (
                <button className="btn small" onClick={rivettorializzaSelezionate} disabled={occupato}
                        title="Rifà SVG ed EPS con il vettorizzatore corrente. Metadati e JPG restano come sono.">
                  {azione === "ritraccia-blocco"
                    ? <><Rotella /> Ritraccio…</>
                    : "Ritraccia vettoriali"}
                </button>
              )}
              {prossimo && <button className="btn small" onClick={approvaSelezionate} disabled={occupato}>{azione === "approva-blocco" ? <><Rotella /> {pubblica ? "Invio…" : "Approvo…"}</> : (pubblica ? "Invia ai marketplace" : "Approva")}</button>}
              <button
                className="btn small danger"
                onClick={() => setDaScartare((s) => new Set([...s, ...selezione]))}
                disabled={occupato}
              >
                Segna per lo scarto
              </button>
              <button className="btn small ghost" onClick={() => setSelezione(new Set())}>Deseleziona</button>
            </div>
          )}

          {/*
            Prima caricata: la griglia è vuota e senza un segno sembrerebbe che la libreria non
            contenga niente. Dalla seconda in poi la griglia c'è già, e basta spegnerla un poco.
          */}
          {caricamento && items.length === 0 && (
            <>
              <Attesa
                testo={ricerca ? `Cerco «${ricerca}» nell'indice di SharePoint…` : "Leggo la libreria…"}
                nota={ricerca ? "La ricerca interroga tutte le migliaia di file, non solo quelli già scaricati." : undefined}
              />
              <Segnaposto righe={4} />
            </>
          )}

          <div className={`gl-grid ${caricamento && items.length > 0 ? "attende" : ""}`}>
            {visibili.map((it) => (
              <article
                key={it.id}
                className={`gl-card ${daScartare.has(it.id) ? "ko" : ""} ${selezione.has(it.id) ? "sel" : ""}`}
              >
                <label className="gl-pick">
                  <input
                    type="checkbox"
                    checked={selezione.has(it.id)}
                    onChange={() => setSelezione((s) => commuta(s, it.id))}
                    aria-label={`Seleziona ${it.fileName}`}
                  />
                </label>

                <button className="gl-shot" onClick={() => apri(it.id)} title={it.fileName}>
                  <AuthImage src={it.previewUrl} alt={it.title || it.fileName} />
                </button>

                <div className="gl-meta">
                  <span className="gl-name" title={it.fileName}>{it.fileName}</span>
                  <span className={`bo-score ${it.validation.score >= 90 ? "ok" : it.validation.score >= 70 ? "warn" : "bad"}`}>
                    {it.validation.score}
                  </span>
                </div>
                <div className="gl-kinds">
                  {(it.deliverables ?? []).map((d) => (
                    <span key={d.id} className={`bo-kind ${d.carrier ? "carrier" : ""}`}>{d.kind}</span>
                  ))}
                  {daScartare.has(it.id) && <span className="gl-flag">segnata</span>}
                </div>
              </article>
            ))}
          </div>

          {visibili.length === 0 && items.length > 0 && (
            <div className="empty">
              Nessuna immagine corrisponde ai filtri, fra le {items.length} caricate.
            </div>
          )}

          <div ref={fondo} className="gl-more">
            {caricamento && items.length > 0 && <><Rotella /> carico altre…</>}
            {!caricamento && altre && (
              <button className="btn small" onClick={() => leggi(token, false)}>Carica altre</button>
            )}
            {!altre && items.length > 0 && "fine dell'elenco"}
          </div>
        </>
      ) : corrente ? (
        <>
          <div className={`cn-body ${zoom ? "zoom" : ""}`}>
            <div className={`cn-stage ${daScartare.has(corrente.id) ? "segnata" : ""}`}>
              <AnteprimaConsegna consegna={scelta} anteprima={corrente.previewUrl} originale={corrente.fileUrl} grande={zoom} ritracciataIl={ritracciate[corrente.id]} />
              {daScartare.has(corrente.id) && <div className="cn-mark">segnata per lo scarto · X per annullare</div>}
            </div>

            <aside className="cn-side">
              <div className={`cn-score ${classePunteggio(corrente.validation.score)}`}>
                <span className="cn-score-n">{corrente.validation.score}</span>
                <span className="cn-score-l">ADOBE + FREEPIK</span>
              </div>

              <div className="cn-file">{corrente.fileName}</div>

              {/*
                Dove si trova l'immagine lungo la pipeline. Lo stato lo decide il server: qui si
                mostra e basta. Prima non c'era, e la differenza fra "pronto" e "gia' mandato, sto
                aspettando" si poteva solo indovinare -- col risultato di premere Invia una seconda
                volta credendo che la prima non avesse funzionato.
              */}
              {corrente.pipeline && (
                <div className={`cn-stato st-${corrente.pipeline.stato}`}>
                  <strong>{corrente.pipeline.etichetta}</strong>
                  <span>{corrente.pipeline.spiega}</span>
                  {corrente.stato && <em title={corrente.stato}>{corrente.stato}</em>}
                </div>
              )}

              {/*
                Lo scarico c'era solo per i formati che il browser non sa disegnare, come se gli
                altri non servisse portarseli via. Ma il momento in cui si vuole un file sul disco
                e' proprio questo: si sta guardando quell'immagine. Qui ci sono tutti, con la
                dimensione dichiarata dal formato, cosi' si sceglie sapendo cosa si prende.
              */}
              <label className="cn-lab">SCARICA</label>
              <div className="cn-scarica">
                {consegne.filter((d) => d.url).map((d) => (
                  <a key={`dl-${d.id}`} className="cn-dl" href={d.url} download={d.fileName} title={d.fileName}>
                    ↓ {d.kind}
                  </a>
                ))}
                {corrente.fileUrl && !consegne.some((d) => d.url === corrente.fileUrl) && (
                  <a className="cn-dl" href={corrente.fileUrl} download={corrente.fileName} title={corrente.fileName}>
                    ↓ originale
                  </a>
                )}
              </div>

              {consegne.length > 1 && (
                <>
                  <label className="cn-lab">CONSEGNE</label>
                  <div className="cn-files">
                    {consegne.map((d) => (
                      <button
                        key={d.id}
                        className={`cn-fbtn ${d.id === scelta?.id ? "on" : ""}`}
                        onClick={() => setConsegna(d.id)}
                        title={d.fileName}
                      >
                        {d.kind}
                      </button>
                    ))}
                  </div>

                  {/*
                    Le date dicono se i vettoriali sono ancora quelli di partenza. Ritracciare
                    riscrive SVG ed EPS e lascia il JPEG com'era: senza le date l'unico modo di
                    accorgersene era aprire SharePoint e confrontare a mano.
                  */}
                  <div className="cn-date">
                    {consegne.map((d) => (
                      <div key={`dt-${d.id}`} className={`cn-data ${d.rifatto ? "rifatto" : ""}`}>
                        <span className="cn-data-k">{d.kind}</span>
                        <span className="cn-data-v" title={`creato ${quando(d.created)}`}>
                          {quando(d.modified) || "—"}
                        </span>
                        {d.rifatto && <span className="cn-data-b">ritracciato</span>}
                      </div>
                    ))}
                  </div>
                </>
              )}

              <label className="cn-lab">TITOLO · {corrente.title.length} CAR.</label>
              <input
                className="cn-input"
                defaultValue={corrente.title}
                key={`t-${corrente.id}`}
                onBlur={(e) => salva("title", e.target.value)}
              />

              <label className="cn-lab">KEYWORD · {corrente.keywords.length}</label>
              <textarea
                className="cn-input"
                rows={5}
                defaultValue={corrente.keywords.join(", ")}
                key={`k-${corrente.id}`}
                onBlur={(e) => salva("tags", e.target.value)}
              />

              <label className="cn-lab">PERCHÉ L'HAI CORRETTO</label>
              <textarea
                className="cn-input"
                rows={2}
                placeholder="es. il titolo non deve iniziare con «immagine di»"
                value={nota}
                onChange={(e) => setNota(e.target.value)}
              />
              <button className="btn small" onClick={insegna} disabled={occupato}>
                {azione === "insegna" ? <><Rotella /> Registro…</> : "Registra la correzione"}
              </button>
              <div className="cn-hint">
                Finisce nel journal che riscrive le regole del prompt: la revisione si avvia da Configurazione.
              </div>

              {corrente.validation.issues.length > 0 && (
                <ul className="cn-issues">
                  {corrente.validation.issues.slice(0, 4).map((i, n) => (
                    <li key={n} className={i.severity}>{i.message}</li>
                  ))}
                </ul>
              )}

              <div className="cn-state">{corrente.stato || "—"}</div>
            </aside>
          </div>

          {/*
            La barra dei comandi ha preso il posto del rullino.

            Il rullino serviva a sapere dove si era e a saltare altrove, ma nel dettaglio si guarda
            una immagine sola: le altre sessanta miniature erano sessanta scarichi e una fila di
            francobolli illeggibili sotto quella che conta. Per andare altrove c'è la galleria, che
            fa la stessa cosa meglio.

            I comandi restano gli stessi delle scorciatoie: chi le impara va a tastiera, chi non le
            conosce trova i pulsanti invece di doverle indovinare.
          */}
          <div className="cn-keys">
            <span className="cn-pos">{posizione + 1} di {visibili.length}</span>

            <button className="btn small" onClick={() => vai(-1)} disabled={posizione <= 0}
                    title="Immagine precedente (freccia sinistra)">
              ← Prec
            </button>
            <button className="btn small" onClick={() => vai(1)} disabled={posizione >= visibili.length - 1}
                    title="Immagine successiva (freccia destra)">
              Succ →
            </button>

            {/*
              I pulsanti seguono lo stato. Prima erano sempre gli stessi: su un file gia' mandato
              "Invia" restava li' invitante, e premerlo non faceva niente di visibile -- o peggio,
              rimetteva in coda qualcosa che era gia' in viaggio.
            */}
            {prossimo && (!pubblica || puoInviare) && (
              <button className="btn small primary" onClick={() => approva()} disabled={occupato}
                      title={pubblica
                        ? "Alza il flag Invia: la pipeline carica il gruppo sui marketplace e lo sposta fra i Pubblicati solo se l'invio riesce (A)"
                        : `Approva e passa a «${STADI.find((s) => s.id === prossimo)?.label}» (A)`}>
                {azione === "approva"
                  ? <><Rotella /> {pubblica ? "Invio…" : "Approvo…"}</>
                  : (pubblica ? "▲ Invia ai marketplace" : "✓ Approva")}
              </button>
            )}

            {/*
              La scorciatoia per chi non vuole aspettare: il flag lo guarda una Logic App che
              interroga SharePoint ogni quindici minuti, e chi sta davanti alla schermata quei
              quindici minuti li vive come un guasto. Qui il file finisce subito in coda.

              Si puo' forzare anche da «in attesa» — e' il caso per cui il pulsante esiste — ma non
              da «in pubblicazione», dove il messaggio e' gia' in coda e un secondo lo
              duplicherebbe sul marketplace.
            */}
            {pubblica && puoForzare && (
              <button className="btn small" onClick={() => pubblicaOra()} disabled={occupato}
                      title="Mette il gruppo in coda adesso, senza aspettare il giro di sorveglianza (P)">
                {azione === "ora" ? <><Rotella /> Accodo…</> : "⚡ Pubblica ora"}
              </button>
            )}

            {inViaggio && (
              <span className="cn-attesa" title={corrente.pipeline?.spiega}>
                {statoCorrente === "in-consegna"
                  ? "▲ in pubblicazione, non interrompibile"
                  : "⏳ gia' inviato, in attesa"}
              </span>
            )}

            {/*
              La via di rientro per un file rimasto incastrato: preso in carico, mai arrivato, e da
              quel momento intoccabile da chiunque -- compresa la sorveglianza, che cerca proprio i
              file senza quel contrassegno. Senza questo pulsante l'unico rimedio sarebbe aprire
              SharePoint e correggere la colonna a mano.
            */}
            {corrente.pipeline?.puoSbloccare && (
              <button className="btn small danger" onClick={() => sblocca()} disabled={occupato}
                      title="Risulta preso in carico da più di mezz'ora senza essere arrivato: lo rimette fra gli inviabili">
                {azione === "sblocca" ? <><Rotella /> Sblocco…</> : "⚠ Sblocca: fermo da troppo"}
              </button>
            )}
            <button className={`btn small ${daScartare.has(corrente.id) ? "danger" : ""}`}
                    onClick={() => segnaScarto()}
                    title="Segna per lo scarto, premi di nuovo per annullare (X)">
              {daScartare.has(corrente.id) ? "✕ Segnata" : "✕ Segna scarto"}
            </button>
            <button className="btn small" onClick={() => rigenera()} disabled={occupato}
                    title="Rigenera i metadati con l'AI (R)">
              {azione === "rigenera"
                ? <><Rotella /> Rigenero…</>
                : <>↻ Rigenera <span className="cn-cost">(a pagamento)</span></>}
            </button>
            {conVettoriali(corrente) && (
              <button className="btn small" onClick={() => void apriFinestraTracciato()} disabled={occupato}
                      title="Rifà SVG ed EPS dal JPG: si scelgono prima i parametri; metadati e JPG restano come sono">
                {azione === "ritraccia" ? <><Rotella /> Ritraccio…</> : "⟳ Ritraccia vettoriali…"}
              </button>
            )}
            <button className="btn small" onClick={() => setZoom((z) => !z)}
                    title="Ingrandisci a tutta larghezza (spazio)">
              {zoom ? "⤡ Riduci" : "⤢ Ingrandisci"}
            </button>
            <button className="btn small ghost" onClick={chiudi} title="Torna alla galleria (Esc)">
              Galleria
            </button>

            <span className="cn-scorc">
              <kbd>← →</kbd><kbd className="hot">A</kbd><kbd>X</kbd><kbd>R</kbd><kbd>spazio</kbd><kbd>Esc</kbd>
            </span>
          </div>
        </>
      ) : null}

      {finestraTracciato && corrente && (
        <div className="modal-backdrop" role="presentation" onClick={() => setFinestraTracciato(false)}>
          <div
            className="modal tracciato-modale"
            role="dialog"
            aria-modal="true"
            aria-labelledby="ritraccia-titolo"
            onClick={(e) => e.stopPropagation()}
          >
            <h3 id="ritraccia-titolo">Ritraccia «{corrente.fileName}»</h3>
            <p className="muted small">
              SVG ed EPS vengono rifatti dall'originale conservato, o dal JPG se l'originale non c'è
              più. I metadati e il JPG non si toccano, e SharePoint conserva le versioni precedenti
              dei file riscritti.
            </p>

            {consigliato === "attesa" && (
              <p className="muted small"><Rotella /> Guardo il disegno…</p>
            )}

            {consigliato && consigliato !== "attesa" && consigliato.ok && consigliato.misure && (
              <div className="consigliato">
                <div className="consigliato-titolo">
                  <strong>{consigliato.misure.genere}</strong>
                  <span className="muted small">
                    {consigliato.misure.tinte} tinte · tratti da {consigliato.misure.spessore} px ·
                    misurato {consigliato.sorgente === "originale" ? "sull'originale" : "sul JPG di consegna"}
                  </span>
                </div>
                <ul>{(consigliato.perche ?? []).map((r) => <li key={r}>{r}</li>)}</ul>
                {!consigliatiApplicati && (
                  <button className="btn small" onClick={applicaConsigliati}>
                    Usa i valori consigliati
                  </button>
                )}
              </div>
            )}

            {consigliato && consigliato !== "attesa" && !consigliato.ok && (
              <p className="muted small">
                Non sono riuscito a misurare il disegno ({consigliato.error}): restano i valori
                predefiniti.
              </p>
            )}

            <PannelloTracciato
              valore={tracciatoScelto}
              onChange={setTracciatoScelto}
              disabilitato={occupato}
            />
            <div className="modal-actions">
              <button className="btn ghost" onClick={() => setFinestraTracciato(false)} disabled={occupato}>
                Annulla
              </button>
              <button
                className="btn"
                disabled={occupato}
                onClick={() => {
                  setFinestraTracciato(false);
                  void rivettorializza(corrente.id, tracciatoScelto);
                }}
              >
                {occupato ? <><Rotella /> Ritraccio…</> : "Ritraccia"}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

/** Estensioni che un browser sa disegnare da sé. L'EPS non è fra queste, e non lo sarà mai. */
const VISIBILI = new Set(["JPG", "JPEG", "PNG", "WEBP", "SVG", "GIF"]);

/**
 * Se l'immagine ha vettoriali accanto a sé, cioè se c'è qualcosa da ritracciare.
 *
 * Un file arrivato da solo, senza SVG né EPS, non è una consegna vettoriale: offrirgli il comando
 * porterebbe soltanto a un errore dopo il clic.
 */
const conVettoriali = (i: BackofficeItem) =>
  (i.deliverables ?? []).some((d) => d.kind === "SVG" || d.kind === "EPS");

/**
 * Mostra la consegna scelta.
 *
 * L'SVG viene servito com'è e il browser lo disegna: è l'unico modo di accorgersi che il tracciato
 * ha mangiato un dettaglio, cosa che la miniatura del JPG non direbbe mai. L'EPS invece nessun
 * browser lo apre: dirlo apertamente e offrire lo scarico è più onesto di un riquadro vuoto.
 */
function AnteprimaConsegna(
  { consegna, anteprima, originale, grande, ritracciataIl }:
  { consegna?: Deliverable; anteprima: string; originale?: string; grande: boolean; ritracciataIl?: number }
) {
  if (!consegna) return <div className="noimg">nessuna consegna</div>;

  const kind = consegna.kind.toUpperCase();
  if (!VISIBILI.has(kind)) {
    return (
      <div className="cn-nofile">
        <div className="cn-nofile-k">{kind}</div>
        <p>
          {consegna.fileName}
          <br />
          Nessun browser sa disegnare questo formato: si controlla scaricandolo.
        </p>
        {consegna.url && (
          <a className="btn small" href={consegna.url} download={consegna.fileName}>
            Scarica
          </a>
        )}
      </div>
    );
  }

  // Il vettoriale va preso intero, altrimenti non c'è niente da guardare. Il raster invece riusa la
  // miniatura della galleria, che il browser ha già in cache: chiedere subito l'originale farebbe
  // scaricare centinaia di kilobyte per una differenza che quasi sempre non serve. L'originale
  // arriva con lo zoom, che è il momento in cui lo si sta davvero cercando.
  const src = kind === "SVG"
    ? (consegna.url ?? anteprima)
    : grande ? (originale ?? anteprima) : anteprima;

  // Dopo un ritracciamento il file è cambiato ma l'indirizzo no, e il browser mostrerebbe la copia
  // che ha già. Il contrassegno lo obbliga a richiederlo: senza, l'unico modo di vedere il lavoro
  // appena fatto sarebbe svuotare la cache a mano.
  const indirizzo = ritracciataIl
    ? src + (src.includes("?") ? "&" : "?") + "rv=" + ritracciataIl
    : src;

  // Un SVG di silhouette è fatto di tracciati neri su fondo trasparente: su un'interfaccia scura
  // vuol dire nero su nero, cioè un riquadro che sembra vuoto mentre il file è perfetto. Il fondo
  // bianco non è una preferenza estetica, è la condizione per vedere quel che si sta giudicando --
  // ed è anche il fondo su cui il vettoriale verrà davvero usato.
  if (kind === "SVG") {
    return (
      <div className="cn-svg">
        <AuthImage src={indirizzo} alt={consegna.fileName} />
      </div>
    );
  }

  return <AuthImage src={indirizzo} alt={consegna.fileName} />;
}

