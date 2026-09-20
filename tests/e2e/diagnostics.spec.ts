import { test, expect } from "@playwright/test";

test("diagnostic shows recurring application, masked timeline and real external receipt through BFF", async ({ page, request }) => {
  test.skip(!process.env.TEST_E2E_MOCK_URL, "Requires isolated stack");
  const headers = { Origin: process.env.TEST_E2E_BASE_URL! };
  const form = { firstName: "Ana", lastName: "Paz", companyName: "Diagnostic Demo", ssn: "000000006", requestedAmount: 130,
    address: { line1: "Demo", line2: null, city: "Demo", state: "CA", postalCode: "90001" } };
  const before = await (await request.get("/api/admin/applications")).json();
  const first = await (await request.post("/api/applications", { headers, data: form })).json();
  const second = await (await request.post("/api/applications", { headers, data: { ...form, requestedAmount: 260 } })).json();
  expect(second.applicationId).toBe(first.applicationId);
  const after = await (await request.get("/api/admin/applications?limit=1")).json();
  expect(after.customerCount).toBe(before.customerCount + 1); expect(after.applicationCount).toBe(before.applicationCount + 1);
  expect((await request.get("/api/admin/applications?cursor=invalid")).status()).toBe(400);
  expect((await request.get(`/api/admin/applications/${first.applicationId}?eventsCursor=-1`)).status()).toBe(400);
  expect((await request.get("/api/admin/external/receipts?limit=101")).status()).toBe(400);
  const detailPage = await (await request.get(`/api/admin/applications/${first.applicationId}?limit=1`)).json();
  expect(detailPage.events.items).toHaveLength(1); expect(detailPage.events.nextCursor).toBe("2");
  const older = await (await request.get(`/api/admin/applications/${first.applicationId}?limit=1&eventsCursor=2`)).json();
  expect(older.events.items[0].eventId).toBe(first.eventId);
  await page.goto("/admin/applications");
  await page.getByRole("row").filter({ hasText: first.applicationId }).getByRole("button", { name: "Ver entregas" }).click();
  const timeline = page.getByRole("region", { name: "Detalle de solicitud" });
  await expect(timeline.getByText(/SSN \*\*\*-\*\*-0006/)).toBeVisible();
  await expect(timeline.getByRole("heading", { name: "Versión 2 · Updated · Delivered" })).toBeVisible({ timeout: 20000 });
  await expect(timeline.getByRole("heading", { name: "Versión 1 · Created · Delivered" })).toBeVisible();
  expect(await timeline.textContent()).not.toContain(form.ssn);
  expect((await request.post(`/api/admin/outbox/${second.eventId}/retry`, { headers })).status()).toBe(409);
  const external = page.getByRole("region", { name: "Servicio externo" });
  await external.getByRole("combobox", { name: "Consultar", exact: true }).selectOption("receipts");
  await external.getByRole("button", { name: "Consultar servicio externo" }).click();
  await expect(external.getByText(/Destino disponible/)).toBeVisible();
  await expect(external.getByText(`Recibo: ${second.eventId}`, { exact: false })).toBeVisible();
  const receipts = await (await request.get("/api/admin/external/receipts?limit=1")).json();
  expect(receipts.items).toHaveLength(1);
  const next = await (await request.get(`/api/admin/external/receipts?limit=1&cursor=${encodeURIComponent(receipts.nextCursor)}`)).json();
  expect(next.items[0].eventId).not.toBe(receipts.items[0].eventId);
  await page.screenshot({ path: "../../.local/diagnostics-data-desktop.png", fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(390);
  await page.screenshot({ path: "../../.local/diagnostics-data-mobile.png", fullPage: true });
});

test("diagnostic distinguishes empty data and unavailable external destination", async ({ page }) => {
  await page.route("**/api/admin/applications?*", route => route.fulfill({ json: { items: [], nextCursor: null, customerCount: 0, applicationCount: 0 } }));
  let unavailable = true;
  await page.route("**/api/admin/external/applications?*", route => unavailable ? route.fulfill({ status: 503, json: { code: "EXTERNAL_UNAVAILABLE" } }) : route.fulfill({ json: { items: [], totalCount: 0, nextCursor: null } }));
  await page.goto("/admin/applications");
  await expect(page.getByText("No hay solicitudes en esta página.")).toBeVisible();
  const external = page.getByRole("region", { name: "Servicio externo" });
  await external.getByRole("button", { name: "Consultar servicio externo" }).click();
  await expect(external.getByRole("alert")).toContainText("Destino no disponible");
  await expect(external.getByText(/Destino disponible/)).toHaveCount(0);
  unavailable = false;
  await external.getByRole("button", { name: "Consultar servicio externo" }).click();
  await expect(external.getByText("No hay registros externos en esta página.")).toBeVisible();
});

test("timeline offers retry for predecessor and refreshes pending events without retrying POST", async ({ page }) => {
  const id = "11111111-1111-4111-8111-111111111111"; const eventId = "22222222-2222-4222-8222-222222222222";
  const successor = "33333333-3333-4333-8333-333333333333";
  const summary = { applicationId: id, customerId: id, requestedAmount: 100, version: 2, policyRevisionId: id, status: "Pending" };
  let retried = false; let delivered = false; let posts = 0;
  await page.route("**/api/admin/applications?*", route => route.fulfill({ json: { items: [summary], customerCount: 1, applicationCount: 1, nextCursor: null } }));
  await page.route(`**/api/admin/applications/${id}?*`, route => {
    const event = { eventId, applicationVersion: 1, policyRevisionId: id, operation: "Created", status: delivered ? "Delivered" : retried ? "Pending" : "Failed", attemptCount: 1, createdAtUtc: "2026-09-18T00:00:00Z", nextAttemptAtUtc: "2026-09-18T00:00:00Z", deliveredAtUtc: null, lastErrorCode: delivered ? null : "HTTP_REJECTED", inProgress: false, blockedByEventId: null, blockedByVersion: null };
    return route.fulfill({ json: { application: summary, customer: { firstName: "Ana", lastName: "Paz", companyName: "Demo", state: "CA", maskedSsn: "***-**-0003" }, events: { items: [{ ...event, eventId: successor, applicationVersion: 2, operation: "Updated", status: delivered ? "Delivered" : "Pending", blockedByEventId: delivered ? null : eventId, blockedByVersion: delivered ? null : 1 }, event], nextCursor: null } } });
  });
  await page.route(`**/api/admin/outbox/${eventId}/retry`, route => { posts++; retried = true; return route.fulfill({ json: { eventId, status: "Pending" } }); });
  await page.goto("/admin/applications"); await page.getByRole("button", { name: "Ver entregas" }).click();
  const timeline = page.getByRole("region", { name: "Detalle de solicitud" });
  await expect(timeline.getByRole("button", { name: "Reintentar entrega v2" })).toBeDisabled();
  await timeline.getByRole("button", { name: "Reintentar entrega v1" }).click();
  await expect(timeline.getByRole("status")).toContainText("Reintento programado");
  await expect(timeline.getByRole("heading", { name: "Versión 1 · Created · Pending" })).toBeVisible();
  delivered = true;
  await expect(timeline.getByRole("heading", { name: "Versión 2 · Updated · Delivered" })).toBeVisible({ timeout: 7000 });
  expect(posts).toBe(1);
});
