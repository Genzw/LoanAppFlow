"use client";
import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { api, ApiError, describe, Field, Head, Revision, Rule, Validation } from "./api";
import RuleEditor from "./RuleEditor";
import Simulator from "./Simulator";

export default function PolicyEditor() {
  const [head, setHead] = useState<Head | null>(null);
  const [headTag, setHeadTag] = useState("");
  const [revision, setRevision] = useState<Revision | null>(null);
  const [draftTag, setDraftTag] = useState("");
  const [fields, setFields] = useState<Field[]>([]);
  const [rules, setRules] = useState<Rule[]>([]);
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [validation, setValidation] = useState<Validation | null>(null);
  const [ssn, setSsn] = useState("");
  const [remote, setRemote] = useState<Revision | null>(null);
  const [conflict, setConflict] = useState(false);

  const load = useCallback(async () => {
    const h = await api<Head>("policy");
    const catalog = await api<{ fields: Field[] }>("rule-catalog");
    const id = h.data.draftRevisionId || h.data.activeRevisionId;
    const r = id ? await api<Revision>(`policy/revisions/${id}`) : null;
    setHead(h.data);
    setHeadTag(h.tag);
    setFields(catalog.data.fields);
    setRevision(r?.data || null);
    setDraftTag(r?.tag || "");
    setRules(structuredClone(r?.data.rules || []));
    setDirty(false);
    setValidation(null);
    setRemote(null);
    setConflict(false);
  }, []);

  useEffect(() => {
    load().catch(e => setError(describe(e))).finally(() => setBusy(false));
  }, [load]);

  useEffect(() => {
    function warn(e: BeforeUnloadEvent) {
      if (dirty) {
        e.preventDefault();
        e.returnValue = "";
      }
    }
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [dirty]);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await action();
    } catch (e) {
      setError(describe(e));
      setValidation(null);
      if (
        e instanceof ApiError &&
        ["STALE_VERSION", "HEAD_CHANGED", "DRAFT_NOT_AVAILABLE", "DRAFT_ALREADY_EXISTS"].includes(e.code)
      ) {
        setConflict(true);
      }
    } finally {
      setBusy(false);
    }
  }

  function edit(next: Rule[]) {
    setRules(next);
    setDirty(true);
    setValidation(null);
  }

  const editing = revision?.kind === "Draft";

  return (
    <main className="workspace">
      <header>
        <Link className="brand" href="/">LoanAppFlow</Link>
        <nav>
          <Link href="/">Home</Link>
          <Link href="/apply">Apply</Link>
          <Link aria-current="page" href="/admin/rules" className="active">Policy Rules</Link>
          <Link href="/admin/rules/history">Version History</Link>
          <Link href="/admin/applications">Deliveries</Link>
          <Link href="/admin/activity">Audit</Link>
        </nav>
      </header>

      <div className="workspace-heading" style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", flexWrap: "wrap", gap: "16px" }}>
        <div>
          <span className="eyebrow">Risk Governance & Configuration</span>
          <h1>Credit Policy Engine</h1>
          <p className="description">
            Design, simulate, and publish automated underwriting rules. Changes take effect atomically across all incoming applications.
          </p>
        </div>
        <div>
          <span className={`badge ${editing ? "warning" : revision ? "" : "danger"}`} style={{ fontSize: "14px", padding: "6px 16px" }}>
            Status: {editing ? "Draft In Progress" : revision ? "Live Active Policy" : "No Policy Configured"}
          </span>
        </div>
      </div>

      {busy && !head && <p role="status" style={{ color: "var(--text-secondary)" }}>Loading policy configuration...</p>}

      {(error || conflict) && (
        <section className="notice error" role="alert" style={{ flexDirection: "column", alignItems: "flex-start" }}>
          <p>{error || "A concurrency conflict was detected. Your local changes are preserved."}</p>
          <div className="button-row" style={{ marginTop: "12px" }}>
            <button
              type="button"
              className="secondary"
              disabled={busy}
              onClick={() =>
                run(async () => {
                  const current = await api<Head>("policy");
                  const id = current.data.draftRevisionId || current.data.activeRevisionId;
                  if (id) {
                    const r = await api<Revision>(`policy/revisions/${id}`);
                    setRemote(r.data);
                  } else {
                    setNotice("No draft or active policy found on server.");
                  }
                })
              }
            >
              Inspect Remote Version
            </button>
            <button
              type="button"
              className="danger"
              disabled={busy}
              onClick={() => {
                if (!dirty || window.confirm("Discard local unsaved changes and reload from server?")) {
                  void run(load);
                }
              }}
            >
              Discard Local Changes & Reload
            </button>
          </div>
        </section>
      )}

      {notice && <div role="status" className="notice success">{notice}</div>}

      {remote && (
        <section className="panel remote" style={{ border: "1px solid var(--accent-blue)" }}>
          <h2>Current Server Revision</h2>
          <p style={{ color: "var(--text-secondary)", marginBottom: "16px" }}>
            Read-only comparison view. Your local working draft remains below without modification.
          </p>
          <p style={{ fontSize: "13px", color: "var(--text-muted)" }}>
            Kind: {remote.kind} · ID: <code>{remote.id}</code> · Version: v{remote.draftVersion}
          </p>
          {remote.rules.map(rule => (
            <RuleEditor key={rule.id} rule={rule} fields={fields} readOnly onChange={() => {}} onDelete={() => {}} />
          ))}
          <button className="secondary" onClick={() => setRemote(null)} style={{ marginTop: "16px" }}>
            Close Comparison
          </button>
        </section>
      )}

      {head && (
        <section className="panel policy-toolbar">
          <div className="panel-heading" style={{ margin: 0 }}>
            <div>
              <h2>{editing ? "Draft Revision Active" : "Published Policy (Immutable)"}</h2>
              <p>
                {editing
                  ? `Revision v${revision!.draftVersion}${dirty ? " · Unsaved modifications" : " · All changes saved to draft"}`
                  : "Create a draft to safely propose criteria updates without affecting live applications."}
              </p>
            </div>

            <div className="button-row" style={{ margin: 0 }}>
              {!editing ? (
                <>
                  <button
                    disabled={busy}
                    onClick={() =>
                      run(async () => {
                        await api("policy/draft", "POST", { sourceRevisionId: head.activeRevisionId }, headTag);
                        await load();
                      })
                    }
                  >
                    {revision ? "Create Draft from Active" : "Initialize New Policy"}
                  </button>
                  {revision && (
                    <button
                      className="secondary"
                      disabled={busy}
                      onClick={() =>
                        run(async () => {
                          await api("policy/draft", "POST", { sourceRevisionId: null }, headTag);
                          await load();
                        })
                      }
                    >
                      Create Empty Draft
                    </button>
                  )}
                </>
              ) : (
                <>
                  <button
                    disabled={busy || !dirty || conflict}
                    onClick={() =>
                      run(async () => {
                        const saved = await api<Revision>("policy/draft/rules", "PUT", { rules }, draftTag);
                        setRevision(saved.data);
                        setDraftTag(saved.tag);
                        setRules(structuredClone(saved.data.rules));
                        setDirty(false);
                        setValidation(null);
                        setNotice("Rule modifications saved to draft. Live production policy remains unchanged.");
                      })
                    }
                  >
                    Save Rules
                  </button>

                  <button
                    className="secondary"
                    disabled={busy || dirty || conflict}
                    onClick={() =>
                      run(async () =>
                        setValidation((await api<Validation>("policy/draft/validate", "POST", undefined, draftTag)).data)
                      )
                    }
                  >
                    Validate & Review Publication
                  </button>

                  <button
                    className="danger"
                    disabled={busy || conflict}
                    onClick={() => {
                      if (window.confirm("Discard this draft and all pending changes? Active policy will remain intact.")) {
                        void run(async () => {
                          await api("policy/draft", "DELETE", undefined, draftTag);
                          await load();
                        });
                      }
                    }}
                  >
                    Discard Draft
                  </button>
                </>
              )}
            </div>
          </div>
        </section>
      )}

      {validation && (
        <section className="panel publish-review" style={{ border: "1px solid var(--accent-emerald)" }}>
          <span className="badge" style={{ marginBottom: "12px" }}>Ready to Publish</span>
          <h2>Publication Summary</h2>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))", gap: "16px", margin: "16px 0" }}>
            <div style={{ background: "var(--bg-base)", border: "1px solid var(--border-subtle)", padding: "14px", borderRadius: "8px" }}>
              <div style={{ fontSize: "12px", color: "var(--text-muted)" }}>Rule Changes</div>
              <div style={{ fontSize: "15px", fontWeight: 600, marginTop: "4px" }}>
                +{validation.summary.rulesAdded} new, ~{validation.summary.rulesChanged} modified, -{validation.summary.rulesRemoved} removed
              </div>
            </div>
            <div style={{ background: "var(--bg-base)", border: "1px solid var(--border-subtle)", padding: "14px", borderRadius: "8px" }}>
              <div style={{ fontSize: "12px", color: "var(--text-muted)" }}>Blacklist Changes</div>
              <div style={{ fontSize: "15px", fontWeight: 600, marginTop: "4px" }}>
                +{validation.summary.blacklistAdded} added, -{validation.summary.blacklistRemoved} removed
              </div>
            </div>
          </div>

          {validation.warnings.includes("APPROVES_ALL_VALID_SUBMISSIONS") && (
            <div className="notice warning">
              ⚠️ Warning: This policy has no enabled rules and will approve all valid applications.
            </div>
          )}

          {validation.warnings.includes("BASELINE_DIFFERS") && (
            <div className="notice warning">
              ℹ️ Note: This policy differs from the initial baseline configuration.
            </div>
          )}

          <div className="button-row">
            <button
              disabled={busy}
              onClick={() =>
                run(async () => {
                  await api("policy/draft/publish", "POST", { expectedHeadVersion: validation.headVersion }, draftTag);
                  await load();
                  setNotice("Policy successfully published! Live incoming applications will now use this version.");
                })
              }
            >
              Confirm & Publish to Production
            </button>
          </div>
        </section>
      )}

      <section aria-labelledby="rules-heading" style={{ marginTop: "36px" }}>
        <div className="panel-heading">
          <div>
            <h2 id="rules-heading">
              Configured Rules <span className="badge neutral">{rules.length}</span>
            </h2>
            <p>Evaluation criteria tested sequentially by priority against applicant payloads.</p>
          </div>
          {editing && (
            <button
              className="secondary"
              disabled={busy || rules.length >= 50 || conflict}
              onClick={() => {
                const id = crypto.randomUUID();
                edit([
                  ...rules,
                  {
                    id,
                    code: `RULE_${id.slice(0, 8).toUpperCase()}`,
                    name: "New Credit Rule",
                    publicMessage: "Application does not meet current credit policy requirements.",
                    enabled: true,
                    priority: 10,
                    effect: "Deny",
                    match: "ALL",
                    conditions: [{ field: fields[0].name, operator: "equals", value: "" }]
                  }
                ]);
              }}
            >
              + Add New Rule
            </button>
          )}
        </div>

        {rules.length === 0 && (
          <div className="panel" style={{ textAlign: "center", padding: "36px", color: "var(--text-muted)" }}>
            No rules configured. An active empty policy automatically approves all valid submissions.
          </div>
        )}

        {rules.map((rule, i) => (
          <div key={rule.id} style={{ marginBottom: "16px" }}>
            <RuleEditor
              rule={rule}
              fields={fields}
              readOnly={!editing || busy || conflict}
              onChange={next => edit(rules.map((r, n) => (n === i ? next : r)))}
              onDelete={() => edit(rules.filter(r => r.id !== rule.id))}
            />
          </div>
        ))}
      </section>

      {revision && (
        <section className="panel blacklist" style={{ marginTop: "36px" }}>
          <div className="panel-heading">
            <div>
              <h2>
                Risk Blacklist <span className="badge danger">{revision.blacklist.length}</span>
              </h2>
              <p>Masked SSN records rejected automatically upon submission.</p>
            </div>
          </div>

          {editing && (
            <form
              style={{ display: "flex", gap: "12px", alignItems: "flex-end", flexWrap: "wrap", marginBottom: "20px" }}
              onSubmit={e => {
                e.preventDefault();
                void run(async () => {
                  const result = await api<{ id: string; maskedSsn: string }>("policy/draft/blacklist", "POST", { ssn }, draftTag);
                  setDraftTag(result.tag);
                  setRevision({
                    ...revision,
                    draftVersion: revision.draftVersion + 1,
                    blacklist: [...revision.blacklist, result.data]
                  });
                  setSsn("");
                  setValidation(null);
                  setNotice("SSN added to draft blacklist.");
                });
              }}
            >
              <label style={{ flex: "1", minWidth: "240px" }}>
                Fictitious SSN to Block
                <input
                  required
                  autoComplete="off"
                  value={ssn}
                  maxLength={11}
                  placeholder="000-00-0001"
                  onChange={e => setSsn(e.target.value)}
                  disabled={busy || dirty || conflict}
                />
              </label>
              <button disabled={busy || dirty || conflict}>Add to Blacklist</button>
            </form>
          )}

          {dirty && editing && (
            <p style={{ fontSize: "13px", color: "var(--accent-amber)", marginBottom: "12px" }}>
              Please save rule modifications before altering the blacklist.
            </p>
          )}

          {revision.blacklist.length === 0 ? (
            <p style={{ color: "var(--text-muted)", fontSize: "14px" }}>No blocked SSNs in this revision.</p>
          ) : (
            <ul style={{ listStyle: "none", display: "flex", flexWrap: "wrap", gap: "10px", padding: 0 }}>
              {revision.blacklist.map(entry => (
                <li
                  key={entry.id}
                  style={{
                    display: "inline-flex",
                    alignItems: "center",
                    gap: "8px",
                    background: "rgba(244, 63, 94, 0.08)",
                    border: "1px solid rgba(244, 63, 94, 0.2)",
                    padding: "6px 12px",
                    borderRadius: "6px"
                  }}
                >
                  <code style={{ color: "#FDA4AF" }}>{entry.maskedSsn}</code>
                  {editing && (
                    <button
                      className="danger"
                      type="button"
                      disabled={busy || dirty || conflict}
                      style={{ padding: "2px 8px", fontSize: "11px", height: "auto" }}
                      onClick={() =>
                        run(async () => {
                          const result = await api(`policy/draft/blacklist/${entry.id}`, "DELETE", undefined, draftTag);
                          setDraftTag(result.tag);
                          setRevision({
                            ...revision,
                            draftVersion: revision.draftVersion + 1,
                            blacklist: revision.blacklist.filter(e => e.id !== entry.id)
                          });
                          setValidation(null);
                        })
                      }
                    >
                      Remove
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>
      )}

      {revision && (
        <Simulator
          key={`${revision.id}-${revision.draftVersion}`}
          revision={revision}
          tag={draftTag}
          disabled={dirty || busy || conflict}
        />
      )}

      <footer>
        Policy changes are committed via immutable versioned snapshots. Past completed loans are never retroactively modified.
      </footer>
    </main>
  );
}
