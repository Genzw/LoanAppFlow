import { NextRequest, NextResponse } from "next/server";
import { cloudConfig, readSession, sessionCookie } from "./server/session";
export function proxy(request: NextRequest) {
  if (process.env.APP_PROFILE === "LocalDevelopment") {
    if (!["127.0.0.1", "localhost", "[::1]"].includes(request.nextUrl.hostname)) return new NextResponse("LOCAL_ONLY", { status: 403 });
    return NextResponse.next();
  }
  if (process.env.APP_PROFILE !== "CloudDemo") return new NextResponse("PROFILE_NOT_READY", { status: 503 });
  try {
    const config = cloudConfig(); const path = request.nextUrl.pathname;
    if (path === "/login" || path.startsWith("/api/")) return NextResponse.next(); // API handlers authorize independently.
    if (!readSession(request.cookies.get(sessionCookie)?.value, config.key)) return NextResponse.redirect(new URL("/login", config.origin));
    const response = NextResponse.next(); response.headers.set("Cache-Control", "no-store"); return response;
  } catch { return new NextResponse("PROFILE_NOT_READY", { status: 503 }); }
}
export const config = { matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"] };
