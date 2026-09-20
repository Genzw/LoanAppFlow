import { test, expect } from "@playwright/test";

test.describe.configure({ mode: "serial" });
test.beforeEach(() => {
  test.skip(!process.env.TEST_E2E_BASE_URL, "Mutation tests require the isolated stack: scripts/Test-Local.ps1 -Browser");
});

test("create, simulate, publish, mask blacklist and restore history using the GUI", async ({ page, request }) => {
  const initial = await (await request.get("/api/admin/policy")).json();
  await page.goto("/admin/rules");
  await page.getByRole("button", { name: "Crear borrador desde activa" }).click();
  await expect(page.getByRole("button", { name: "Añadir regla" })).toBeEnabled();
  await page.getByRole("button", { name: "Añadir regla" }).click();
  const rule = page.locator("article.rule-card").last();
  await rule.getByLabel("Nombre de regla").fill("Límite de demostración");
  await rule.getByLabel("Código", { exact: true }).fill("AMOUNT_LIMIT");
  await rule.getByLabel("Mensaje para el solicitante").fill("El importe supera el límite.");
  await rule.getByRole("combobox", { name: "Campo", exact: true }).selectOption("requestedAmount");
  await rule.getByRole("combobox", { name: "Operador", exact: true }).selectOption("greaterThan");
  await rule.getByLabel("Valor", { exact: true }).fill("20000");
  await page.getByRole("button", { name: "Guardar reglas" }).click();
  await expect(page.getByText("Reglas guardadas en el borrador.", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Cargar ejemplo ficticio" }).click();
  await page.getByLabel("Importe USD", { exact: true }).fill("25000");
  await page.getByRole("button", { name: "Simular", exact: true }).click();
  await expect(page.getByRole("heading", { name: "La política denegaría esta solicitud" })).toBeVisible();
  const blacklist = page.locator("section.blacklist");
  await blacklist.getByLabel("SSN ficticio").fill("000-00-0004");
  await blacklist.getByRole("button", { name: "Agregar a lista negra" }).click();
  await expect(blacklist.getByText("***-**-0004", { exact: true })).toBeVisible();
  await expect(blacklist.getByLabel("SSN ficticio")).toHaveValue("");
  await page.getByRole("button", { name: "Validar y revisar publicación" }).click();
  await expect(page.getByRole("heading", { name: "Revisar publicación" })).toBeVisible();
  await expect(page.getByText("La política difiere del perfil inicial", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Confirmar publicación" }).click();
  await expect(page.getByText("Política publicada.", { exact: false })).toBeVisible();
  await page.reload(); await expect(page.getByRole("heading", { name: "Límite de demostración" })).toBeVisible();
  await page.getByRole("link", { name: "Historial", exact: true }).click();
  const original = page.locator("article").filter({ hasText: initial.baselineRevisionId });
  await original.getByRole("button", { name: "Usar como borrador" }).click();
  await expect(page.getByRole("button", { name: "Validar y revisar publicación" })).toBeEnabled();
  await expect(page.getByRole("heading", { name: "Límite de demostración" })).toHaveCount(0);
  await page.getByRole("button", { name: "Validar y revisar publicación" }).click();
  await page.getByRole("button", { name: "Confirmar publicación" }).click();
  await expect(page.getByText("Política publicada.", { exact: false })).toBeVisible();
});

test("conflicting tabs preserve unsaved changes and allow inspecting the server version", async ({ page, context }) => {
  await page.goto("/admin/rules"); await page.getByRole("button", { name: "Crear borrador desde activa" }).click();
  await expect(page.getByRole("button", { name: "Añadir regla" })).toBeEnabled();
  const other = await context.newPage(); await other.goto("/admin/rules");
  await expect(other.getByRole("button", { name: "Añadir regla" })).toBeEnabled();
  await page.getByLabel("Nombre de regla").first().fill("Primera pestaña");
  await page.getByRole("button", { name: "Guardar reglas" }).click();
  await expect(page.getByText("Reglas guardadas en el borrador.", { exact: false })).toBeVisible();
  await other.getByLabel("Nombre de regla").first().fill("Mi edición sin guardar");
  await other.getByRole("button", { name: "Guardar reglas" }).click();
  await expect(other.getByRole("main").getByRole("alert")).toContainText("cambió en otra pestaña");
  await expect(other.getByLabel("Nombre de regla").first()).toHaveValue("Mi edición sin guardar");
  await other.getByRole("button", { name: "Consultar versión actual" }).click();
  await expect(other.locator("section.remote")).toContainText("Primera pestaña");
  await other.getByRole("button", { name: "Cerrar comparación" }).click();
  await expect(other.getByLabel("Nombre de regla").first()).toHaveValue("Mi edición sin guardar");
  other.on("dialog", dialog => dialog.accept());
  await other.getByRole("button", { name: "Descartar cambios locales y recargar" }).click();
  await expect(other.getByLabel("Nombre de regla").first()).toHaveValue("Primera pestaña");
  await other.getByRole("button", { name: "Descartar borrador", exact: true }).click();
  await expect(other.getByRole("button", { name: "Crear borrador desde activa" })).toBeVisible();
  await other.close();
});

test("edit, disable, delete and explicitly publish an empty policy with validation feedback", async ({ page, request }) => {
  const baseline = (await (await request.get("/api/admin/policy")).json()).baselineRevisionId;
  await page.goto("/admin/rules");
  await page.getByRole("button", { name: "Crear borrador desde activa" }).click();
  const stateRule = page.locator("article.rule-card").first();
  await stateRule.getByLabel("Valor", { exact: true }).fill("X");
  await page.getByRole("button", { name: "Guardar reglas" }).click();
  await expect(page.getByRole("main").getByRole("alert")).toBeVisible();
  await expect(stateRule.getByLabel("Valor", { exact: true })).toHaveValue("X");
  await expect(page.getByRole("button", { name: "Validar y revisar publicación" })).toBeDisabled();
  await stateRule.getByLabel("Valor", { exact: true }).fill("NY");
  await stateRule.getByRole("checkbox", { name: "Regla habilitada" }).uncheck();
  await page.getByRole("button", { name: "Guardar reglas" }).click();
  await expect(page.getByText("Reglas guardadas en el borrador.", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Cargar ejemplo ficticio" }).click();
  await page.locator("section.simulator").getByLabel("Estado", { exact: true }).fill("NY");
  await page.getByRole("button", { name: "Simular", exact: true }).click();
  await expect(page.getByRole("heading", { name: "La política aprobaría esta solicitud" })).toBeVisible();
  await stateRule.getByRole("checkbox", { name: "Regla habilitada" }).check();
  await expect(page.locator(".simulation-result")).toHaveCount(0);
  await stateRule.getByRole("combobox", { name: "Denegar cuando" }).selectOption("ANY");
  await stateRule.getByRole("button", { name: "Añadir condición" }).click();
  await stateRule.getByRole("combobox", { name: "Campo", exact: true }).last().selectOption("companyName");
  await stateRule.getByRole("combobox", { name: "Operador", exact: true }).last().selectOption("in");
  await stateRule.getByLabel("Valores, uno por línea").fill("Taller Demo\nOtra empresa");
  await page.getByRole("button", { name: "Guardar reglas" }).click();
  await expect(page.getByText("Reglas guardadas en el borrador.", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Cargar ejemplo ficticio" }).click();
  await page.getByRole("button", { name: "Simular", exact: true }).click();
  await expect(page.getByRole("heading", { name: "La política denegaría esta solicitud" })).toBeVisible();
  const blacklist = page.locator("section.blacklist");
  await blacklist.getByLabel("SSN ficticio").fill("000-00-0001");
  await blacklist.getByRole("button", { name: "Agregar a lista negra" }).click();
  await expect(page.getByRole("main").getByRole("alert")).toContainText("ya está en la lista negra");
  await expect(blacklist.getByRole("button", { name: "Retirar" }).first()).toBeEnabled();
  await blacklist.getByRole("button", { name: "Retirar" }).first().click();
  await expect(blacklist.getByText("***-**-0001", { exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Eliminar regla" }).first().click();
  await page.getByRole("button", { name: "Eliminar regla" }).first().click();
  await page.getByRole("button", { name: "Guardar reglas" }).click();
  await expect(page.getByText("Reglas guardadas en el borrador.", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Validar y revisar publicación" }).click();
  await expect(page.getByText("Esta política aprobará todas las solicitudes", { exact: false })).toBeVisible();
  await page.getByRole("button", { name: "Confirmar publicación" }).click();
  await expect(page.getByText("Política publicada.", { exact: false })).toBeVisible();
  await page.reload(); await expect(page.locator("article.rule-card")).toHaveCount(0);
  await expect(page.getByText("No hay reglas.", { exact: false })).toBeVisible();
  await page.getByRole("link", { name: "Historial", exact: true }).click();
  await page.locator("article").filter({ hasText: baseline }).getByRole("button", { name: "Usar como borrador" }).click();
  await page.getByRole("button", { name: "Validar y revisar publicación" }).click();
  await page.getByRole("button", { name: "Confirmar publicación" }).click();
  await expect(page.getByText("Política publicada.", { exact: false })).toBeVisible();
});

test("history distinguishes unavailable service from an empty history and supports retry", async ({ page }) => {
  // Response injection is limited to UI states; persistence/concurrency tests use real PostgreSQL.
  let unavailable = true;
  await page.route("**/api/admin/policy/revisions", route => route.fulfill({
    status: unavailable ? 503 : 200, contentType: "application/json",
    body: JSON.stringify(unavailable ? { code: "UPSTREAM_UNAVAILABLE" } : { items: [], nextCursor: null })
  }));
  await page.goto("/admin/rules/history");
  await expect(page.getByRole("main").getByRole("alert")).toBeVisible();
  await expect(page.getByText("No hay políticas publicadas.")).toHaveCount(0);
  unavailable = false;
  await page.getByRole("button", { name: "Reintentar historial" }).click();
  await expect(page.getByText("No hay políticas publicadas.")).toBeVisible();
  await expect(page.getByRole("main").getByRole("alert")).toHaveCount(0);
});
