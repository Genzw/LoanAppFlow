import { test, expect } from "@playwright/test";
test("bootstrap reads the actual API catalog and labels pending functionality", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: /Decisiones trazables/ })).toBeVisible();
  await page.getByRole("button", { name: "Comprobar API" }).click();
  await expect(page.getByRole("status")).toHaveText("API conectada · Catálogo recibido");
  await expect(page.getByRole("cell", { name: "requestedAmount", exact: true })).toBeVisible();
  await expect(page.getByText(/Esta pantalla no procesa solicitudes/)).toBeVisible();
});
test("bootstrap exposes no arbitrary backend proxy", async ({ request }) => {
  expect((await request.get("/api/admin/unknown-route")).status()).toBe(404);
  expect((await request.post("/api/admin/rule-catalog", { data: {} })).status()).toBe(405);
});

test("home page is navigable at 360px without overflow", async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 640 });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: /Decisiones trazables/ })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(360);
});
