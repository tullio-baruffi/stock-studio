import { useEffect, useState } from "react";
import { apiUrl } from "../api";

const STORAGE_KEY = "apiKey";

/**
 * Gate shown when the backend has Security:ApiKey configured and the browser has no key yet.
 * The key lives in localStorage and travels as X-Api-Key, never in the URL.
 */
export default function ApiKeyGate({ onUnlocked }: { onUnlocked: () => void }) {
  const [needed, setNeeded] = useState(false);
  const [value, setValue] = useState("");
  const [checking, setChecking] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const probe = async (key?: string) => {
    const k = key ?? localStorage.getItem(STORAGE_KEY) ?? "";
    try {
      const r = await fetch(apiUrl("/api/health"), { headers: k ? { "X-Api-Key": k } : {} });
      return r.status !== 401;
    } catch {
      // Network failure is a different problem: do not ask for a key we cannot validate.
      return true;
    }
  };

  useEffect(() => {
    (async () => {
      setNeeded(!(await probe()));
      setChecking(false);
    })();
  }, []);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    const k = value.trim();
    if (!k) return;
    setError(null);
    if (await probe(k)) {
      localStorage.setItem(STORAGE_KEY, k);
      setNeeded(false);
      onUnlocked();
    } else {
      setError("Chiave non valida: il backend continua a rispondere 401.");
    }
  };

  if (checking || !needed) return null;

  return (
    <div className="apikey-gate" role="dialog" aria-labelledby="apikey-title">
      <form onSubmit={save}>
        <h2 id="apikey-title">Chiave API richiesta</h2>
        <p>
          Il backend è protetto da <code>Security:ApiKey</code>. Incolla la chiave per usare
          l'applicazione: resta in questo browser e viaggia nell'header <code>X-Api-Key</code>,
          mai nell'indirizzo.
        </p>
        <input
          type="password"
          autoFocus
          placeholder="Chiave API"
          value={value}
          onChange={(e) => setValue(e.target.value)}
        />
        {error && <div className="notice err" role="alert">{error}</div>}
        <div className="row" style={{ gap: 8 }}>
          <button className="btn" type="submit" disabled={!value.trim()}>Sblocca</button>
        </div>
        <small className="muted">
          Puoi leggerla da Key Vault: <code>az keyvault secret show --vault-name kv-classifier-001
          --name Security--ApiKey --query value -o tsv</code>
        </small>
      </form>
    </div>
  );
}
