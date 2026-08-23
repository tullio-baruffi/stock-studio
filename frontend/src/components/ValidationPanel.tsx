import { type Validation } from "../api";

function scoreClass(n: number) {
  return n >= 80 ? "good" : n >= 50 ? "mid" : "bad";
}

export default function ValidationPanel({ v }: { v: Validation }) {
  if (!v) return null;
  return (
    <div className="valid">
      <div className="valid-head">
        <span className={`vscore ${scoreClass(v.score)}`}>{v.score}</span>
        <span className="vscore-label">SEO / qualità</span>
        <span className="vsites">
          {v.sites.map((s) => (
            <span key={s.site} className={`vsite ${scoreClass(s.score)}`}>{s.site}: {s.score}</span>
          ))}
        </span>
      </div>
      {v.issues.length === 0 ? (
        <div className="vok">✓ Pronto per la pubblicazione: nessun problema rilevato.</div>
      ) : (
        <ul className="vissues">
          {v.issues.map((i, idx) => (
            <li key={idx} className={`vissue ${i.severity}`}>
              <span className="vsev">{i.severity === "error" ? "✕" : i.severity === "warning" ? "!" : "i"}</span>
              {i.message}{i.site && <span className="vfor"> ({i.site})</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
