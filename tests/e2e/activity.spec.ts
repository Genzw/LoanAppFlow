import { test, expect } from "@playwright/test";

test("audit joins investigation by correlation through real API and HTTP mock without mixing pages", async ({ page, request }) => {
  test.skip(!process.env.TEST_E2E_MOCK_URL, "Requires isolated stack");
  const response = await request.post("/api/applications", { headers: { Origin: process.env.TEST_E2E_BASE_URL! }, data: { firstName: "Ana", lastName: "Paz", companyName: "Audit Demo", ssn: "000000007", requestedAmount: 130,
    address: { line1: "Demo", line2: null, city: "Demo", state: "CA", postalCode: "90001" } } });
  expect(response.status()).toBe(200); const approved = await response.json();
  const correlation = response.headers()["x-correlation-id"];
  await expect.poll(async () => { const r = await request.get(`/api/admin/external/audit?correlationId=${correlation}`); return r.ok() ? (await r.json()).items.length : 0; }, { timeout: 20000 }).toBe(1);
  const first = await (await request.get(`/api/admin/audit?limit=1&correlationId=${correlation}`)).json();
  expect(first.items).toHaveLength(1); expect(first.nextCursor).toBeTruthy();
  const next = await (await request.get(`/api/admin/audit?limit=1&correlationId=${correlation}&cursor=${encodeURIComponent(first.nextCursor)}`)).json();
  expect(next.items[0].id).not.toBe(first.items[0].id);
  expect((await request.get(`/api/admin/audit?limit=1&action=Different&cursor=${encodeURIComponent(first.nextCursor)}`)).status()).toBe(400);
  expect((await request.get(`/api/admin/external/audit?limit=1&correlationId=${correlation}&cursor=${encodeURIComponent(first.nextCursor)}`)).status()).toBe(400);
  expect((await request.get("/api/admin/audit?fromUtc=invalid")).status()).toBe(400);
  await page.goto("/admin/activity");
  await page.getByLabel("Correlación", { exact: true }).fill(correlation); await page.getByRole("button", { name: "Aplicar filtros" }).click();
  const results = page.getByRole("region", { name: "Resultados de actividad" });
  await expect(results.getByRole("heading", { name: "Application.Approved", exact: true })).toBeVisible();
  await results.getByRole("button", { name: "Ver detalle de Application.Approved", exact: true }).click();
  const detail = page.getByRole("region", { name: "Detalle de actividad" }); await expect(detail).toContainText(correlation); await expect(detail).toContainText(approved.eventId);
  expect(await page.locator("main").textContent()).not.toContain("000000007");
  await page.getByRole("button", { name: "Mock externo", exact: true }).click();
  await expect(results.getByRole("heading", { name: "ExternalApplication.Created", exact: true })).toBeVisible();
  await expect(results.getByRole("heading", { name: "Application.Approved", exact: true })).toHaveCount(0);
  await results.getByRole("button", { name: "Ver detalle de ExternalApplication.Created", exact: true }).click();
  await expect(detail).toContainText(correlation); await expect(detail).toContainText("Worker");
  await page.screenshot({ path: "../../.local/activity-desktop.png", fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 }); expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(390);
  await page.screenshot({ path: "../../.local/activity-mobile.png", fullPage: true });
});

test("audit errors never appear as an empty audit and can be retried", async ({ page }) => {
  let unavailable = true;
  await page.route("**/api/admin/audit?*", route => unavailable ? route.fulfill({ status: 503, json: { code: "DATABASE_UNAVAILABLE" } }) : route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.goto("/admin/activity"); await expect(page.getByRole("main").getByRole("alert")).toContainText("No se pudo consultar la actividad");
  await expect(page.getByText("No hay movimientos para estos filtros.")).toHaveCount(0);
  unavailable = false; await page.getByRole("button", { name: "Reintentar consulta" }).click();
  await expect(page.getByText("No hay movimientos para estos filtros.")).toBeVisible();
});
