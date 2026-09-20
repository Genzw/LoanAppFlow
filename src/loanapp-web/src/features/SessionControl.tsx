"use client";
import { useEffect, useState } from "react";

export default function SessionControl() {
  const [cloud, setCloud] = useState(false);
  const [error, setError] = useState("");

  useEffect(() => {
    if (window.location.pathname === "/login") return;
    let active = true;
    let timer: ReturnType<typeof setTimeout>;
    let expiresAt = 0;

    function expiry() {
      if (expiresAt && Date.now() >= expiresAt) window.location.replace("/login");
    }

    void fetch("/api/session", { cache: "no-store" })
      .then(async (r) => {
        if (!active) return;
        if (r.status === 401) {
          window.location.replace("/login");
          return;
        }
        if (!r.ok) return;
        const state = await r.json();
        if (!active) return;
        if (state.profile === "CloudDemo") {
          setCloud(true);
          expiresAt = state.expiresAt * 1000;
          timer = setTimeout(expiry, Math.max(0, expiresAt - Date.now()));
        }
      })
      .catch(() => {});

    document.addEventListener("visibilitychange", expiry);
    return () => {
      active = false;
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", expiry);
    };
  }, []);

  if (!cloud) return null;

  return (
    <aside aria-label="Cloud Demo Session" className="session-top-bar">
      <div className="session-top-content">
        <div className="session-status">
          <span className="pulse-dot"></span>
          <span className="session-title">Cloud Demo Session Active</span>
          <span className="session-role">· Authenticated Operator</span>
        </div>
        <button
          type="button"
          className="signout-btn"
          onClick={async () => {
            try {
              const r = await fetch("/api/session", { method: "DELETE" });
              if (r.ok) window.location.replace("/login");
              else setError("Failed to sign out. Please try again.");
            } catch {
              setError("Failed to sign out. Please try again.");
            }
          }}
        >
          Sign Out
        </button>
      </div>
      {error && (
        <div className="session-error-wrap">
          <p role="alert" className="session-error">{error}</p>
        </div>
      )}
    </aside>
  );
}
