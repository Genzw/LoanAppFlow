import { NextRequest, NextResponse } from "next/server";
import { cloudConfig, readSession, sessionCookie, sessionSeconds, signSession, verifyPassword } from "../../../server/session";

export const runtime = "nodejs";
function json(data: unknown, status = 200) { return NextResponse.json(data, { status, headers: { "Cache-Control": "no-store" } }); }
export async function GET(request: NextRequest) {
  if (process.env.APP_PROFILE === "LocalDevelopment") return json({ profile: "LocalDevelopment", authenticated: true });
  if (process.env.APP_PROFILE !== "CloudDemo") return json({ code: "PROFILE_NOT_READY" }, 503);
  try { const config = cloudConfig(); const session = readSession(request.cookies.get(sessionCookie)?.value, config.key);
    return session ? json({ profile: "CloudDemo", authenticated: true, expiresAt: session.exp }) : json({ code: "SESSION_REQUIRED" }, 401);
  } catch { return json({ code: "PROFILE_NOT_READY" }, 503); }
}
export async function POST(request: NextRequest) {
  if (process.env.APP_PROFILE !== "CloudDemo") return json({ code: "PROFILE_NOT_READY" }, 503);
  try {
    const config = cloudConfig();
    if (request.headers.get("origin") !== config.origin) return json({ code: "ORIGIN_NOT_ALLOWED" }, 403);
    if (!request.headers.get("content-type")?.toLowerCase().startsWith("application/json")) return json({ code: "JSON_REQUIRED" }, 415);
    const reader = request.body?.getReader(); if (!reader) return json({ code: "INVALID_CREDENTIALS" }, 401);
    const chunks: Uint8Array[] = []; let size = 0;
    while (true) { const chunk = await reader.read(); if (chunk.done) break; size += chunk.value.length; if (size > 4096) { await reader.cancel(); return json({ code: "BODY_TOO_LARGE" }, 413); } chunks.push(chunk.value); }
    let body: unknown; try { body = JSON.parse(Buffer.concat(chunks).toString("utf8")); } catch { return json({ code: "INVALID_JSON" }, 400); }
    if (!body || typeof body !== "object" || Array.isArray(body) || Object.keys(body).some(k => k !== "password") || !await verifyPassword((body as { password?: unknown }).password, config.passwordHash)) {
      console.info(JSON.stringify({ timestamp: new Date().toISOString(), component: "Bff", action: "Login", outcome: "Denied" }));
      return json({ code: "INVALID_CREDENTIALS" }, 401);
    }
    const response = json({ authenticated: true });
    response.cookies.set(sessionCookie, signSession(config.key), { httpOnly: true, secure: true, sameSite: "strict", path: "/", maxAge: sessionSeconds });
    console.info(JSON.stringify({ timestamp: new Date().toISOString(), component: "Bff", action: "Login", outcome: "Succeeded" }));
    return response;
  } catch { return json({ code: "PROFILE_NOT_READY" }, 503); }
}
export async function DELETE(request: NextRequest) {
  if (process.env.APP_PROFILE !== "CloudDemo") return json({ code: "PROFILE_NOT_READY" }, 503);
  try {
    const config = cloudConfig(); if (request.headers.get("origin") !== config.origin) return json({ code: "ORIGIN_NOT_ALLOWED" }, 403);
    const response = json({ authenticated: false }); response.cookies.set(sessionCookie, "", { httpOnly: true, secure: true, sameSite: "strict", path: "/", maxAge: 0 });
    console.info(JSON.stringify({ timestamp: new Date().toISOString(), component: "Bff", action: "Logout", outcome: "Succeeded" })); return response;
  } catch { return json({ code: "PROFILE_NOT_READY" }, 503); }
}
