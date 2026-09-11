/**
 * Quale veste dell'applicazione mostrare.
 *
 * Le due vesti leggono gli stessi dati e chiamano la stessa API: cambia solo come sono disposte
 * sullo schermo. Tenere entrambe ha un costo -- ogni funzione nuova va disegnata due volte -- ma
 * cambiare l'abitudine di lavoro di qualcuno senza lasciargli una via di ritorno ne ha uno maggiore,
 * e si paga tutto il primo giorno.
 *
 * La scelta vive nel browser e non sul server: è una preferenza di chi guarda, non una
 * configurazione dell'impianto, e non ha senso che segua l'utente su un'altra macchina.
 */

const KEY = "esperienza";

export type Esperienza = "classica" | "nastro";

export function esperienzaCorrente(): Esperienza {
  try {
    return localStorage.getItem(KEY) === "nastro" ? "nastro" : "classica";
  } catch {
    // Modalità privata o storage pieno: si torna alla veste che c'è sempre stata.
    return "classica";
  }
}

export function impostaEsperienza(e: Esperienza): void {
  try {
    localStorage.setItem(KEY, e);
  } catch {
    /* la preferenza non si conserva, ma la sessione corrente funziona lo stesso */
  }
}

/**
 * Segna la veste sull'elemento radice, che è da dove il foglio di stile la legge.
 *
 * Sta sul documento e non su un contenitore React perché la veste deve raggiungere anche quello
 * che React non disegna -- lo sfondo della pagina, la barra di scorrimento, il colore di selezione
 * -- e perché così è attiva prima del primo fotogramma, senza il lampo di veste sbagliata che si
 * vedrebbe applicandola dentro un effetto.
 */
export function applicaVesteAlDocumento(e: Esperienza = esperienzaCorrente()): void {
  document.documentElement.dataset.veste = e;
}
