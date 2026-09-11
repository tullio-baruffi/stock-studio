/**
 * Le attese, dette a voce.
 *
 * Su piano gratuito e con SharePoint in mezzo, molte operazioni durano secondi: una ricerca
 * sull'indice, una rigenerazione, un incrocio col magazzino. Un pulsante che non risponde per
 * ventisette secondi sembra rotto, e chi guarda lo preme di nuovo -- che sulle chiamate a pagamento
 * significa pagare due volte la stessa risposta.
 *
 * Le tre forme qui sotto servono a tre attese diverse, e vale la pena distinguerle: quella dentro
 * un pulsante, quella che occupa il posto di un risultato che deve ancora arrivare, e quella che
 * accompagna una lista già visibile mentre si allunga.
 */

/** La rotella. Il colore lo prende dalla veste, come tutto il resto. */
export function Rotella({ classe = "" }: { classe?: string }) {
  return <span className={`trend-spinner ${classe}`} aria-hidden="true" />;
}

/**
 * Attesa dichiarata, con la sua spiegazione.
 *
 * Il testo non è decorativo: dire *cosa* si sta aspettando è la differenza fra un'attesa e un
 * dubbio. «Interrogo l'indice di SharePoint» si sopporta; una rotella muta no.
 */
export function Attesa({ testo, nota }: { testo: string; nota?: string }) {
  return (
    <div className="attesa" role="status" aria-live="polite">
      <Rotella />
      <span>
        {testo}
        {nota && <em className="attesa-nota">{nota}</em>}
      </span>
    </div>
  );
}

/**
 * Il posto che un risultato occuperà, mentre non c'è ancora.
 *
 * Serve dove il contenuto sostituisce la pagina: senza, la schermata resta vuota e sembra che non
 * sia successo niente. Con una forma già al suo posto, l'occhio sa dove guardare quando arriva.
 */
export function Segnaposto({ righe = 3 }: { righe?: number }) {
  return (
    <div className="segnaposto" aria-hidden="true">
      {Array.from({ length: righe }, (_, i) => <span key={i} className="segnaposto-riga" />)}
    </div>
  );
}
