"use client";
import { useEffect, useState } from "react";
import Link from "next/link";

type Summary = { applicationId: string; customerId: string; requestedAmount: number; version: number; policyRevisionId: string; status: string };
type Page<T> = { items: T[]; nextCursor: string | null };
type List = Page<Summary> & { customerCount: number; applicationCount: number };
type Event = {
  eventId: string;
  applicationVersion: number;
  policyRevisionId: string;
  operation: string;
  status: string;
  attemptCount: number;
  createdAtUtc: string;
  nextAttemptAtUtc: string;
  deliveredAtUtc: string | null;
  lastErrorCode: string | null;
  inProgress: boolean;
  blockedByEventId: string | null;
  blockedByVersion: number | null;
};
type Detail = {
  application: Summary;
  customer: { firstName: string; lastName: string; companyName: string; state: string; maskedSsn: string };
  events: Page<Event>;
};
type ExternalRow = {
  applicationId: string;
  eventId?: string;
  version?: number;
  applicationVersion?: number;
  operation?: string;
  maskedSsn?: string;
  companyName?: string;
  requestedAmount?: number;
  receivedAtUtc?: string;
  updatedAtUtc?: string;
};
type External = Page<ExternalRow> & { totalCount: number };

const pendingList = (data: List) => data.items.some(a => a.status === "Pending");
const pendingDetail = (data: Detail) => data.events.items.some(e => e.status === "Pending");
const noPolling = () => false;
const date = (value: string | null) =>
  value ? new Date(value).toLocaleString("en-US", { timeZone: "UTC", dateStyle: "short", timeStyle: "medium" }) + " UTC" : "—";
const money = (amount: number) => new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" }).format(amount);

function useRead<T>(path: string | null, pending: (data: T) => boolean) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    setData(null);
    setError("");
    if (!path) return;
    let active = true;
    let inFlight = false;
    let shouldPoll = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let controller: AbortController | undefined;

    async function load() {
      if (!active || inFlight) return;
      inFlight = true;
      controller = new AbortController();
      setBusy(true);
      try {
        const response = await fetch(`/api/admin/${path}`, { cache: "no-store", signal: controller.signal });
        if (!response.ok) {
          throw new Error(
            response.status === 503
              ? "Service unavailable. Background worker may be suspended or waking up."
              : `Request failed with status ${response.status}.`
          );
        }
        const next = (await response.json()) as T;
        if (active) {
          setData(next);
          setError("");
          shouldPoll = pending(next);
        }
      } catch (e) {
        if (active && !controller.signal.aborted) {
          setError(e instanceof Error ? e.message : "Network error.");
          shouldPoll = false;
        }
      } finally {
        inFlight = false;
        if (active) {
          setBusy(false);
          if (shouldPoll && !document.hidden) timer = setTimeout(load, 2000);
        }
      }
    }

    function visibility() {
      clearTimeout(timer);
      if (document.hidden) controller?.abort();
      else if (shouldPoll) void load();
    }

    document.addEventListener("visibilitychange", visibility);
    void load();
    return () => {
      active = false;
      clearTimeout(timer);
      controller?.abort();
      document.removeEventListener("visibilitychange", visibility);
    };
  }, [path, pending, refresh]);

  return { data, error, busy, reload: () => setRefresh(n => n + 1) };
}

