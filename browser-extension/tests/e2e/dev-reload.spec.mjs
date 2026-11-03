// Developer auto-update: an unpacked copy reloads itself when a new build lands on disk,
// and reopens the WinShot pages that were open. Store installs never run this
// (installType must be "development"); here the extension is loaded unpacked.
import { test, expect } from "@playwright/test";
import puppeteer from "puppeteer";
import { sandboxArgs } from "./launch-args.mjs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "node:fs";
import os from "node:os";

const here = path.dirname(fileURLToPath(import.meta.url));
const builtPath = path.resolve(here, "..", "..", "dist", "winshot-capture");

function removeOwnedDirectory(directory, prefix) {
  const resolved = path.resolve(directory);
  if (path.dirname(resolved) !== path.resolve(os.tmpdir()) || !path.basename(resolved).startsWith(prefix)) {
    throw new Error(`Refusing to clean unexpected test path: ${resolved}`);
  }
  fs.rmSync(resolved, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
}

test("an unpacked build reloads itself when a new build stamp appears and reopens its pages", async () => {
  // A private copy, so rewriting its stamp cannot disturb other tests.
  const copy = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-dev-reload-ext-"));
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-dev-reload-profile-"));
  fs.cpSync(builtPath, copy, { recursive: true });
  const browser = await puppeteer.launch({
    executablePath: await puppeteer.executablePath(),
    headless: true,
    userDataDir: profile,
    enableExtensions: true,
    args: [...sandboxArgs, "--window-size=1200,900"]
  });
  try {
    const extensionId = await browser.installExtension(copy);
    const isWorker = (target) => target.type() === "service_worker" && target.url().endsWith("/src/background.js");
    const firstTarget = await browser.waitForTarget(isWorker, { timeout: 15_000 });
    const first = await firstTarget.worker();
    expect(await first.evaluate(async () => (await chrome.management.getSelf()).installType)).toBe("development");

    const history = await browser.newPage();
    await history.goto(`chrome-extension://${extensionId}/src/history.html`);
    // The worker records the stamp it started with; an unchanged stamp does nothing.
    await expect.poll(() => first.evaluate(async () => (await chrome.storage.session.get("winshot:dev-loaded-stamp"))["winshot:dev-loaded-stamp"] || null)).toBeTruthy();
    expect(await first.evaluate(async () => (await chrome.alarms.get("winshot-dev-reload"))?.periodInMinutes)).toBe(0.5);
    await first.evaluate(() => globalThis.__winshotTest.devReloadCheck());
    await new Promise((resolve) => setTimeout(resolve, 500));
    expect(firstTarget.worker && browser.targets().includes(firstTarget)).toBe(true);

    // A new build lands: the next check saves the open WinShot pages and reloads.
    // Puppeteer's Chrome disables an extension that reloads itself (a real
    // "Load unpacked" copy comes straight back), so the reload call is recorded here
    // and the reopen step is driven by hand below.
    await first.evaluate(() => {
      globalThis.__devReloadCalls = 0;
      Object.defineProperty(chrome.runtime, "reload", { configurable: true, writable: true, value: () => { globalThis.__devReloadCalls += 1; } });
    });
    const stampPath = path.join(copy, "src", "build-stamp.json");
    fs.writeFileSync(stampPath, JSON.stringify({ id: `test-${Date.now()}`, version: "test", commit: "test", builtAt: new Date().toISOString() }));
    await first.evaluate(() => globalThis.__winshotTest.devReloadCheck());
    expect(await first.evaluate(() => globalThis.__devReloadCalls)).toBe(1);
    const saved = await first.evaluate(async () => (await chrome.storage.local.get("winshot:dev-reopen-tabs"))["winshot:dev-reopen-tabs"]);
    expect(saved.map((page) => page.url)).toEqual([`chrome-extension://${extensionId}/src/history.html`]);

    // After the reload the new worker starts, reopens the saved pages once and records the
    // new stamp, so the same build does not reload again.
    await history.close();
    await first.evaluate(() => chrome.storage.session.clear());
    const reopened = browser.waitForTarget((target) => target.type() === "page" && target.url() === `chrome-extension://${extensionId}/src/history.html`, { timeout: 15_000 });
    expect(await first.evaluate(() => globalThis.__winshotTest.devReloadStart())).toBe(true);
    await reopened;
    expect(await first.evaluate(async () => (await chrome.storage.local.get("winshot:dev-reopen-tabs"))["winshot:dev-reopen-tabs"] ?? null)).toBeNull();
    expect(await first.evaluate(async () => (await chrome.storage.session.get("winshot:dev-loaded-stamp"))["winshot:dev-loaded-stamp"])).toMatch(/^test-/);
    await first.evaluate(() => globalThis.__winshotTest.devReloadCheck());
    expect(await first.evaluate(() => globalThis.__devReloadCalls)).toBe(1);

  } finally {
    await browser.close().catch(() => {});
    removeOwnedDirectory(profile, "winshot-dev-reload-profile-");
    removeOwnedDirectory(copy, "winshot-dev-reload-ext-");
  }
});
