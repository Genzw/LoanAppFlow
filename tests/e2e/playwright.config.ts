import { defineConfig } from "@playwright/test";
export default defineConfig({ testDir: ".", testMatch: "*.spec.ts", workers: 1, use: { baseURL: process.env.TEST_E2E_BASE_URL || "http://127.0.0.1:3000", headless: true, channel: process.env.PLAYWRIGHT_CHANNEL || undefined }, reporter: "list" });
