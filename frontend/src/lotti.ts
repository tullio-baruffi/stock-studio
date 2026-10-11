/**
 * Come dividere una consegna in più richieste.
 *
 * ## Il difetto che risolve
 * La pagina mandava tutte le immagini in **una richiesta sola**, e davanti all'applicazione c'è un
 * tetto: 30.000.000 byte, il valore predefinito di IIS su App Service. Lo applica la piattaforma
 * prima del login e prima del codice, quindi il `RequestSizeLimit` scritto sul controller non lo
 * può alzare: sta più a valle. Misurato in produzione l'11/10/2026 con richieste anonime:
 *
 *     30.000.000 byte -> 401 (passa, poi chiede l'accesso)
 *     30.000.001 byte -> 413 Request Entity Too Large, da Microsoft-IIS/10.0
 *
 * Dieci immagini stavano sotto, trenta no: arrivava un 413 e nessuna entrava in pipeline.
 *
 * ## Perché qui e non solo sul server
 * Il tetto del server si può alzare, ma non lo si controlla del tutto da qui: chi lo applica è la
 * piattaforma, e il prossimo strato -- un proxy, un front door, un piano diverso -- potrebbe
 * averne un altro. Richieste piccole passano ovunque, e portano altri due vantaggi: se una fallisce
 * le altre arrivano lo stesso, e la pagina può dire a che punto è la consegna.
 */

/**
 * Quanti byte di immagini al massimo in una richiesta.
 *
 * Sei milioni sotto il tetto, per l'involucro multipart: intestazioni di ogni parte, soglie,
 * parametri del tracciato. Sono poche centinaia di byte per file, quindi il margine è largo apposta
 * -- un lotto in più costa una richiesta, un lotto troppo grande costa la consegna.
 */
export const BYTE_PER_LOTTO = 24_000_000;

/**
 * Quante immagini al massimo in una richiesta, anche se piccole.
 *
 * Il server le deposita una dopo l'altra, e il fronte di App Service chiude le richieste che
 * superano 230 secondi. Dieci è il lotto che in produzione è sempre arrivato.
 */
export const FILE_PER_LOTTO = 10;

/**
 * Il tetto misurato sul server in produzione l'11/10/2026, prima di alzarlo con web.config.
 *
 * **Non** serve a bloccare niente: il file si manda comunque, perché il tetto vero lo decide il
 * server e può cambiare (web.config ora lo alza). Serve a leggere un rifiuto. Chi rifiuta un corpo
 * troppo grande risponde 413 e chiude la connessione mentre il browser sta ancora mandando, e il
 * browser lo riporta come una rete caduta -- "Failed to fetch" -- invece che come un file troppo
 * grande. Misurato con un proxy che si comporta come IIS. Sopra questa misura, una connessione
 * caduta è con ogni probabilità quel rifiuto, e lo si dice con il suo nome.
 */
export const TETTO_SERVER_MISURATO = 30_000_000;

/**
 * Divide i file in lotti consecutivi, rispettando i due tetti.
 *
 * Restituisce le **posizioni** dei file, non i file, e nell'ordine originale: le soglie scelte
 * nell'anteprima viaggiano accanto alle immagini e il server le abbina per posizione, quindi ogni
 * lotto deve sapere quali soglie gli toccano.
 *
 * Un file più grande del tetto non si può spezzare: va in un lotto da solo, così se il server lo
 * rifiuta si rifiuta quello e basta, non i vicini.
 */
export function dividiInLotti(
  dimensioni: readonly number[],
  bytePerLotto: number = BYTE_PER_LOTTO,
  filePerLotto: number = FILE_PER_LOTTO
): number[][] {
  const lotti: number[][] = [];
  let corrente: number[] = [];
  let byte = 0;

  dimensioni.forEach((dimensione, i) => {
    const pieno =
      corrente.length > 0 && (corrente.length >= filePerLotto || byte + dimensione > bytePerLotto);
    if (pieno) {
      lotti.push(corrente);
      corrente = [];
      byte = 0;
    }
    corrente.push(i);
    byte += dimensione;
  });

  if (corrente.length > 0) lotti.push(corrente);
  return lotti;
}

/** Una voce della risposta che nomina un file, e forse dice dove stava. */
export type ConPosizione = { file: string; posizione?: number };

/**
 * Riporta all'elenco intero le posizioni che il server dà rispetto al suo lotto.
 *
 * Il server numera le immagini dentro la **sua** richiesta, e non sa che ce ne sono state altre:
 * la terza immagine del secondo lotto per lui è la numero 2. Qui torna a essere quella che era
 * nell'elenco che si è consegnato, così la risposta ricomposta parla di un elenco solo.
 *
 * Una voce senza posizione viene da un server che ancora non la riporta: si ripiega sul nome,
 * prendendo la prima posizione libera del lotto con quel nome. Sbaglia solo con due omonimi nello
 * stesso lotto -- che è esattamente il motivo per cui il server ora dà la posizione.
 *
 * Le posizioni esplicite si assegnano **prima** di qualunque ripiego, altrimenti un ripiego potrebbe
 * occupare il posto che spettava a una voce che lo dichiarava.
 */
export function posizioniGlobali<T extends ConPosizione>(
  voci: readonly T[],
  lotto: readonly number[],
  nomi: readonly string[]
): T[] {
  const esplicita = (v: ConPosizione): v is ConPosizione & { posizione: number } =>
    typeof v.posizione === "number" && Number.isInteger(v.posizione) &&
    v.posizione >= 0 && v.posizione < lotto.length;

  const usate = new Set<number>();
  for (const v of voci) if (esplicita(v)) usate.add(lotto[v.posizione]);

  return voci.map((v) => {
    if (esplicita(v)) return { ...v, posizione: lotto[v.posizione] };
    const p = lotto.find((q) => !usate.has(q) && nomi[q] === v.file);
    if (p === undefined) return { ...v, posizione: undefined };
    usate.add(p);
    return { ...v, posizione: p };
  });
}
