"use client";
import { useState } from "react";
import { api, describe, Evaluation, Revision } from "./api";

const empty = {
  firstName: "",
  lastName: "",
  companyName: "",
  line1: "",
  city: "",
  state: "",
  postalCode: "",
  ssn: "",
  requestedAmount: ""
};

const labels: Record<keyof typeof empty, string> = {
  firstName: "First Name",
  lastName: "Last Name",
  companyName: "Company Name",
  line1: "Street Address",
  city: "City",
  state: "State Code (2 letters)",
  postalCode: "ZIP Code",
  ssn: "SSN (Demo)",
  requestedAmount: "Requested Amount (USD)"
};

export default function Simulator({
  revision,
  tag,
  disabled
}: {
  revision: Revision;
  tag: string;
  disabled: boolean;
}) {
  const [form, setForm] = useState(empty);
  const [result, setResult] = useState<Evaluation | null>(null);
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);

  return (
    <section className="panel simulator" style={{ marginTop: "32px" }}>
      <div className="panel-heading">
        <div>
          <h2>Policy Simulation Workbench</h2>
          <p>
            Dry-run applicant profiles against this policy revision without writing to the database or emitting events.
          </p>
        </div>
      </div>

      {disabled && (
        <div className="notice" style={{ background: "#FFFBEB", borderColor: "#FDE68A", color: "#B45309" }}>
          ⚠️ Please save rule modifications before running simulations.
        </div>
      )}

      <form
        onSubmit={async e => {
          e.preventDefault();
          setBusy(true);
          setMessage("");
          setResult(null);
          try {
            const response = await api<Evaluation>(
              `policy/revisions/${revision.id}/simulate`,
              "POST",
              {
                firstName: form.firstName,
                lastName: form.lastName,
                companyName: form.companyName,
                ssn: form.ssn,
                requestedAmount: Number(form.requestedAmount),
                address: { line1: form.line1, line2: null, city: form.city, state: form.state, postalCode: form.postalCode }
              },
              revision.kind === "Draft" ? tag : undefined
            );
            setResult(response.data);
          } catch (error) {
            setMessage(describe(error));
          } finally {
            setBusy(false);
          }
        }}
      >
        <fieldset disabled={busy || disabled}>
          <legend className="sr-only">Simulation Parameters</legend>
          <div className="form-grid">
            {(Object.keys(empty) as (keyof typeof empty)[]).map(field => (
              <label key={field}>
                {labels[field]}
                <input
                  required
                  value={form[field]}
                  type={field === "requestedAmount" ? "number" : "text"}
                  step={field === "requestedAmount" ? "0.01" : undefined}
                  min={field === "requestedAmount" ? "0.01" : undefined}
                  maxLength={field === "state" ? 2 : field === "ssn" ? 11 : 200}
                  onChange={e => {
                    setForm({ ...form, [field]: e.target.value });
                    setResult(null);
                  }}
                />
              </label>
            ))}
          </div>

          <div className="button-row">
            <button type="submit" disabled={busy || disabled}>
              {busy ? "Evaluating Profile..." : "Run Simulation"}
            </button>
            <button
              type="button"
              className="secondary"
              disabled={busy || disabled}
              onClick={() => {
                setForm({
                  firstName: "Anna",
                  lastName: "Paz",
                  companyName: "California Ventures Inc",
                  line1: "100 Market Street",
                  city: "San Francisco",
                  state: "CA",
                  postalCode: "94105",
                  ssn: "000-00-0003",
                  requestedAmount: "10000"
                });
                setResult(null);
              }}
            >
              Load Sample Profile (CA, $10k)
            </button>
          </div>
        </fieldset>
      </form>

      {message && <p role="alert" className="notice error">{message}</p>}

      {result && !disabled && (
        <div
          role="status"
          className="simulation-result"
          style={{
            marginTop: "24px",
            padding: "20px",
            borderRadius: "var(--radius-md)",
            border: `1px solid ${result.decision === "Approved" ? "rgba(16, 185, 129, 0.3)" : "rgba(244, 63, 94, 0.3)"}`,
            background: result.decision === "Approved" ? "rgba(16, 185, 129, 0.05)" : "rgba(244, 63, 94, 0.05)"
          }}
        >
          <div style={{ display: "flex", alignItems: "center", gap: "10px", marginBottom: "8px" }}>
            <span className={`badge ${result.decision === "Approved" ? "" : "danger"}`}>
              Outcome: {result.decision}
            </span>
            <span style={{ fontSize: "12px", color: "var(--text-muted)" }}>
              Revision: <code>{result.policyRevisionId.slice(0, 8)}</code> (Draft v{result.draftVersion})
            </span>
          </div>

          <h3 style={{ fontSize: "18px", margin: "8px 0" }}>
            {result.decision === "Approved"
              ? "✓ Policy Engine would APPROVE this submission"
              : "✕ Policy Engine would DECLINE this submission"}
          </h3>

          {result.reasons.length > 0 && (
            <div style={{ marginTop: "12px" }}>
              <strong style={{ fontSize: "13px", color: "var(--text-primary)" }}>Triggered Rules:</strong>
              <ul style={{ margin: "6px 0 0", paddingLeft: "20px", fontSize: "13px", color: "var(--accent-rose)" }}>
                {result.reasons.map(reason => (
                  <li key={reason.ruleId}>{reason.message}</li>
                ))}
              </ul>
            </div>
          )}

          <details style={{ marginTop: "16px", cursor: "pointer", fontSize: "13px" }}>
            <summary style={{ color: "var(--accent-blue)", fontWeight: 600 }}>
              View detailed evaluation trace
            </summary>
            <ul style={{ margin: "12px 0 0", paddingLeft: "20px", color: "var(--text-secondary)" }}>
              {result.trace.map(trace => (
                <li key={trace.ruleId} style={{ margin: "6px 0" }}>
                  <strong>{revision.rules.find(r => r.id === trace.ruleId)?.name || trace.ruleId}:</strong>{" "}
                  <span style={{ color: !trace.enabled ? "var(--text-muted)" : trace.matched ? "var(--accent-rose)" : "var(--accent-emerald)" }}>
                    {!trace.enabled ? "Disabled" : trace.matched ? "Matched (Denies)" : "Passed"}
                  </span>
                  <ul style={{ margin: "4px 0 0", paddingLeft: "16px" }}>
                    {trace.conditions.map(c => (
                      <li key={c.index} style={{ fontSize: "12px" }}>
                        Condition {c.index + 1}: {c.matched ? "Matched" : "Did not match"}
                      </li>
                    ))}
                  </ul>
                </li>
              ))}
            </ul>
          </details>
        </div>
      )}
    </section>
  );
}
