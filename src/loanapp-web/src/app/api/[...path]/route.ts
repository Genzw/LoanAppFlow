import { NextRequest, NextResponse } from "next/server";
import { cloudConfig, readSession, sessionCookie } from "../../../server/session";

export const runtime = "nodejs";
export const maxDuration = 90;

async function forward(request: NextRequest, context: { params: Promise<{ path: string[] }> }) {
  const started = Date.now();
  const correlationId = crypto.randomUUID();
  function log(statusCode: number, code?: string) {
    console.info(JSON.stringify({
      timestamp: new Date().toISOString(), component: "Bff", action: "HttpRequest",
      route: request.nextUrl.pathname, method: request.method,
      statusCode, durationMs: Date.now() - started,
      correlationId, ...(code ? { code } : {})
    }));
  }
  const cloud = process.env.APP_PROFILE === "CloudDemo";
  if (!cloud && process.env.APP_PROFILE !== "LocalDevelopment") {
    log(503, "PROFILE_NOT_READY");
    return NextResponse.json({ code: "PROFILE_NOT_READY" }, { status: 503 });
  }
  let security: ReturnType<typeof cloudConfig> | undefined; let sessionId: string | undefined;
  if (cloud) {
    try { security = cloudConfig(); sessionId = readSession(request.cookies.get(sessionCookie)?.value, security.key)?.id; }
    catch { log(503, "PROFILE_NOT_READY"); return NextResponse.json({ code: "PROFILE_NOT_READY" }, { status: 503 }); }
    if (!sessionId) { log(401, "SESSION_REQUIRED"); return NextResponse.json({ code: "SESSION_REQUIRED" }, { status: 401, headers: { "Cache-Control": "no-store" } }); }
  }
  const host = request.nextUrl.hostname;
  if (!cloud && !["127.0.0.1", "localhost", "[::1]"].includes(host)) {
    log(403, "LOCAL_ONLY");
    return NextResponse.json({ code: "LOCAL_ONLY" }, { status: 403 });
  }
  const { path } = await context.params;
  const route = path.join("/");
  const uuid = "[0-9a-fA-F-]{36}";
  const allowed: [RegExp, string[]][] = [
    [/^admin\/(?:external\/)?audit$/, ["GET"]],
    [/^admin\/applications$/, ["GET"]], [new RegExp(`^admin/applications/${uuid}$`), ["GET"]],
    [/^admin\/external\/(applications|receipts)$/, ["GET"]],
    [new RegExp(`^admin/outbox/${uuid}/retry$`), ["POST"]],
    [/^applications$/, ["POST"]],
    [/^admin\/rule-catalog$/, ["GET"]], [/^admin\/policy$/, ["GET"]],
    [/^admin\/policy\/revisions$/, ["GET"]], [new RegExp(`^admin/policy/revisions/${uuid}$`), ["GET"]],
    [/^admin\/policy\/draft$/, ["POST", "DELETE"]], [/^admin\/policy\/draft\/rules$/, ["PUT"]],
    [/^admin\/policy\/draft\/blacklist$/, ["POST"]], [new RegExp(`^admin/policy/draft/blacklist/${uuid}$`), ["DELETE"]],
    [/^admin\/policy\/draft\/(validate|publish)$/, ["POST"]], [new RegExp(`^admin/policy/revisions/${uuid}/simulate$`), ["POST"]]
  ];
  const match = allowed.find(([pattern]) => pattern.test(route));
  if (!match) { log(404, "NOT_FOUND"); return NextResponse.json({ code: "NOT_FOUND" }, { status: 404 }); }
  if (!match[1].includes(request.method)) { log(405, "METHOD_NOT_ALLOWED"); return NextResponse.json({ code: "METHOD_NOT_ALLOWED" }, { status: 405 }); }
  const mutating = request.method !== "GET";
  const allowedOrigin = security?.origin || process.env.LOCAL_WEB_ORIGIN || "http://127.0.0.1:3000";
  if (mutating && request.headers.get("origin") !== allowedOrigin) {
    log(403, "ORIGIN_NOT_ALLOWED");
    return NextResponse.json({ code: "ORIGIN_NOT_ALLOWED" }, { status: 403 });
  }
  const backend = security?.backend || process.env.BACKEND_BASE_URL;
  if (!backend) { log(503, "BACKEND_NOT_CONFIGURED"); return NextResponse.json({ code: "BACKEND_NOT_CONFIGURED" }, { status: 503 }); }
  try {
    const url = new URL(`/api/${route}`, backend);
    if (!cloud && (!["127.0.0.1", "localhost", "[::1]"].includes(url.hostname) || !["http:", "https:"].includes(url.protocol))) {
      log(503, "LOCAL_ONLY");
      return NextResponse.json({ code: "LOCAL_ONLY" }, { status: 503 });
    }
    if (route === "admin/policy/revisions" || route === "admin/audit" || route.startsWith("admin/applications") || route.startsWith("admin/external/")) {
      for (const key of ["limit", "cursor", "eventsCursor", "fromUtc", "toUtc", "action", "entityType", "entityId", "correlationId"]) {
        const value = request.nextUrl.searchParams.get(key); if (value) url.searchParams.set(key, value);
      }
    }
    const headers = new Headers();
    if (security && sessionId) { headers.set("X-Backend-Token", security.backendToken); headers.set("X-Demo-Session", sessionId); }
    const tag = request.headers.get("if-match"); if (tag) headers.set("If-Match", tag);
    if (mutating) headers.set("Origin", allowedOrigin);
    let body: Uint8Array<ArrayBuffer> | undefined;
    if (request.body) {
      const limit = route === "admin/policy/draft/rules" ? 2097152 : 16384;
      const reader = request.body.getReader(); const chunks: Uint8Array[] = []; let size = 0;
      while (true) {
        const chunk = await reader.read(); if (chunk.done) break;
        size += chunk.value.length;
        if (size > limit) { await reader.cancel(); log(413, "BODY_TOO_LARGE"); return NextResponse.json({ code: "BODY_TOO_LARGE" }, { status: 413 }); }
        chunks.push(chunk.value);
      }
      if (size && !request.headers.get("content-type")?.toLowerCase().startsWith("application/json")) {
        log(415, "JSON_REQUIRED");
        return NextResponse.json({ code: "JSON_REQUIRED" }, { status: 415 });
      }
      body = new Uint8Array(size); let offset = 0;
      for (const chunk of chunks) { body.set(chunk, offset); offset += chunk.length; }
      if (size) headers.set("Content-Type", "application/json"); else body = undefined;
    }
    const response = await fetch(url, { method: request.method, headers, body, cache: "no-store", redirect: "error", signal: AbortSignal.timeout(cloud ? 75000 : 20000) });
    const outputHeaders = new Headers({ "Cache-Control": "no-store" });
    for (const name of ["ETag", "Content-Type", "X-Correlation-Id"]) {
      const value = response.headers.get(name); if (value) outputHeaders.set(name, value);
    }
    if (response.status === 204) { log(204); return new NextResponse(null, { status: 204, headers: outputHeaders }); }
    if (!/application\/(?:problem\+)?json/i.test(response.headers.get("content-type") || "")) {
      log(503, "UPSTREAM_UNAVAILABLE");
      return NextResponse.json({ code: "UPSTREAM_UNAVAILABLE" }, { status: 503 });
    }
    log(response.status);
    return new NextResponse(await response.text(), { status: response.status, headers: outputHeaders });
  } catch {
    const code = mutating ? "OUTCOME_UNKNOWN" : "UPSTREAM_UNAVAILABLE";
    log(503, code);
    return NextResponse.json({ code }, { status: 503 });
  }
}
export { forward as GET, forward as POST, forward as PUT, forward as DELETE };
