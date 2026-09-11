import { useEffect, useState } from "react";
import { api } from "../api";

/**
 * La riga di numeri sempre in fondo allo schermo.
 *
 * Nei bozzetti c'è in ogni schermata, ed è la cosa che manca di più alla veste classica: lo stato
 * dell'impianto vive sparso fra Monitoraggio, Pipeline e Sistema, e per sapere se la coda si sta
 * svuotando o se la CPU gratuita è agli sgoccioli bisogna smettere di fare quello che si stava
 * facendo. Qui i quattro numeri che contano si leggono di sfuggita, senza cambiare pagina.
 *
 * Le letture sono volutamente rade: su piano F1 ogni chiamata è tempo di CPU sottratto al lavoro
 * vero, e questi numeri si muovono in minuti, non in secondi.
 */

const OGNI = 120_000;

type Stato = {
  bozze: number | null;
  pronti: number | null;
  pubblicati: number | null;
  coda: number | null;
  cpu: number | null;
};

const VUOTO: Stato = { bozze: null, pronti: null, pubblicati: null, coda: null, cpu: null };

export default function BarraStato() {
  const [s, setS] = useState<Stato>(VUOTO);

  useEffect(() => {
    let vivo = true;

    const leggi = async () => {
      // Ognuna per conto suo: se la coda non è configurata o il piano non risponde, gli altri
      // numeri devono comparire lo stesso. Un allSettled invece di un all, per questo.
      const [f, q, p] = await Promise.allSettled([api.funnel(), api.queues(), api.systemPlan()]);
      if (!vivo) return;

      const conta = (k: string) =>
        f.status === "fulfilled" && f.value.ok
          ? f.value.stages?.find((x) => x.key === k)?.count ?? null
          : null;

      const coda = q.status === "fulfilled" && q.value.configured
        ? q.value.queues.reduce<number | null>(
            (t, x) => (x.count === null ? t : (t ?? 0) + x.count), null)
        : null;

      setS({
        bozze: conta("classify"),
        pronti: conta("send"),
        pubblicati: conta("sent"),
        coda,
        cpu: p.status === "fulfilled" && p.value.ok ? p.value.cpuPercent ?? null : null,
      });
    };

    leggi();
    const t = setInterval(leggi, OGNI);
    return () => { vivo = false; clearInterval(t); };
  }, []);

  const n = (v: number | null) => (v === null ? "—" : v.toLocaleString("it-IT"));

  return (
    <div className="stato" role="status" aria-label="Stato dell'impianto">
      <span>Bozze<span className="stato-v">{n(s.bozze)}</span></span>
      <span>Pronti<span className={`stato-v ${s.pronti ? "acceso" : ""}`}>{n(s.pronti)}</span></span>
      <span>Pubblicati<span className="stato-v">{n(s.pubblicati)}</span></span>
      <span>Coda<span className={`stato-v ${s.coda ? "acceso" : ""}`}>{n(s.coda)}</span></span>
      <span>
        CPU
        {/* Oltre l'ottanta per cento della quota gratuita il sito rallenta e poi si ferma: è il
            solo numero di questa barra che chieda di fare qualcosa, e va visto prima. */}
        <span className={`stato-v ${s.cpu !== null && s.cpu >= 80 ? "caldo" : ""}`}>
          {s.cpu === null ? "—" : `${Math.round(s.cpu)}%`}
        </span>
      </span>
      <span className="stato-fine">Nastro</span>
    </div>
  );
}
