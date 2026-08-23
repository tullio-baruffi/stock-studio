import { useEffect, useState } from "react";

/**
 * Chi è entrato, e la via d'uscita.
 *
 * L'identità la stabilisce l'autenticazione di App Service prima che la richiesta arrivi
 * all'applicazione: non c'è nulla da chiedere e nulla da conservare, `/.auth/me` si limita a
 * riferire ciò che la piattaforma ha già deciso. Quando non risponde nessuno — un'esecuzione
 * locale, dove davanti all'app non c'è niente — il componente sparisce invece di mentire.
 */
export default function SignedInAs() {
  const [who, setWho] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    fetch("/.auth/me")
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (!alive) return;
        const principal = Array.isArray(d) ? d[0] : null;
        const claims: { typ?: string; val?: string }[] = principal?.user_claims ?? [];
        const name = claims.find((c) => c.typ === "name")?.val;
        setWho(name || principal?.user_id || null);
      })
      .catch(() => alive && setWho(null));
    return () => {
      alive = false;
    };
  }, []);

  if (!who) return null;

  return (
    <div className="whoami" role="status">
      <span className="whoami-name" title={who}>
        {who}
      </span>
      <a className="btn small ghost" href="/.auth/logout?post_logout_redirect_uri=/">
        Esci
      </a>
    </div>
  );
}
