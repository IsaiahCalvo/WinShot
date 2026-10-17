import { defineConfig } from "@playwright/test";

const platformSmoke = /popup uses the desktop icon|dynamic, horizontal, frame, and pixel-surface|URL batch enforces the 20-URL cap|wall-clock adjustments|transient screenshot readback|repeating Web Animations resume|fixed sidebar survives|fixed header and footer remain whole|capture controls stay disabled until delayed settings|CSS view-timeline reveals|scroll-driven history-path URL updates|lazy embedded documents|shadow trees created|Stop remains effective|countdowns allow|cancelled picker identity|cross-window progress|last-used hotkey starts|frame that finishes navigating|devicePixelRatio 2: sticky|first scrolling tile rejects|fixed wrapper with visible absolute children|GSAP reveals finish|collapses after the first tile|WebGL canvas|rAF canvas pixels|videos pause during capture|clipboard and print|only embedded document is still loading|nested fixed header and footer|GSAP Infinity|GSAP invalidated|recapture keeps the original|old main-world animation bridge/;

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
      name: "extension-smoke",
      testMatch: /(?:adversarial-reliability|adversarial-scroll-engine|adversarial-scroll-motion|adversarial-element-frame-picker|adversarial-media-output|adversarial-popup-settings-history|adversarial-batch|puppeteer-extension)\.spec\.mjs/,
      grep: platformSmoke,
      use: { browserName: "chromium" }
    },
    {
      name: "extension-puppeteer",
      testMatch: /(?:puppeteer-extension|adversarial-[\w-]+)\.spec\.mjs/,
      grepInvert: platformSmoke,
      dependencies: ["extension-smoke"],
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
