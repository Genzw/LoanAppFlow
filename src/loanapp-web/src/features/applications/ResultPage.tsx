"use client";
import Link from "next/link";
import { useApplicationSession } from "./ApplicationSession";

export default function ResultPage({ decision }: { decision: "Approved" | "Denied" }) {
  const { result } = useApplicationSession();

  return (
    <main className="workspace">
      <header>
        <Link className="brand" href="/">LoanAppFlow</Link>
        <nav>
          <Link href="/">Home</Link>
          <Link href="/apply">New Application</Link>
          <Link href="/admin/applications">Deliveries</Link>
          <Link href="/admin/activity">Audit</Link>
        </nav>
      </header>

      <div style={{ maxWidth: "680px", margin: "40px auto 0" }}>
        <section className="panel" style={{ padding: "40px", textAlign: "center" }}>
          {!result || result.decision !== decision ? (
            <div>
              <span className="badge neutral" style={{ marginBottom: "16px" }}>No Active Session</span>
              <h1 style={{ fontSize: "28px", margin: "12px 0" }}>No Decision Context Available</h1>
              <p style={{ color: "var(--text-secondary)", marginBottom: "28px" }}>
                This page requires an active submission from this browser session. Directly accessing this URL does not confirm an approval or denial.
              </p>
              <Link href="/apply" className="button secondary">
                Go to Application Form &rarr;
              </Link>
            </div>
          ) : result.decision === "Approved" ? (
            <div>
              <span className="badge" style={{ marginBottom: "16px", padding: "6px 16px", fontSize: "13px" }}>
                ✓ Application Approved
              </span>
              <h1 style={{ fontSize: "36px", margin: "12px 0 16px", color: "#F8FAFC" }}>
                Congratulations!
              </h1>
              <p style={{ color: "var(--text-secondary)", fontSize: "16px", marginBottom: "28px" }}>
                Your financing request has passed all automated risk rules and has been persisted to the database. Outbox delivery to our banking partner is processing in the background.
              </p>

              <div style={{
                background: "rgba(16, 185, 129, 0.06)",
                border: "1px solid rgba(16, 185, 129, 0.2)",
                borderRadius: "var(--radius-md)",
                padding: "20px",
                textAlign: "left",
                marginBottom: "28px"
              }}>
                <div style={{ display: "flex", justifyContent: "space-between", marginBottom: "12px" }}>
                  <span style={{ fontSize: "13px", color: "var(--text-secondary)" }}>Application Reference ID</span>
                  <span style={{ fontSize: "13px", color: "#34D399", fontWeight: 600 }}>
                    {result.operation === "Created" ? "New Record" : "Returning Customer"}
                  </span>
                </div>
                <code
                  data-testid="application-reference"
                  style={{
                    display: "block",
                    fontSize: "15px",
                    padding: "10px 14px",
                    background: "rgba(0, 0, 0, 0.3)",
                    borderRadius: "6px",
                    color: "#F8FAFC",
                    wordBreak: "break-all"
                  }}
                >
                  {result.applicationId}
                </code>
                <div style={{ marginTop: "12px", fontSize: "12px", color: "var(--text-muted)" }}>
                  Version: <strong>v{result.applicationVersion}</strong> · Aggregate ACID Committed
                </div>
              </div>

              <div className="button-row" style={{ justifyContent: "center" }}>
                <Link href="/admin/applications" className="button">
                  View Outbox Delivery Status &rarr;
                </Link>
                <Link href="/apply" className="button secondary">
                  Submit Another Application
                </Link>
              </div>
            </div>
          ) : (
            <div>
              <span className="badge danger" style={{ marginBottom: "16px", padding: "6px 16px", fontSize: "13px" }}>
                ✕ Application Declined
              </span>
              <h1 style={{ fontSize: "32px", margin: "12px 0 16px" }}>
                We Could Not Approve Your Application
              </h1>
              <p style={{ color: "var(--text-secondary)", fontSize: "15px", marginBottom: "24px" }}>
                Thank you for your submission. Based on current credit policy criteria, we are unable to approve this loan request at this time.
              </p>

              {result.reasons && result.reasons.length > 0 && (
                <div style={{
                  background: "rgba(244, 63, 94, 0.08)",
                  border: "1px solid rgba(244, 63, 94, 0.2)",
                  borderRadius: "var(--radius-md)",
                  padding: "18px 24px",
                  textAlign: "left",
                  marginBottom: "28px"
                }}>
                  <span style={{ fontSize: "12px", fontWeight: 700, textTransform: "uppercase", color: "#FB7185", letterSpacing: "1px" }}>
                    Decision Reasons
                  </span>
                  <ul style={{ margin: "10px 0 0", paddingLeft: "20px", color: "#FDA4AF", fontSize: "14px" }}>
                    {result.reasons.map(r => (
                      <li key={r.ruleId} style={{ margin: "4px 0" }}>{r.message}</li>
                    ))}
                  </ul>
                </div>
              )}

              <div className="button-row" style={{ justifyContent: "center" }}>
                <Link href="/apply" className="button secondary">
                  &larr; Return to Application Form
                </Link>
                <Link href="/admin/rules" className="button secondary">
                  Inspect Credit Policy Engine
                </Link>
              </div>
            </div>
          )}
        </section>
      </div>
    </main>
  );
}
