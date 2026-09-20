"use client";
import { FormEvent, useState } from "react";
import Link from "next/link";

export default function LoginPage() {
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      const r = await fetch("/api/session", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ password })
      });
      setPassword("");
      if (r.ok) {
        window.location.assign("/");
      } else {
        setError(r.status === 401 ? "Invalid access key. Please verify credentials." : "Authentication service unavailable. Please try again later.");
        setBusy(false);
      }
    } catch {
      setPassword("");
      setError("Network error. Could not establish connection.");
      setBusy(false);
    }
  }

  return (
    <main style={{ minHeight: "85vh", display: "flex", flexDirection: "column", justifyContent: "center", alignItems: "center" }}>
      <div style={{ width: "100%", maxWidth: "440px" }}>
        <div style={{ textAlign: "center", marginBottom: "28px" }}>
          <Link className="brand" href="/" style={{ justifyContent: "center", fontSize: "24px" }}>
            LoanAppFlow
          </Link>
          <p style={{ fontSize: "14px", color: "var(--text-secondary)", marginTop: "8px" }}>
            Cloud Demo Authentication
          </p>
        </div>

        <section className="panel" style={{ padding: "36px" }}>
          <span className="eyebrow">Secure Access</span>
          <h1 style={{ fontSize: "28px", margin: "10px 0 16px" }}>Sign In</h1>
          <p style={{ fontSize: "14px", color: "var(--text-secondary)", marginBottom: "24px" }}>
            Enter your shared key to unlock full platform features including loan applications and policy management.
          </p>

          <form onSubmit={submit}>
            <label htmlFor="password">
              Access Key
              <input
                id="password"
                type="password"
                autoComplete="current-password"
                maxLength={256}
                required
                placeholder="Enter access key..."
                value={password}
                onChange={e => setPassword(e.target.value)}
                disabled={busy}
              />
            </label>

            {error && (
              <div className="notice error" role="alert" style={{ marginTop: "16px" }}>
                {error}
              </div>
            )}

            <div className="button-row">
              <button type="submit" disabled={busy} style={{ width: "100%" }}>
                {busy ? "Authenticating..." : "Sign In to Platform"}
              </button>
            </div>
          </form>
        </section>

        <footer style={{ paddingTop: "16px" }}>
          End-to-end PBKDF2 HMAC-SHA256 session encryption
        </footer>
      </div>
    </main>
  );
}
