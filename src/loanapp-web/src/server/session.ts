import { createHmac, pbkdf2, randomUUID, timingSafeEqual } from "node:crypto";

export const sessionCookie = "__Host-loanapp-session";
export const sessionSeconds = 8 * 60 * 60;
export type Session = { v: 1; id: string; iat: number; exp: number };
export function keyBytes(value: string | undefined): Buffer {
  if (!value || !/^[A-Za-z0-9+/]{43}=$/.test(value)) throw new Error("INVALID_SECRET_CONFIGURATION");
  const bytes = Buffer.from(value, "base64");
  if (bytes.length !== 32 || bytes.toString("base64") !== value) throw new Error("INVALID_SECRET_CONFIGURATION");
  return bytes;
}
export function passwordParts(value: string | undefined) {
  const parts = value?.split("$") ?? [];
  if (parts.length !== 4 || parts[0] !== "pbkdf2-sha256" || parts[1] !== "600000" || !/^[A-Za-z0-9+/]{22}==$/.test(parts[2])) throw new Error("INVALID_PASSWORD_CONFIGURATION");
  const salt = Buffer.from(parts[2], "base64"); if (salt.length !== 16 || salt.toString("base64") !== parts[2]) throw new Error("INVALID_PASSWORD_CONFIGURATION");
  return { salt, expected: keyBytes(parts[3]) };
}
export async function verifyPassword(password: unknown, hash: string): Promise<boolean> {
  const { salt, expected } = passwordParts(hash);
  if (typeof password !== "string" || Buffer.byteLength(password, "utf8") > 256 || password.length === 0) return false;
  const actual = await new Promise<Buffer>((resolve, reject) => pbkdf2(password, salt, 600000, 32, "sha256", (error, derived) => error ? reject(error) : resolve(derived)));
  return timingSafeEqual(actual, expected);
}
export function signSession(key: string, now = Math.floor(Date.now() / 1000)): string {
  const payload: Session = { v: 1, id: randomUUID(), iat: now, exp: now + sessionSeconds };
  const encoded = Buffer.from(JSON.stringify(payload)).toString("base64url");
  return encoded + "." + createHmac("sha256", keyBytes(key)).update(encoded).digest("base64url");
}
export function readSession(token: string | undefined, key: string, now = Math.floor(Date.now() / 1000)): Session | null {
  const bytes = keyBytes(key);
  if (!token || token.length > 1024) return null;
  const parts = token.split("."); if (parts.length !== 2 || !parts.every(p => /^[A-Za-z0-9_-]+$/.test(p))) return null;
  const signature = Buffer.from(parts[1], "base64url"); const expected = createHmac("sha256", bytes).update(parts[0]).digest();
  if (signature.length !== expected.length || signature.toString("base64url") !== parts[1] || !timingSafeEqual(signature, expected)) return null;
  try {
    const value = JSON.parse(Buffer.from(parts[0], "base64url").toString("utf8"));
    if (value.v !== 1 || typeof value.id !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value.id) ||
        !Number.isSafeInteger(value.iat) || !Number.isSafeInteger(value.exp) || value.iat > now || value.exp <= now || value.exp - value.iat !== sessionSeconds) return null;
    return value as Session;
  } catch { return null; }
}
export function cloudConfig() {
  const key = process.env.SESSION_SIGNING_KEY!; keyBytes(key);
  const backendToken = process.env.BACKEND_SERVICE_TOKEN!; keyBytes(backendToken);
  if (key === backendToken) throw new Error("SECRETS_MUST_DIFFER");
  const passwordHash = process.env.DEMO_PASSWORD_HASH!; passwordParts(passwordHash);
  function origin(value: string | undefined) {
    if (!value) throw new Error("ORIGIN_REQUIRED"); const url = new URL(value);
    if (url.protocol !== "https:" || url.username || url.password || url.pathname !== "/" || url.search || url.hash) throw new Error("HTTPS_ORIGIN_REQUIRED");
    return url.origin;
  }
  return { key, backendToken, passwordHash, origin: origin(process.env.DEMO_ORIGIN), backend: origin(process.env.BACKEND_BASE_URL) };
}
