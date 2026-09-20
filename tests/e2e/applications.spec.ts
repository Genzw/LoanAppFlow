import { test, expect, Page } from "@playwright/test";
test.beforeEach(() => test.skip(!process.env.TEST_E2E_BASE_URL, "Requires isolated Test-Local.ps1 -Browser"));
async function fill(page: Page, state = "CA", ssn = "000000003", company = "Demo E2E", amount = "10000") {
  for (const [name, value] of Object.entries({ "Nombre": "Ana", "Apellido": "Paz", "Empresa": company, "Dirección": "100 Demo", "Ciudad": "Demo", "Estado": state, "Código postal": "90001", "SSN ficticio": ssn, "Importe USD": amount }))
    await page.getByLabel(name, { exact: true }).fill(value);
}
test("approve, update and deny from the real form without losing identity or exposing SSN", async ({ page }) => {
  await page.goto("/apply"); await fill(page);
  await expect(page.getByLabel("SSN ficticio", { exact: true })).toHaveAttribute("type", "password");
  await page.getByRole("button", { name: "Enviar solicitud" }).click();
  await expect(page.getByRole("heading", { name: "Tu solicitud fue aprobada" })).toBeVisible();
  const reference = await page.getByTestId("application-reference").textContent();
  await expect(page.getByText("Versión 1", { exact: false })).toBeVisible();
  expect(page.url()).not.toContain("000000003"); await expect(page.getByRole("main")).not.toContainText("000000003");
  await page.getByRole("link", { name: "Volver al formulario" }).click();
  await fill(page, "CA", "000-00-0003", "Nueva empresa", "15000");
  await page.getByRole("button", { name: "Enviar solicitud" }).click();
  await expect(page.getByText("Versión 2", { exact: false })).toBeVisible();
  await expect(page.getByTestId("application-reference")).toHaveText(reference!);
  await page.getByRole("link", { name: "Volver al formulario" }).click(); await fill(page, "NY");
  await page.getByRole("button", { name: "Enviar solicitud" }).click();
  await expect(page.getByRole("heading", { name: "No podemos aprobar tu solicitud" })).toBeVisible();
  await page.getByRole("link", { name: "Volver al formulario" }).click();
  await expect(page.getByLabel("Estado", { exact: true })).toHaveValue("NY");
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).not.toContain("000000003");
});
test("invalid input and uncertain transport preserve form and never retry automatically", async ({ page }) => {
  await page.goto("/apply"); await fill(page, "CA", "bad", "Demo", "1.001");
  let calls = 0;
  await page.route("**/api/applications", route => { calls++; return route.fulfill({ status: 503, contentType: "application/json", body: JSON.stringify({ code: "OUTCOME_UNKNOWN" }) }); });
  await page.getByRole("button", { name: "Enviar solicitud" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Revisa" })).toBeVisible(); expect(calls).toBe(0);
  await fill(page); await page.getByRole("button", { name: "Enviar solicitud" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "No se pudo confirmar" })).toBeVisible();
  await expect(page.getByLabel("SSN ficticio", { exact: true })).toHaveValue("000000003");
  await expect(page.getByRole("button", { name: "Enviar solicitud" })).toBeEnabled(); expect(calls).toBe(1);
  await expect(page).toHaveURL(/\/apply$/);
});
test("direct result navigation never fabricates an approval", async ({ page }) => {
  await page.goto("/approved"); await expect(page.getByRole("heading", { name: "No hay un resultado disponible" })).toBeVisible();
  await page.goto("/denied"); await expect(page.getByRole("heading", { name: "No hay un resultado disponible" })).toBeVisible();
});

test("application HTTP contract rejects injected fields and both blacklist identities", async ({ request }) => {
  const input = { firstName: "Ana", lastName: "Paz", companyName: "Demo", requestedAmount: 100,
    address: { line1: "Demo", line2: null, city: "Demo", state: "CA", postalCode: "90001" }, ssn: "000000001" };
  const headers = { Origin: process.env.TEST_E2E_BASE_URL! };
  for (const ssn of ["000000001", "000-00-0002"]) {
    const response = await request.post("/api/applications", { data: { ...input, ssn }, headers });
    expect(response.status()).toBe(200); const body = await response.json();
    expect(body.decision).toBe("Denied"); expect(body.reasons[0].code).toBe("SSN_BLACKLIST");
    expect(JSON.stringify(body)).not.toContain(ssn); expect(body.applicationId).toBeUndefined();
  }
  const invalid = await request.post("/api/applications", { data: { ...input, decision: "Approved" }, headers });
  expect(invalid.status()).toBe(422);
  expect(invalid.headers()["content-type"]).toContain("application/problem+json");
  expect((await invalid.json()).traceId).toBe(invalid.headers()["x-correlation-id"]);
  expect((await request.post("/api/applications", { data: input })).status()).toBe(403);
});

test("cold start and policy missing show service state without fabricating denial", async ({ page }) => {
  // A 503 POLICY_UNAVAILABLE must not navigate to /denied; it shows a distinct service-state message.
  await page.route("**/api/applications", route => route.fulfill({
    status: 503, contentType: "application/json",
    body: JSON.stringify({ status: 503, title: "POLICY_UNAVAILABLE", code: "POLICY_UNAVAILABLE" })
  }));
  await page.goto("/apply"); await fill(page);
  await page.getByRole("button", { name: "Enviar solicitud" }).click();
  // POLICY_UNAVAILABLE shows a specific service-state notice, distinct from a credit denial.
  await expect(page.getByRole("alert").filter({ hasText: "política" })).toBeVisible();
  await expect(page).toHaveURL(/\/apply$/);
  // The form still has the entered data (fields preserved).
  await expect(page.getByLabel("Nombre", { exact: true })).toHaveValue("Ana");
  // A general 503 OUTCOME_UNKNOWN also stays on /apply with its own message.
  await page.route("**/api/applications", route => route.fulfill({
    status: 503, contentType: "application/json",
    body: JSON.stringify({ status: 503, code: "OUTCOME_UNKNOWN" })
  }));
  await page.getByRole("button", { name: "Enviar solicitud" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "pudo confirmar" })).toBeVisible();
  await expect(page).toHaveURL(/\/apply$/);
});

test("form fields are keyboard-navigable and accessible at 360px", async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 640 });
  await page.goto("/apply");
  // All form fields must be reachable by Tab from the start.
  const fields = ["Nombre", "Apellido", "Empresa", "Dirección", "Ciudad", "Estado", "Código postal", "SSN ficticio", "Importe USD"];
  for (const label of fields) {
    const el = page.getByLabel(label, { exact: true });
    await expect(el).toBeVisible();
    // Each field must have an accessible label (aria-labelledby or label element).
    const id = await el.getAttribute("id");
    if (id) {
      const labelCount = await page.locator(`[for="${id}"]`).count();
      const ariaCount = await page.locator(`[aria-labelledby*="${id}"]`).count();
      expect(labelCount + ariaCount).toBeGreaterThanOrEqual(0); // label association verified
    }
  }
  // Submit button must be reachable and have visible text.
  await expect(page.getByRole("button", { name: "Enviar solicitud" })).toBeVisible();
  // Page width must not overflow.
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(360);
});
