import { useCallback, useEffect, useState } from "react";
import { sessioneSharePoint, type StatoSessione } from "../sessioneSharePoint";

/** Tiene sott'occhio la sessione SharePoint e la riprova quando serve. */
export function useSessioneSharePoint(provaSubito = false) {
  const [stato, setStato] = useState<StatoSessione>(sessioneSharePoint.stato());
  const [inCorso, setInCorso] = useState(false);

  useEffect(() => {
    const stacca = sessioneSharePoint.abbonati(() => setStato(sessioneSharePoint.stato()));
    if (provaSubito && sessioneSharePoint.stato() === "ignoto") sessioneSharePoint.prova();
    return stacca;
  }, [provaSubito]);

  const accedi = useCallback(async () => {
    setInCorso(true);
    try { await sessioneSharePoint.accedi(); }
    finally { setInCorso(false); }
  }, []);

  return { stato, inCorso, accedi, prova: () => sessioneSharePoint.prova() };
}

/**
 * Il pulsante che stabilisce la sessione SharePoint.
 *
 * Esiste perché le anteprime non passano più dall'API: le chiede il browser, e senza sessione
 * restano riquadri vuoti senza che nulla dica perché. Il pulsante è sempre disponibile -- una
 * sessione può scadere in qualsiasi momento e nessuno deve andarla a cercare -- ma si fa notare
 * soltanto quando serve davvero.
 */
export default function AccessoSharePoint({ compatto = false }: { compatto?: boolean }) {
  const { stato, inCorso, accedi } = useSessioneSharePoint(true);

  if (compatto && stato === "presente") return null;

  const etichetta = inCorso ? "Attendo l'accesso…"
    : stato === "assente" ? "Accedi a SharePoint"
    : stato === "presente" ? "Sessione SharePoint attiva"
    : "Verifica accesso SharePoint";

  return (
    <button
      className={`btn small ${stato === "assente" ? "primary" : "ghost"} sp-accesso ${stato}`}
      onClick={accedi}
      disabled={inCorso}
      title={stato === "presente"
        ? "Il browser vede le immagini di SharePoint. Premi per rifare l'accesso se dovessero sparire."
        : "Le anteprime arrivano da SharePoint con la tua sessione: senza accesso restano vuote."}
    >
      {stato === "presente" ? "🔓" : "🔒"} {etichetta}
    </button>
  );
}

/**
 * La fascia che compare quando le immagini non si vedono.
 *
 * Un riquadro vuoto non dice niente; ripetuto quaranta volte sembra un'applicazione rotta. Questa
 * fascia dice cosa manca e come rimediare, nello stesso posto in cui il problema si manifesta.
 */
export function AvvisoSessioneSharePoint() {
  const { stato, inCorso, accedi } = useSessioneSharePoint(true);
  if (stato !== "assente") return null;

  return (
    <div className="sp-avviso">
      <strong>Le anteprime non si vedono perché manca l'accesso a SharePoint.</strong> Le immagini
      non passano da questa applicazione: le chiede il browser direttamente alla libreria, con la
      tua sessione. È il motivo per cui la galleria è veloce, ed è anche l'unica cosa che può
      mancare.
      <button className="btn small primary" onClick={accedi} disabled={inCorso}>
        {inCorso ? "Attendo l'accesso…" : "Accedi a SharePoint"}
      </button>
    </div>
  );
}
