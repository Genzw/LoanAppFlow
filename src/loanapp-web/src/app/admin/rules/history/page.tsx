"use client";
import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { api, describe, Head, Revision } from "../../../../features/policies/api";
import Simulator from "../../../../features/policies/Simulator";

type HistoryRow = { id: string; baseRevisionId: string | null; publishedAtUtc: string; isActive: boolean };

export default function HistoryPage() {
  const router = useRouter();
  const [rows, setRows] = useState<HistoryRow[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(true);
  const [selected, setSelected] = useState<Revision | null>(null);

  async function load(next?: string) {
    setBusy(true);
    setError("");
    try {
      const result = await api<{ items: HistoryRow[]; nextCursor: string | null }>(
        `policy/revisions${next ? `?cursor=${encodeURIComponent(next)}` : ""}`
      );
      setRows(current => (next ? [...current, ...result.data.items] : result.data.items));
      setCursor(result.data.nextCursor);
    } catch (e) {
      setError(describe(e));
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    void load();
  }, []);

  return (
    <main className="workspace">
      <header>
        <Link className="brand" href="/">LoanAppFlow</Link>
        <nav>
          <Link href="/">Home</Link>
          <Link href="/admin/rules">Policy Rules</Link>
          <Link aria-current="page" href="/admin/rules/history" className="active">Version History</Link>
          <Link href="/admin/applications">Deliveries</Link>
          <Link href="/admin/activity">Audit</Link>
        </nav>
      </header>

      <div className="workspace-heading">
        <span className="eyebrow">Immutable Snapshots</span>
        <h1>Policy Version History</h1>
        <p className="description">
          Restoring an earlier version safely creates a new draft without altering live underwriting. Active criteria change only after explicit publication.
        </p>
      </div>

      {error && (
        <section className="notice error" role="alert" style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <p>{error}</p>
          <button className="secondary" disabled={busy} onClick={() => load()} style={{ width: "auto" }}>
            Retry
          </button>
        </section>
      )}

      {busy && !rows.length && <p role="status" style={{ color: "var(--text-secondary)" }}>Loading history timeline...</p>}

      {!busy && !error && rows.length === 0 && (
        <div className="panel" style={{ textAlign: "center", padding: "40px", color: "var(--text-muted)" }}>
          No published policy snapshots recorded yet.
        </div>
      )}

      <div className="history-list" style={{ display: "grid", gap: "16px" }}>
        {rows.map(row => (
          <article className="panel" key={row.id} style={{ margin: 0 }}>
            <div className="panel-heading" style={{ margin: 0 }}>
              <div>
                <div style={{ display: "flex", alignItems: "center", gap: "10px" }}>
                  <h2 style={{ fontSize: "16px" }}>
                    {new Date(row.publishedAtUtc).toLocaleString("en-US", { timeZone: "UTC", dateStyle: "medium", timeStyle: "short" })} UTC
                  </h2>
                  {row.isActive && <span className="badge">Active Policy</span>}
                </div>
                <code style={{ fontSize: "12px", color: "var(--text-muted)", marginTop: "4px", display: "inline-block" }}>
                  {row.id}
                </code>
              </div>

              <div className="button-row" style={{ margin: 0 }}>
                <button
                  className="secondary"
                  disabled={busy}
                  onClick={async () => {
                    setBusy(true);
                    setError("");
                    try {
                      setSelected((await api<Revision>(`policy/revisions/${row.id}`)).data);
                    } catch (e) {
                      setError(describe(e));
                    } finally {
                      setBusy(false);
                    }
                  }}
                >
                  Inspect & Simulate
                </button>

                <button
                  disabled={busy}
                  onClick={async () => {
                    setBusy(true);
                    setError("");
                    try {
                      const head = await api<Head>("policy");
                      await api("policy/draft", "POST", { sourceRevisionId: row.id }, head.tag);
                      router.push("/admin/rules");
                    } catch (e) {
                      setError(describe(e));
                      setBusy(false);
                    }
                  }}
                >
                  Fork into Draft
                </button>
              </div>
            </div>
          </article>
        ))}
      </div>

      {cursor && (
        <div style={{ textAlign: "center", marginTop: "24px" }}>
          <button disabled={busy} className="secondary" onClick={() => load(cursor)}>
            Load Earlier Revisions
          </button>
        </div>
      )}

      {selected && (
        <div style={{ marginTop: "40px" }}>
          <section className="panel">
            <div className="panel-heading">
              <div>
                <h2>Inspected Snapshot Content</h2>
                <code>{selected.id}</code>
              </div>
              <button className="secondary" onClick={() => setSelected(null)}>
                Close Preview
              </button>
            </div>

            <div style={{ marginTop: "16px" }}>
              <h3 style={{ fontSize: "15px", marginBottom: "10px" }}>Rules in Snapshot ({selected.rules.length})</h3>
              <ul style={{ listStyle: "none", padding: 0, display: "grid", gap: "10px" }}>
                {selected.rules.map(rule => (
                  <li key={rule.id} style={{ background: "rgba(255, 255, 255, 0.02)", padding: "12px", borderRadius: "6px", border: "1px solid var(--border-subtle)" }}>
                    <div style={{ display: "flex", justifyContent: "space-between" }}>
                      <strong>{rule.name}</strong>
                      <span className={`badge ${rule.enabled ? "" : "neutral"}`}>{rule.enabled ? "Enabled" : "Disabled"}</span>
                    </div>
                    <p style={{ fontSize: "13px", color: "var(--text-secondary)", marginTop: "4px" }}>{rule.publicMessage}</p>
                    <div style={{ fontSize: "12px", color: "var(--text-muted)", marginTop: "6px" }}>
                      Match Logic: <code>{rule.match}</code> · Priority: {rule.priority}
                    </div>
                  </li>
                ))}
              </ul>

              <div style={{ marginTop: "20px" }}>
                <h3 style={{ fontSize: "15px", marginBottom: "8px" }}>Blacklist Entries ({selected.blacklist.length})</h3>
                {selected.blacklist.length === 0 ? (
                  <p style={{ color: "var(--text-muted)", fontSize: "13px" }}>None recorded.</p>
                ) : (
                  <div style={{ display: "flex", gap: "8px", flexWrap: "wrap" }}>
                    {selected.blacklist.map(e => (
                      <code key={e.id} style={{ color: "#FDA4AF" }}>{e.maskedSsn}</code>
                    ))}
                  </div>
                )}
              </div>
            </div>
          </section>

          <Simulator key={selected.id} revision={selected} tag="" disabled={busy} />
        </div>
      )}
    </main>
  );
}