export default function DiagnosticsPage() {
  const [cursor, setCursor] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const list = useRead<List>(`applications?limit=20${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`, pendingList);

  return (
    <main className="workspace diagnostics">
      <header>
        <Link className="brand" href="/">LoanAppFlow</Link>
        <nav>
          <Link href="/">Home</Link>
          <Link href="/apply">Apply</Link>
          <Link href="/admin/rules">Policy Rules</Link>
          <Link aria-current="page" href="/admin/applications" className="active">Outbox & Deliveries</Link>
          <Link href="/admin/activity">Audit</Link>
        </nav>
      </header>

      <div className="workspace-heading">
        <span className="eyebrow">Outbox Pipeline Diagnostics</span>
        <h1>Applications & Delivery Pipeline</h1>
        <p className="description">
          Approved applications are immediately persisted with transactional integrity. The background worker asynchronously dispatches events to the external partner receiver with distributed leases.
        </p>
      </div>

      {list.error && (
        <div className="notice error" role="alert">
          {list.error} {list.data && "(Displayed information may be out of date)"}
        </div>
      )}

      {list.data && (
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))", gap: "16px", marginBottom: "24px" }}>
          <div className="panel" style={{ margin: 0, padding: "20px" }}>
            <span style={{ fontSize: "12px", color: "var(--text-muted)", textTransform: "uppercase", fontWeight: 700 }}>Total Customers</span>
            <div style={{ fontSize: "28px", fontWeight: 800, marginTop: "6px", color: "var(--text-primary)" }}>
              {list.data.customerCount}
            </div>
          </div>
          <div className="panel" style={{ margin: 0, padding: "20px" }}>
            <span style={{ fontSize: "12px", color: "var(--text-muted)", textTransform: "uppercase", fontWeight: 700 }}>Total Applications</span>
            <div style={{ fontSize: "28px", fontWeight: 800, marginTop: "6px", color: "var(--text-primary)" }}>
              {list.data.applicationCount}
            </div>
          </div>
          <div className="panel" style={{ margin: 0, padding: "20px" }}>
            <span style={{ fontSize: "12px", color: "var(--text-muted)", textTransform: "uppercase", fontWeight: 700 }}>Delivery Mode</span>
            <div style={{ fontSize: "16px", fontWeight: 700, marginTop: "12px", color: "#34D399" }}>
              Transactional Outbox
            </div>
          </div>
        </div>
      )}

      <div className="button-row" style={{ marginBottom: "20px" }}>
        <button disabled={list.busy} onClick={list.reload}>
          {list.busy ? "Refreshing..." : "Refresh Applications"}
        </button>
        {cursor && (
          <button className="secondary" onClick={() => setCursor(null)}>
            First Page
          </button>
        )}
      </div>

      {list.data && (
        <section className="panel">
          <div className="panel-heading">
            <div>
              <h2>Originated Applications</h2>
              <p>Most recent submissions evaluated and committed locally</p>
            </div>
          </div>

          {list.data.items.length === 0 ? (
            <p style={{ color: "var(--text-muted)", padding: "20px 0" }}>No applications found on this page.</p>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Application / Customer</th>
                    <th>Requested Amount</th>
                    <th>Version / Policy</th>
                    <th>Outbox Status</th>
                    <th>Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {list.data.items.map(a => (
                    <tr key={a.applicationId}>
                      <td>
                        <code>{a.applicationId}</code>
                        <div style={{ fontSize: "12px", color: "var(--text-muted)", marginTop: "4px" }}>
                          Customer: <code>{a.customerId}</code>
                        </div>
                      </td>
                      <td style={{ fontWeight: 600, color: "var(--text-primary)" }}>{money(a.requestedAmount)}</td>
                      <td>
                        <span className="badge neutral">v{a.version}</span>
                        <div style={{ fontSize: "11px", color: "var(--text-muted)", marginTop: "4px" }}>
                          Policy: <code>{a.policyRevisionId.slice(0, 8)}</code>
                        </div>
                      </td>
                      <td>
                        <span className={`badge ${a.status === "Delivered" ? "" : a.status === "Pending" ? "warning" : "danger"}`}>
                          {a.status}
                        </span>
                      </td>
                      <td>
                        <button className="secondary" style={{ padding: "6px 12px", fontSize: "12px" }} onClick={() => setSelected(a.applicationId)}>
                          View Events
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {list.data.nextCursor && (
            <div style={{ marginTop: "16px" }}>
              <button className="secondary" onClick={() => { setCursor(list.data!.nextCursor); setSelected(null); }}>
                Next Applications Page &rarr;
              </button>
            </div>
          )}
        </section>
      )}

      {selected && <Timeline key={selected} id={selected} close={() => setSelected(null)} changed={list.reload} />}

      <ExternalView />
    </main>
  );
}

function Timeline({ id, close, changed }: { id: string; close: () => void; changed: () => void }) {
  const [cursor, setCursor] = useState<string | null>(null);
  const [retrying, setRetrying] = useState(false);
  const [message, setMessage] = useState("");
  const result = useRead<Detail>(`applications/${id}?limit=20${cursor ? `&eventsCursor=${cursor}` : ""}`, pendingDetail);

  async function retry(event: Event) {
    setRetrying(true);
    setMessage("");
    try {
      const response = await fetch(`/api/admin/outbox/${event.eventId}/retry`, { method: "POST" });
      setMessage(
        response.ok
          ? "Retry scheduled. Outbox worker will process this event in its next execution cycle."
          : response.status === 409
          ? "Event state has changed or delivery is already in flight."
          : "Could not schedule retry. Please check connection status."
      );
    } catch {
      setMessage("Could not confirm retry. Please verify network connectivity.");
    } finally {
      setRetrying(false);
      result.reload();
      changed();
    }
  }

  const data = result.data;
  return (
    <section className="panel" aria-label="Application Details" style={{ border: "1px solid var(--accent-emerald)" }}>
      <div className="panel-heading">
        <div>
          <h2>Outbox Event Timeline</h2>
          <code>{id}</code>
        </div>
        <button className="secondary" onClick={close}>
          Close Details
        </button>
      </div>

      {result.error && <p role="alert" className="notice error">{result.error}</p>}
      {message && <div role="status" className="notice success">{message}</div>}

      <div className="button-row" style={{ marginBottom: "16px" }}>
        <button className="secondary" disabled={result.busy || retrying} onClick={result.reload}>
          Refresh Events
        </button>
        {cursor && (
          <button className="secondary" onClick={() => setCursor(null)}>
            Latest Events
          </button>
        )}
      </div>

      {data && (
        <>
          <div style={{ background: "rgba(255, 255, 255, 0.03)", padding: "14px 18px", borderRadius: "8px", marginBottom: "20px" }}>
            <span style={{ fontSize: "12px", color: "var(--text-muted)", textTransform: "uppercase", fontWeight: 700 }}>Customer Information</span>
            <div style={{ fontSize: "15px", fontWeight: 600, color: "var(--text-primary)", marginTop: "4px" }}>
              {data.customer.firstName} {data.customer.lastName} · {data.customer.companyName} ({data.customer.state})
            </div>
            <div style={{ fontSize: "13px", color: "var(--text-secondary)", marginTop: "2px" }}>
              SSN: <code>{data.customer.maskedSsn}</code>
            </div>
          </div>

          {!data.events.items.length && <p style={{ color: "var(--text-muted)" }}>No outbox events recorded for this application.</p>}

          {data.events.items.map(e => (
            <article className="panel" id={`event-${e.eventId}`} key={e.eventId} style={{ background: "rgba(15, 23, 42, 0.9)", margin: "12px 0" }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: "8px" }}>
                <h3 style={{ fontSize: "16px" }}>
                  Version v{e.applicationVersion} · {e.operation}
                </h3>
                <span className={`badge ${e.status === "Delivered" ? "" : e.status === "Pending" ? "warning" : "danger"}`}>
                  {e.status}
                </span>
              </div>

              <p style={{ fontSize: "13px", color: "var(--text-secondary)", margin: "4px 0" }}>
                Event ID: <code>{e.eventId}</code> · Policy: <code>{e.policyRevisionId.slice(0, 8)}</code>
              </p>
              <p style={{ fontSize: "13px", color: "var(--text-secondary)", margin: "4px 0" }}>
                Delivery Attempts: <strong>{e.attemptCount}</strong> · Status: {e.inProgress ? "⚡ Delivery In Flight" : "Idle"}
              </p>
              <p style={{ fontSize: "12px", color: "var(--text-muted)", margin: "4px 0" }}>
                Created: {date(e.createdAtUtc)} · Delivered: {date(e.deliveredAtUtc)}
                {e.status === "Pending" && <> · Next Attempt: {date(e.nextAttemptAtUtc)}</>}
              </p>

              {e.lastErrorCode && (
                <div style={{ color: "#FB7185", fontSize: "13px", marginTop: "6px" }}>
                  Last Error: <code>{e.lastErrorCode}</code>
                </div>
              )}

              {e.blockedByEventId && (
                <p style={{ color: "var(--accent-amber)", fontSize: "13px", marginTop: "6px" }}>
                  Blocked by prior event: <code>{e.blockedByEventId}</code> (v{e.blockedByVersion})
                </p>
              )}

              {e.status !== "Delivered" && (
                <div style={{ marginTop: "12px" }}>
                  <button
                    disabled={retrying || result.busy || !!result.error || e.inProgress || !!e.blockedByEventId}
                    onClick={() => retry(e)}
                    style={{ fontSize: "12px", padding: "6px 14px" }}
                  >
                    Retry Delivery (v{e.applicationVersion})
                  </button>
                </div>
              )}
            </article>
          ))}

          {data.events.nextCursor && (
            <button className="secondary" onClick={() => setCursor(data.events.nextCursor)}>
              Load Earlier Events
            </button>
          )}
        </>
      )}
    </section>
  );
}

function ExternalView() {
  const [resource, setResource] = useState<"applications" | "receipts">("applications");
  const [cursor, setCursor] = useState<string | null>(null);
  const [enabled, setEnabled] = useState(false);
  const result = useRead<External>(
    enabled ? `external/${resource}?limit=20${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}` : null,
    noPolling
  );

  return (
    <section className="panel" aria-label="External Banking Partner System" style={{ marginTop: "40px" }}>
      <div className="panel-heading">
        <div>
          <h2>External Banking Partner Logs (Mock)</h2>
          <p>
            Direct HTTP query to the isolated Mock receiver inbox. This view verifies external delivery independently from local database state.
          </p>
        </div>
      </div>

      <div className="button-row" style={{ alignItems: "center" }}>
        <label style={{ display: "inline-flex", flexDirection: "row", alignItems: "center", gap: "10px" }}>
          Resource:
          <select
            value={resource}
            onChange={e => {
              setResource(e.target.value as typeof resource);
              setCursor(null);
              setEnabled(false);
            }}
            style={{ width: "auto" }}
          >
            <option value="applications">Processed Loan Records</option>
            <option value="receipts">Delivery Receipts</option>
          </select>
        </label>

        <button disabled={result.busy} onClick={() => { setEnabled(true); result.reload(); }}>
          {result.busy ? "Querying Partner..." : "Query External Partner"}
        </button>
      </div>

      {result.error && <p role="alert" className="notice error">Partner unreachable: {result.error}</p>}

      {result.data && (
        <div style={{ marginTop: "20px" }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: "12px" }}>
            <span className="badge">✓ Partner Online</span>
            <span style={{ fontSize: "13px", color: "var(--text-secondary)" }}>
              Total Logged: <strong>{result.data.totalCount}</strong>
            </span>
          </div>

          {!result.data.items.length && <p style={{ color: "var(--text-muted)" }}>No external records found on this page.</p>}

          <div style={{ display: "grid", gap: "12px" }}>
            {result.data.items.map(row => (
              <article key={row.eventId || row.applicationId} style={{ background: "rgba(255, 255, 255, 0.02)", padding: "16px", borderRadius: "8px", border: "1px solid var(--border-subtle)" }}>
                <div style={{ display: "flex", justifyContent: "space-between", flexWrap: "wrap", gap: "8px" }}>
                  <div>
                    <span style={{ fontSize: "12px", color: "var(--text-muted)" }}>Application ID</span>
                    <div style={{ fontWeight: 600 }}><code>{row.applicationId}</code></div>
                  </div>
                  {row.eventId && (
                    <div>
                      <span style={{ fontSize: "12px", color: "var(--text-muted)" }}>Receipt Event ID</span>
                      <div><code>{row.eventId}</code></div>
                    </div>
                  )}
                </div>

                <div style={{ display: "flex", gap: "20px", marginTop: "8px", fontSize: "13px", color: "var(--text-secondary)" }}>
                  <span>Version: <strong>v{row.version ?? row.applicationVersion}</strong></span>
                  <span>{row.companyName ?? row.operation}</span>
                  {row.maskedSsn && <span>SSN: <code>{row.maskedSsn}</code></span>}
                  {row.requestedAmount && <span style={{ color: "#34D399", fontWeight: 600 }}>{money(row.requestedAmount)}</span>}
                </div>

                <div style={{ fontSize: "11px", color: "var(--text-muted)", marginTop: "6px" }}>
                  Received: {date(row.receivedAtUtc ?? row.updatedAtUtc ?? null)}
                </div>
              </article>
            ))}
          </div>

          <div className="button-row" style={{ marginTop: "16px" }}>
            {cursor && (
              <button className="secondary" onClick={() => setCursor(null)}>
                First External Page
              </button>
            )}
            {result.data.nextCursor && (
              <button className="secondary" onClick={() => setCursor(result.data!.nextCursor)}>
                Next External Page &rarr;
              </button>
            )}
          </div>
        </div>
      )}
    </section>
  );
}
