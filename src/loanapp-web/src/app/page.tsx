"use client";
import { useState } from "react";
import Link from "next/link";

type Field = { name: string; type: string; operators: string[] };

export default function Home() {
  const [fields, setFields] = useState<Field[]>([]);
  const [status, setStatus] = useState("Not checked yet");
  const [loading, setLoading] = useState(false);

  async function check() {
    setLoading(true);
    setStatus("Querying backend API...");
    setFields([]);
    try {
      const response = await fetch("/api/admin/rule-catalog", { cache: "no-store" });
      if (!response.ok) throw new Error("API service is currently unreachable. Check your cloud deployment.");
      const data = await response.json();
      setFields(data.fields);
      setStatus("Connected · Catalog retrieved successfully");
    } catch (error) {
      setStatus(error instanceof Error ? error.message : "Connection failed.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <main>
      <header>
        <Link className="brand" href="/">LoanAppFlow</Link>
        <nav>
          <Link href="/apply">Apply for Loan</Link>
          <Link href="/admin/rules">Rule Engine</Link>
          <Link href="/admin/applications">Deliveries</Link>
          <Link href="/admin/activity">Audit Trail</Link>
          <span className="badge">Production Ready</span>
        </nav>
      </header>

      <section className="intro">
        <span className="eyebrow">Enterprise Lending Infrastructure</span>
        <h1>Automated Lending Decisions.<br />Complete Auditability.</h1>
        <p className="description">
          A resilient, distributed loan processing platform powered by <strong>.NET 10</strong>, <strong>Next.js 16</strong>, and <strong>PostgreSQL 17</strong>. Features a dynamic JSONB rule engine, transactional outbox background delivery, and immutable event tracking.
        </p>
      </section>

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))", gap: "20px", marginBottom: "32px" }}>
        <Link href="/apply" style={{ textDecoration: "none" }}>
          <div className="panel" style={{ height: "100%", margin: 0 }}>
            <span className="eyebrow">Applicant Portal</span>
            <h2 style={{ fontSize: "18px", margin: "8px 0" }}>Loan Application &rarr;</h2>
            <p style={{ fontSize: "13px", color: "var(--text-secondary)" }}>Submit new loan requests or update returning customer profiles with immediate credit decisioning.</p>
          </div>
        </Link>

        <Link href="/admin/rules" style={{ textDecoration: "none" }}>
          <div className="panel" style={{ height: "100%", margin: 0 }}>
            <span className="eyebrow">Risk Governance</span>
            <h2 style={{ fontSize: "18px", margin: "8px 0" }}>Policy Engine &rarr;</h2>
            <p style={{ fontSize: "13px", color: "var(--text-secondary)" }}>Configure credit criteria, manage blacklists, run real-time simulations, and publish atomic drafts.</p>
          </div>
        </Link>

        <Link href="/admin/applications" style={{ textDecoration: "none" }}>
          <div className="panel" style={{ height: "100%", margin: 0 }}>
            <span className="eyebrow">Outbox Pipeline</span>
            <h2 style={{ fontSize: "18px", margin: "8px 0" }}>Deliveries & Outbox &rarr;</h2>
            <p style={{ fontSize: "13px", color: "var(--text-secondary)" }}>Monitor background worker status, lease expirations, and external partner webhook deliveries.</p>
          </div>
        </Link>

        <Link href="/admin/activity" style={{ textDecoration: "none" }}>
          <div className="panel" style={{ height: "100%", margin: 0 }}>
            <span className="eyebrow">Observability</span>
            <h2 style={{ fontSize: "18px", margin: "8px 0" }}>System Audit Trail &rarr;</h2>
            <p style={{ fontSize: "13px", color: "var(--text-secondary)" }}>Explore keyset-paginated audit trails unified across API and Mock services via correlation IDs.</p>
          </div>
        </Link>
      </div>

      <section className="panel" aria-labelledby="connection">
        <div className="panel-heading">
          <div>
            <h2 id="connection">API Diagnostics & Rule Catalog</h2>
            <p role="status" aria-live="polite">{status}</p>
          </div>
          <button onClick={check} disabled={loading}>
            {loading ? "Verifying..." : "Verify API Connection"}
          </button>
        </div>

        {fields.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Field</th>
                  <th>Type</th>
                  <th>Supported Operators</th>
                </tr>
              </thead>
              <tbody>
                {fields.map(field => (
                  <tr key={field.name}>
                    <td><code>{field.name}</code></td>
                    <td>{field.type}</td>
                    <td>{field.operators.join(", ")}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <aside>
        <strong>Technical Note:</strong>
        <p>
          This demonstration environment utilizes <strong>Transactional Outbox</strong> with distributed leases in PostgreSQL to guarantee reliable event delivery. Business rules are evaluated purely as dynamic data without hardcoded logic.
        </p>
      </aside>

      <footer>
        .NET 10 · Next.js 16 · PostgreSQL 17 · Distributed Architecture
      </footer>
    </main>
  );
}
