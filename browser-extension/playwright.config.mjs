import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./tests/e2e",
  timeout: 120_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [["list"], ["html", { outputFolder: "playwright-report", open: "never" }]],
  outputDir: "../output/playwright",
  webServer: {
    command: "node tests/fixture-server.mjs",
    url: "http://127.0.0.1:4173/",
    reuseExistingServer: true,
    timeout: 30_000
  },
  projects: [
    {
      name: "extension-puppeteer",
      testMatch: /puppeteer-extension\.spec\.mjs/,
      use: { browserName: "chromium" }
    },
    {
      name: "chrome-smoke",
      testMatch: /browser-smoke\.spec\.mjs/,
      use: { browserName: "chromium", channel: "chrome" }
    },
    {
      name: "edge-smoke",
      testMatch: /browser-smoke\.spec\.mjs/,
      use: { browserName: "chromium", channel: "msedge" }
    }
  ]
});
