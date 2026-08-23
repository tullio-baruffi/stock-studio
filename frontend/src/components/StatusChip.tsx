import { statusInfo } from "../api";

export default function StatusChip({ status }: { status: string }) {
  const s = statusInfo(status);
  return (
    <span className={`chip ${s.cls}`}>
      <span className="chip-icon">{s.icon}</span> {s.label}
    </span>
  );
}
