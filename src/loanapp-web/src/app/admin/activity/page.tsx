"use client";
import { FormEvent, useEffect, useState } from "react";
import Link from "next/link";

type AuditItem = {
  id: string;
  occurredAtUtc: string;
  component: string;
  action: string;
  correlationId: string;
  actorType: string;
  actorRef: string | null;
  entityType: string;
  entityId: string | null;
  outcome: string;
  reasonCode: string | null;
  policyRevisionId: string | null;
  outboxEventId: string | null;
  metadata: Record<string, unknown>;
};

type AuditPage = { items: AuditItem[]; nextCursor: string | null };
type Filters = { fromUtc: string; toUtc: string; action: string; entityType: string; entityId: string; correlationId: string; limit: string };

const empty: Filters = { fromUtc: "", toUtc: "", action: "", entityType: "", entityId: "", correlationId: "", limit: "20" };

const labels: Record<keyof Filters, string> = {
  fromUtc: "From Timestamp (UTC, inclusive)",
  toUtc: "To Timestamp (UTC, exclusive)",
  action: "Action Code",
  entityType: "Entity Type",
  entityId: "Entity UUID",
  correlationId: "Trace / Correlation ID",
  limit: "Page Size"
};

export default function ActivityPage() {
  const [source, setSource] = useState<"Api" | "Mock">("Api");
  const [draft, setDraft] = useState<Filters>(empty);
  const [filters, setFilters] = useState<Filters>(empty);
  const [cursor, setCursor] = useState<string | null>(null);
  const [page, setPage] = useState<AuditPage | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const [selected, setSelected] = useState<AuditItem | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    setBusy(true);
    setPage(null);
    setSelected(null);
    setError("");
    const params = new URLSearchParams();
    Object.entries(filters).forEach(([key, value]) => {
      if (value) params.set(key, value);
    });
    if (cursor) params.set("cursor", cursor);

    void (async () => {
      try {
        const response = await fetch(`/api/admin/${source === "Mock" ? "external/" : ""}audit?${params}`, {
          cache: "no-store",
          signal: controller.signal
        });
        if (!response.ok) {
          throw new Error(
            response.status === 400
              ? "Invalid search filters or pagination cursor. Use valid UTC timestamps (e.g. 2026-09-18T00:00:00Z) and valid UUIDs."
              : response.status === 401
              ? "Session expired. Please sign in again to access the audit log."
              : "Could not fetch audit records. The requested service may be warming up."
          );
        }
        const data = (await response.json()) as AuditPage;
        if (active) setPage(data);
      } catch (e) {
        if (active && !controller.signal.aborted) setError(e instanceof Error ? e.message : "Connection failed.");
      } finally {
        if (active) setBusy(false);
      }
    })();

    return () => {
      active = false;
      controller.abort();
    };
  }, [source, filters, cursor, refresh]);

  function apply(event: FormEvent) {
    event.preventDefault();
    setFilters({ ...draft });
    setCursor(null);
  }

  function correlate(id: string) {
    const next = { ...empty, correlationId: id };
    setDraft(next);
    setFilters(next);
    setCursor(null);
  }

  return (
    <main className="workspace diagnostics activity">
      <header>
        <Link className="brand" href="/">LoanAppFlow</Link>
        <nav>
          <Link href="/">Home</Link>
          <Link href="/apply">Apply</Link>
          <Link href="/admin/rules">Policy Rules</Link>
          <Link href="/admin/applications">Deliveries</Link>
          <Link aria-current="page" href="/admin/activity" className="active">Audit Trail</Link>
        </nav>
      </header>

      <div className="workspace-heading">
        <span className="eyebrow">Observability & Compliance</span>
        <h1>System Activity & Audit Trail</h1>
        <p className="description">
          Committed aggregate operations across both core and mock services. Filter records by correlation ID to trace end-to-end distributed execution.
        </p>
      </div>

      <div className="button-row" aria-label="Audit Log Source" style={{ marginBottom: "24px" }}>
        {(["Api", "Mock"] as const).map(value => (
          <button
            key={value}
            aria-pressed={source === value}
            className={source === value ? "" : "secondary"}
            onClick={() => {
              setSource(value);
              setCursor(null);
            }}
          >
            {value === "Api" ? "Primary Core API" : "External Partner Mock"}
          </button>
        ))}
      </div>

      <form className="panel" onSubmit={apply}>
        <div className="panel-heading">
          <div>
            <h2>Filter Activity Records</h2>
            <p>Enter criteria to filter keyset-paginated audit entries</p>
          </div>
        </div>

        <div className="form-grid">
          {(Object.keys(empty) as (keyof Filters)[]).map(key => (
            <label key={key}>
              {labels[key]}
              {key === "limit" ? (
                <select value={draft[key]} onChange={e => setDraft({ ...draft, [key]: e.target.value })}>
                  {[20, 50, 100].map(n => (
                    <option key={n} value={n}>
                      {n} records per page
                    </option>
                  ))}
                </select>
              ) : (
                <input
                  value={draft[key]}
                  maxLength={key.endsWith("Utc") ? 40 : 80}
                  placeholder={key.endsWith("Utc") ? "2026-09-18T00:00:00Z" : undefined}
                  onChange={e => setDraft({ ...draft, [key]: e.target.value })}
                />
              )}
            </label>
          ))}
        </div>

        <div className="button-row">
          <button type="submit" disabled={busy}>
            Apply Filters
          </button>
          <button
            type="button"
            className="secondary"
            onClick={() => {
              setDraft(empty);
              setFilters({ ...empty });
              setCursor(null);
            }}
          >
            Clear Filters
          </button>
        </div>
      </form>

      {error && (
        <section className="notice error" role="alert">
          <p>{error}</p>
          <button className="secondary" disabled={busy} onClick={() => setRefresh(n => n + 1)} style={{ width: "auto" }}>
            Retry
          </button>
        </section>
      )}

      {busy && <p role="status" style={{ color: "var(--text-secondary)" }}>Querying audit store...</p>}

      {page && (
        <section className="panel" aria-label="Audit Results">
          <div className="panel-heading">
            <div>
              <h2>{source === "Api" ? "Core API Operations" : "External Mock Inbound Events"}</h2>
              <p>Ordered chronologically (most recent first). Keyset cursor pagination.</p>
            </div>
          </div>

          {!page.items.length ? (
            <p style={{ color: "var(--text-muted)", padding: "20px 0" }}>No matching activity records found.</p>
          ) : (
            <div style={{ display: "grid", gap: "12px" }}>
              {page.items.map(item => (
                <article
                  key={item.id}
                  style={{
                    background: "rgba(255, 255, 255, 0.02)",
                    padding: "16px 20px",
                    borderRadius: "8px",
                    border: "1px solid var(--border-subtle)"
                  }}
                >
                  <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", flexWrap: "wrap", gap: "8px" }}>
                    <div>
                      <h3 style={{ fontSize: "16px", color: "var(--text-primary)" }}>{item.action}</h3>
                      <div style={{ fontSize: "12px", color: "var(--text-muted)", marginTop: "4px" }}>
                        {new Date(item.occurredAtUtc).toLocaleString("en-US", { timeZone: "UTC", dateStyle: "short", timeStyle: "medium" })} UTC
                      </div>
                    </div>
                    <span className={`badge ${item.outcome === "Succeeded" ? "" : "warning"}`}>
                      {item.outcome}
                    </span>
                  </div>

                  <div style={{ display: "flex", gap: "16px", margin: "10px 0", fontSize: "13px", color: "var(--text-secondary)", flexWrap: "wrap" }}>
                    <span>Actor: <strong>{item.actorType}</strong></span>
                    <span>Entity: <strong>{item.entityType}</strong></span>
                    {item.entityId && (
                      <span>
                        Entity ID: <code>{item.entityId}</code>
                      </span>
                    )}
                  </div>

                  <div style={{ fontSize: "12px", color: "var(--text-muted)", marginBottom: "12px" }}>
                    Correlation ID: <code>{item.correlationId}</code>
                  </div>

                  <div className="button-row" style={{ margin: 0 }}>
                    <button
                      className="secondary"
                      style={{ fontSize: "12px", padding: "6px 12px" }}
                      onClick={() => setSelected(item)}
                    >
                      Inspect Metadata
                    </button>
                    <button
                      className="secondary"
                      style={{ fontSize: "12px", padding: "6px 12px" }}
                      onClick={() => correlate(item.correlationId)}
                    >
                      Trace Correlation &rarr;
                    </button>
                  </div>
                </article>
              ))}
            </div>
          )}

          <div className="button-row" style={{ marginTop: "20px" }}>
            <button className="secondary" disabled={busy} onClick={() => setRefresh(n => n + 1)}>
              Refresh Records
            </button>
            {cursor && (
              <button className="secondary" onClick={() => setCursor(null)}>
                First Page
              </button>
            )}
            {page.nextCursor && (
              <button onClick={() => setCursor(page.nextCursor)}>
                Next Page &rarr;
              </button>
            )}
          </div>
        </section>
      )}

      {selected && (
        <section className="panel" aria-label="Event Details" style={{ border: "1px solid var(--accent-emerald)" }}>
          <div className="panel-heading">
            <div>
              <h2>Audit Event Details</h2>
              <code>{selected.id}</code>
            </div>
            <button className="secondary" onClick={() => setSelected(null)}>
              Close Details
            </button>
          </div>

          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))", gap: "12px", margin: "16px 0" }}>
            {Object.entries(selected)
              .filter(([key]) => key !== "metadata")
              .map(([key, value]) => (
                <div key={key} style={{ background: "rgba(255, 255, 255, 0.02)", padding: "10px", borderRadius: "6px" }}>
                  <div style={{ fontSize: "11px", color: "var(--text-muted)", textTransform: "uppercase" }}>{key}</div>
                  <div style={{ fontSize: "13px", fontWeight: 600, color: "var(--text-primary)", marginTop: "2px", wordBreak: "break-all" }}>
                    {value == null ? "—" : String(value)}
                  </div>
                </div>
              ))}
          </div>

          <h3 style={{ fontSize: "15px", marginTop: "20px", marginBottom: "8px" }}>Sanitized Metadata Payload</h3>
          <pre
            style={{
              background: "rgba(0, 0, 0, 0.4)",
              border: "1px solid var(--border-subtle)",
              borderRadius: "8px",
              padding: "14px",
              fontSize: "12px",
              color: "#38BDF8",
              whiteSpace: "pre-wrap",
              overflowWrap: "anywhere"
            }}
          >
            {JSON.stringify(selected.metadata, null, 2)}
          </pre>
        </section>
      )}
    </main>
  );
}
