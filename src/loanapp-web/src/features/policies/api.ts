export type Condition = { field: string; operator: string; value?: string | number; values?: string[] };
export type Rule = { id: string; code: string; name: string; publicMessage: string; enabled: boolean; priority: number; effect: "Deny"; match: "ALL" | "ANY"; conditions: Condition[] };
export type Revision = { id: string; kind: "Draft" | "Published"; draftVersion: number; baseRevisionId: string | null; publishedAtUtc: string | null; rules: Rule[]; blacklist: { id: string; maskedSsn: string }[] };
export type Head = { headVersion: number; activeRevisionId: string | null; draftRevisionId: string | null; baselineRevisionId: string | null };
export type Field = { name: string; type: string; maxLength: number | null; operators: string[] };
export type Validation = { valid: boolean; warnings: string[]; headVersion: number; draftVersion: number; summary: { rulesAdded: number; rulesChanged: number; rulesRemoved: number; blacklistAdded: number; blacklistRemoved: number; hasBaselineDifferences: boolean | null } };
export type Evaluation = { decision: string; policyRevisionId: string; draftVersion: number; reasons: { ruleId: string; code: string; message: string }[]; trace: { ruleId: string; enabled: boolean; matched: boolean | null; conditions: { index: number; matched: boolean }[] }[] };
export class ApiError extends Error {
  constructor(public status: number, public code: string, detail: string) { super(detail); }
}
export async function api<T>(path: string, method = "GET", body?: unknown, tag?: string): Promise<{ data: T; tag: string }> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (tag) headers["If-Match"] = tag;
  let response: Response;
  try { response = await fetch(`/api/admin/${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: "no-store" }); }
  catch { throw new ApiError(503, method === "GET" ? "UPSTREAM_UNAVAILABLE" : "OUTCOME_UNKNOWN", "Could not confirm the operation. Check system status before retrying."); }
  const data = response.status === 204 ? undefined : await response.json();
  if (!response.ok) {
    const detail = data.errors ? Object.entries(data.errors).map(([field, errors]) => `${field}: ${(errors as string[]).join(" ")}`).join(" · ") : data.code;
    throw new ApiError(response.status, data.code || "REQUEST_FAILED", detail || "The operation could not be completed.");
  }
  return { data: data as T, tag: response.headers.get("etag") || "" };
}
export function describe(error: unknown) {
  if (error instanceof ApiError && error.code === "BLACKLIST_DUPLICATE") return "This SSN is already on the draft blacklist.";
  if (error instanceof ApiError && [409, 412].includes(error.status)) return "The policy was modified in another session or the action is stale. Local edits have been preserved; please review the current version.";
  if (error instanceof ApiError && error.code === "OUTCOME_UNKNOWN") return "Could not confirm the outcome. Please inspect current policy state before repeating the action.";
  return error instanceof Error ? error.message : "An unexpected error occurred.";
}
