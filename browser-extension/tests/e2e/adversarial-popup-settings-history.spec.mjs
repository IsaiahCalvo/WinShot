// Adversarial QA audit: popup, settings, hotkeys, badge, context menu, history.
// Launch/helper pattern replicated from puppeteer-extension.spec.mjs (not exported there).
//
// Two tests started life as EXPECTED-BUG exposers; both defects are now fixed
// in the product and the tests remain as regression cover for the fixes:
//   - deleting a history record mid-capture now sticks (capture aborts, record
//     stays deleted, no editor opens — background.js updateRecord guards)
//   - a hotkey capture started while a picker overlay is open dismisses the
//     overlay first instead of capturing/stranding it

import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "node:fs";
import os from "node:os";

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionPath = process.env.WINSHOT_EXTENSION_PATH || path.resolve(here, "..", "..", "dist", "winshot-capture");
const SETTINGS_KEY = "winshot:capture-settings";

function removeOwnedDirectory(directory, prefix) {
  const resolved = path.resolve(directory);
  const temp = path.resolve(os.tmpdir());
  if (path.dirname(resolved) !== temp || !path.basename(resolved).startsWith(prefix)) {
    throw new Error(`Refusing to clean unexpected test path: ${resolved}`);
  }
  fs.rmSync(resolved, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
}

function monitorPage(page, errors) {
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(`console: ${message.text()}`);
  });
  page.on("pageerror", (error) => errors.push(`pageerror: ${error.message}`));
}

async function launchExactExtension() {
  const profileRoot = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-adversarial-e2e-"));
  const errors = [];
  const browser = await launchCaptureBrowser(profileRoot);
  browser.on("targetcreated", async (target) => {
    if (target.type() !== "page") return;
    const page = await target.page().catch(() => null);
    if (page) monitorPage(page, errors);
  });
  const extensionId = await browser.installExtension(extensionPath);
  const extensions = await browser.extensions();
  const extension = extensions.get(extensionId);
  if (!extension) throw new Error("The exact WinShot MV3 extension package did not load.");
  const workerTarget = await browser.waitForTarget(
    (target) => target.type() === "service_worker" && target.url().endsWith("/src/background.js"),
    { timeout: 15_000 }
  );
  const worker = await workerTarget.worker();
  worker.on("console", (message) => {
    if (message.type() === "error") errors.push(`worker console: ${message.text()}`);
  });
  return { browser, extension, worker, profileRoot, errors };
}

async function closeExactExtension(run) {
  await run.browser.close();
  removeOwnedDirectory(run.profileRoot, "winshot-adversarial-e2e-");
}

async function openPopup(run, fixturePage) {
  await fixturePage.bringToFront();
  const existingTargets = new Set(run.browser.targets());
  const popupTarget = run.browser.waitForTarget(
    (target) => !existingTargets.has(target) && target.type() === "page" && target.url().includes(run.extension.id) && target.url().endsWith("/src/popup.html"),
    { timeout: 10_000 }
  );
  await run.extension.triggerAction(fixturePage);
  const popup = await (await popupTarget).asPage();
  monitorPage(popup, run.errors);
  await popup.waitForSelector("[data-mode='full-page']");
  return popup;
}

// Extension page loaded as a regular tab — needed for IndexedDB access via store.js
// (dynamic import is unavailable inside the MV3 service worker).
async function openExtensionPage(run, file = "src/popup.html") {
  const page = await run.browser.newPage();
  await page.goto(`chrome-extension://${run.extension.id}/${file}`, { waitUntil: "domcontentloaded" });
  return page;
}

function editorTarget(run) {
  const existingTargets = new Set(run.browser.targets());
  return run.browser.waitForTarget(
    (target) => !existingTargets.has(target) && target.type() === "page" && target.url().includes(run.extension.id) && target.url().includes("/src/editor.html?id="),
    { timeout: 120_000 }
  );
}

async function recordFromEditorTarget(target) {
  const editor = await target.asPage();
  await editor.waitForSelector("#state:not(:empty)");
  const captureId = new URL(editor.url()).searchParams.get("id");
  const record = await editor.evaluate(async (id) => {
    const store = await import(chrome.runtime.getURL("src/store.js"));
    return await store.getRecord(id);
  }, captureId);
  return { editor, record };
}

function badgeText(run) {
  return run.worker.evaluate(() => chrome.action.getBadgeText({}));
}

function storedLastMode(run) {
  return run.worker.evaluate(async (key) => (await chrome.storage.local.get(key))[key]?.lastMode, SETTINGS_KEY);
}

// A minimal but complete record shape matching contract.js createSession output,
// so history.js and editor.js render it exactly as they would a real capture.
// Serialized as plain overrides and expanded inside the extension page (its CSP
// forbids eval, so the factory is defined inline in each evaluate call).
function seedOverrides(overrides) {
  return {
    id: overrides.id,
    mode: overrides.mode || "full-page",
    sourceTitle: overrides.sourceTitle || overrides.id,
    startedAt: overrides.startedAt ?? new Date().toISOString(),
    finishedAt: overrides.finishedAt ?? null,
    state: overrides.state || "complete",
    error: overrides.error ?? null
  };
}

// Body of the in-page factory shared by the seeding tests.
async function seedRecords(page, overridesList) {
  await page.evaluate(async (list) => {
    const store = await import(chrome.runtime.getURL("src/store.js"));
    for (const item of list) {
      await store.putRecord({
        protocolVersion: 1,
        id: item.id,
        mode: item.mode,
        tabId: 1,
        windowId: 1,
        sourceUrl: "http://127.0.0.1:4173/seeded",
        sourceTitle: item.sourceTitle,
        target: null,
        limits: {},
        startedAt: item.startedAt,
        finishedAt: item.finishedAt,
        state: item.state,
        error: item.error,
        report: {
          target: item.mode,
          mode: item.mode,
          dimensionsCss: null,
          dimensionsPx: null,
          tileCount: 0,
          seamConfidence: null,
          warnings: item.error ? [item.error] : [],
          fallbackUsed: null,
          restoration: "pending",
          coverage: null
        }
      });
    }
  }, overridesList);
}

test("direct-storage garbage settings are clamped on load and the popup repeat row follows lastMode", async () => {
  const run = await launchExactExtension();
  try {
    await run.worker.evaluate(async (key) => {
      await chrome.storage.local.set({
        [key]: {
          delayMs: -50_000,
          maxDurationMs: "garbage",
          maxTiles: 999_999_999,
          maxHeightCss: -5,
          historyRetentionDays: "nope",
          lastMode: "bogus",
          batchMode: "weird",
          fileNameCounter: -3,
          fileNameCounterPad: 99,
          captureInfiniteGrowth: null,
          contextMenu: "yes"
        }
      });
    }, SETTINGS_KEY);

    const popup = await openExtensionPage(run);
    const normalized = await popup.evaluate(async () => {
      const settings = await import(chrome.runtime.getURL("src/settings.js"));
      return await settings.loadSettings();
    });
    expect(normalized).toMatchObject({
      delayMs: 0,                 // -50000 clamped up
      maxDurationMs: 300_000,     // NaN falls back to the default
      maxTiles: 2000,             // huge value clamped down
      maxHeightCss: 1000,         // negative clamped up to the floor
      historyRetentionDays: 30,   // NaN falls back to the default
      lastMode: "full-page",      // invalid mode falls back
      batchMode: "full-page",     // invalid batch mode falls back
      fileNameCounter: 0,
      fileNameCounterPad: 10,
      captureInfiniteGrowth: true,
      contextMenu: true
    });

    // The popup UI must reflect the clamped values, not the stored garbage.
    await popup.waitForSelector("#repeat-label:not(:empty)");
    const shown = await popup.evaluate(() => ({
      delay: document.querySelector("#capture-delay").value,
      timeout: document.querySelector("#capture-timeout").value,
      height: document.querySelector("#capture-height").value,
      tiles: document.querySelector("#capture-tiles").value,
      retention: document.querySelector("#history-retention").value,
      batchMode: document.querySelector("#batch-mode").value,
      repeat: document.querySelector("#repeat-label").textContent
    }));
    expect(shown).toEqual({
      delay: "0",
      timeout: "5",
      height: "1000",
      tiles: "2000",
      retention: "30",
      batchMode: "full-page",
      repeat: "Entire page"
    });

    // Repeat-last row follows a valid lastMode written by another surface.
    await popup.evaluate(async (key) => { await chrome.storage.local.set({ [key]: { lastMode: "region" } }); }, SETTINGS_KEY);
    await popup.reload({ waitUntil: "domcontentloaded" });
    await popup.waitForSelector("#repeat-label:not(:empty)");
    expect(await popup.$eval("#repeat-label", (node) => node.textContent)).toBe("Drag a selection");
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("hotkey commands dispatch: full-page captures, region opens a picker, Escape cancels, lastMode follows", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    await page.bringToFront();

    // capture-full-page runs a real scrolling capture through the command handler.
    const waitingEditor = editorTarget(run);
    await run.worker.evaluate(() => globalThis.__winshotTest.handleCommand("capture-full-page"));
    const { editor, record } = await recordFromEditorTarget(await waitingEditor);
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.mode).toBe("full-page");
    expect(record.report.tileCount).toBeGreaterThan(1);
    expect(await storedLastMode(run)).toBe("full-page");
    await editor.close();

    // capture-region opens the region picker overlay on the active tab.
    await page.bringToFront();
    const started = await run.worker.evaluate(() => globalThis.__winshotTest.handleCommand("capture-region"));
    expect(started?.started).toBe(true);
    await page.waitForSelector("[data-winshot-picker='tip']", { timeout: 10_000 });
    expect(await storedLastMode(run)).toBe("region");

    // Escape cancels the picker and removes every overlay element.
    await page.keyboard.press("Escape");
    await page.waitForFunction(() => !document.querySelector("[data-winshot-picker]"), { timeout: 10_000 });

    // The popup repeat row now advertises the region mode.
    const popup = await openExtensionPage(run);
    await popup.waitForSelector("#repeat-label:not(:empty)");
    expect(await popup.$eval("#repeat-label", (node) => node.textContent)).toBe("Drag a selection");
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("Ctrl+Shift+S repeats when idle, stops a running capture, and the badge is set and cleared on cancel and failure", async () => {
  const run = await launchExactExtension();
  try {
    await run.worker.evaluate(async (key) => { await chrome.storage.local.set({ [key]: { lastMode: "visible" } }); }, SETTINGS_KEY);
    const page = await run.browser.newPage();
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    await page.bringToFront();

    // Idle: last-used-action repeats the last mode (visible).
    let waitingEditor = editorTarget(run);
    await run.worker.evaluate(() => globalThis.__winshotTest.handleCommand("last-used-action"));
    const repeat = await recordFromEditorTarget(await waitingEditor);
    expect(repeat.record, repeat.record.error).toMatchObject({ state: "complete" });
    expect(repeat.record.report.mode).toBe("visible");
    expect(repeat.record.report.tileCount).toBe(1);
    await repeat.editor.close();

    // Running: the same command stops the capture instead of starting another.
    await page.bringToFront();
    await page.goto("http://127.0.0.1:4173/long", { waitUntil: "networkidle0" });
    await run.worker.evaluate(() => { globalThis.__winshotTest.handleCommand("capture-full-page").catch(() => {}); return true; });
    await page.waitForSelector("[data-winshot-capture]", { timeout: 15_000 });
    await expect.poll(() => badgeText(run), { timeout: 15_000 }).not.toBe("");
    const stopped = await run.worker.evaluate(() => globalThis.__winshotTest.handleCommand("last-used-action"));
    expect(stopped?.cancelled).toBe(true);
    await page.waitForFunction(() => !document.querySelector("[data-winshot-capture]"), { timeout: 15_000 });
    await expect.poll(() => badgeText(run), { timeout: 15_000 }).toBe("");
    // A cancelled capture must not open an editor tab.
    await new Promise((resolve) => setTimeout(resolve, 1_000));
    expect(run.browser.targets().some((target) => target.url().includes("/src/editor.html"))).toBe(false);

    // Failure: navigation aborts the capture and the badge must clear too.
    await page.bringToFront();
    waitingEditor = editorTarget(run);
    await run.worker.evaluate(() => { globalThis.__winshotTest.handleCommand("capture-full-page").catch(() => {}); return true; });
    await page.waitForSelector("[data-winshot-capture]", { timeout: 15_000 });
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    const failed = await recordFromEditorTarget(await waitingEditor);
    expect(failed.record.state).toBe("failed");
    expect(failed.record.error).toMatch(/navigated during capture/i);
    await expect.poll(() => badgeText(run), { timeout: 15_000 }).toBe("");

    // History bookkeeping: cancelled capture has no thumbnail (fallback path) and the run left 3 records.
    const summaries = await failed.editor.evaluate(async () => {
      const store = await import(chrome.runtime.getURL("src/store.js"));
      const records = await store.listRecords();
      return records.map((record) => ({ state: record.state, mode: record.report?.mode, hasThumbnail: Boolean(record.thumbnail) }));
    });
    expect(summaries).toHaveLength(3);
    expect(summaries.filter((item) => item.state === "cancelled")).toHaveLength(1);
    expect(summaries.find((item) => item.state === "cancelled").hasThumbnail).toBe(false);
  } finally {
    await closeExactExtension(run);
  }
});

test("Alt-hold on a popup capture button runs a one-off delayed capture without persisting the delay", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    const waitingEditor = editorTarget(run);
    const popup = await openPopup(run, page);
    expect(await popup.$eval("#capture-delay", (node) => node.value)).toBe("0");

    const started = Date.now();
    await popup.keyboard.down("Alt");
    await popup.click("[data-mode='visible']");
    await popup.keyboard.up("Alt");
    await popup.waitForFunction(
      () => /Capture starts in 5 seconds/.test(document.querySelector("#status")?.textContent || ""),
      { timeout: 5_000 }
    );
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(Date.now() - started).toBeGreaterThanOrEqual(4_500);
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.tileCount).toBe(1);

    // The 5-second Alt delay is one-off: stored settings still say no delay.
    // (Read via a fresh extension page — the action popup may auto-close when
    // the editor tab takes focus.)
    const reader = await openExtensionPage(run);
    const storedDelay = await reader.evaluate(async () => {
      const settings = await import(chrome.runtime.getURL("src/settings.js"));
      return (await settings.loadSettings()).delayMs;
    });
    expect(storedDelay).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("history select-all spans filter and pagination, thumbnail falls back, and a failed record opens a disabled editor", async () => {
  const run = await launchExactExtension();
  try {
    // Seed 60 filter-matching records (more than one 50-row page), 5 others, and one failed record.
    const seeder = await openExtensionPage(run);
    const base = Date.now();
    const seeds = [];
    for (let index = 0; index < 60; index++) {
      seeds.push(seedOverrides({
        id: `bulk-${index}`,
        startedAt: new Date(base - index * 1000).toISOString(),
        finishedAt: new Date(base - index * 1000 + 500).toISOString()
      }));
    }
    for (let index = 0; index < 5; index++) {
      seeds.push(seedOverrides({
        id: `other-${index}`,
        startedAt: new Date(base - 100_000 - index * 1000).toISOString(),
        finishedAt: new Date(base - 100_000 - index * 1000 + 500).toISOString()
      }));
    }
    seeds.push(seedOverrides({
      id: "fail-target",
      state: "failed",
      error: "Boom - capture failed for test",
      startedAt: new Date(base - 200_000).toISOString(),
      finishedAt: new Date(base - 199_000).toISOString()
    }));
    await seedRecords(seeder, seeds);

    const history = await openExtensionPage(run, "src/history.html");
    await history.waitForFunction(() => document.querySelectorAll(".row").length === 50, { timeout: 15_000 });
    expect(await history.$eval("#load-more", (node) => node.hidden)).toBe(false);

    // Filter to the 60 "bulk" records; only 50 render, select-all must still cover all 60.
    await history.type("#filter", "bulk");
    await history.waitForFunction(() =>
      [...document.querySelectorAll(".row .title")].every((node) => node.textContent.startsWith("bulk-")), { timeout: 10_000 });
    expect(await history.$$eval(".row", (rows) => rows.length)).toBe(50);
    await history.click("#select-all");
    expect(await history.$eval("#delete-selected", (node) => node.textContent)).toBe("Delete selected [60]");

    await history.click("#delete-selected");
    await history.waitForFunction(() => document.querySelectorAll(".row").length === 0 && !document.querySelector("#empty").hidden, { timeout: 30_000 });

    // Clearing the filter reveals exactly the 6 untouched records — proof the 10
    // unrendered pagination rows were deleted and nothing else was.
    await history.$eval("#filter", (node) => { node.value = ""; node.dispatchEvent(new Event("input", { bubbles: true })); });
    await history.waitForFunction(() => document.querySelectorAll(".row").length === 6, { timeout: 10_000 });
    const remaining = await history.$$eval(".row .title", (nodes) => nodes.map((node) => node.textContent).sort());
    expect(remaining).toEqual(["fail-target", "other-0", "other-1", "other-2", "other-3", "other-4"]);

    // Thumbnail fallback: no Blob thumbnail means a state-text placeholder, not a broken image.
    const failedRow = await history.evaluate(() => {
      const row = [...document.querySelectorAll(".row")].find((node) => node.querySelector(".title").textContent === "fail-target");
      return { hasImage: Boolean(row.querySelector("img")), placeholder: row.querySelector(".no-thumb")?.textContent, pill: row.querySelector(".pill")?.textContent };
    });
    expect(failedRow).toEqual({ hasImage: false, placeholder: "failed", pill: "failed" });

    // Opening the failed record from history must yield a safe editor: no export buttons enabled.
    await Promise.all([
      history.waitForNavigation({ waitUntil: "domcontentloaded" }),
      history.evaluate(() => {
        [...document.querySelectorAll(".row")].find((node) => node.querySelector(".title").textContent === "fail-target").click();
      })
    ]);
    await history.waitForSelector("#state:not(:empty)");
    const editorView = await history.evaluate(() => ({
      state: document.querySelector("#state").textContent,
      preview: document.querySelector("#preview").textContent,
      warningsHidden: document.querySelector("#warnings").hidden,
      warningText: document.querySelector("#warnings").textContent,
      disabled: [...document.querySelectorAll("header button")].map((button) => button.disabled)
    }));
    expect(editorView.state).toBe("failed");
    expect(editorView.preview).toContain("No verified pixels");
    expect(editorView.warningsHidden).toBe(false);
    expect(editorView.warningText).toContain("Boom - capture failed for test");
    expect(editorView.disabled.every(Boolean)).toBe(true);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("retention pruning removes strictly-older records, keeps the exact-boundary record, and sweeps orphan tiles", async () => {
  const run = await launchExactExtension();
  try {
    const page = await openExtensionPage(run);
    const cutoff = "2026-01-01T00:00:00.000Z";
    await seedRecords(page, [
      seedOverrides({ id: "prune-old", startedAt: "2025-11-01T00:00:00.000Z", finishedAt: "2025-12-31T23:59:59.999Z" }),
      seedOverrides({ id: "prune-boundary", startedAt: "2025-12-31T00:00:00.000Z", finishedAt: cutoff }),
      seedOverrides({ id: "prune-recent", startedAt: "2026-02-01T00:00:00.000Z", finishedAt: "2026-02-01T00:01:00.000Z" }),
      // Interrupted long ago: no finishedAt, old startedAt — prunable by startedAt.
      seedOverrides({ id: "prune-interrupted", state: "capturing", startedAt: "2025-11-01T00:00:00.000Z", finishedAt: null }),
      // No timestamps at all: must be kept, never treated as ancient.
      seedOverrides({ id: "prune-unstamped", startedAt: "", finishedAt: null })
    ]);
    const result = await page.evaluate(async (cutoffIso) => {
      const store = await import(chrome.runtime.getURL("src/store.js"));
      await store.putTile({ captureId: "prune-old", index: 0, blob: new Blob(["x"]) });
      await store.putTile({ captureId: "prune-boundary", index: 0, blob: new Blob(["x"]) });
      await store.putTile({ captureId: "ghost-capture", index: 0, blob: new Blob(["x"]) });

      const removed = await store.pruneOlderThan(cutoffIso);
      const records = (await store.listRecords()).map((record) => record.id).sort();
      return {
        removed,
        records,
        oldTiles: (await store.getTiles("prune-old")).length,
        boundaryTiles: (await store.getTiles("prune-boundary")).length,
        ghostTiles: (await store.getTiles("ghost-capture")).length
      };
    }, cutoff);

    expect(result.removed).toBe(2);
    expect(result.records).toEqual(["prune-boundary", "prune-recent", "prune-unstamped"]);
    expect(result.oldTiles).toBe(0);      // pruned record's tiles removed
    expect(result.boundaryTiles).toBe(1); // boundary record keeps its tiles
    expect(result.ghostTiles).toBe(0);    // orphan tiles with no record swept
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("regression: deleting a capture from history mid-run sticks — no resurrected record, no stray tiles, no editor", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    await page.goto("http://127.0.0.1:4173/long", { waitUntil: "networkidle0" });
    const popup = await openPopup(run, page);
    await popup.click("[data-mode='full-page']");

    // Wait until at least 3 tiles are stored, then delete the record the way
    // the history page does while the capture is still running.
    await expect.poll(() => popup.evaluate(async () => {
      const store = await import(chrome.runtime.getURL("src/store.js"));
      const [record] = await store.listRecords();
      return record?.report?.tileCount || 0;
    }), { timeout: 60_000 }).toBeGreaterThanOrEqual(3);
    const captureId = await popup.evaluate(async () => {
      const store = await import(chrome.runtime.getURL("src/store.js"));
      const [record] = await store.listRecords();
      await store.deleteCapture(record.id);
      return record.id;
    });

    // The fixed product aborts the capture (CANCELLED) and deliberately does NOT
    // open an editor tab, so "capture finished" is detected via the action badge,
    // which clears only once no capture is active.
    await expect.poll(() => badgeText(run), { timeout: 90_000 }).toBe("");

    // Invariant: the user's delete sticks — the record must not be resurrected
    // and every tile written after the delete must be swept. (Read storage from a
    // fresh extension page: the popup can auto-close if focus moves.)
    const checker = await openExtensionPage(run);
    const consistency = await checker.evaluate(async (id) => {
      const store = await import(chrome.runtime.getURL("src/store.js"));
      const record = await store.getRecord(id);
      const tiles = await store.getTiles(id);
      return { record: record ?? null, orphanTiles: tiles.length };
    }, captureId);
    expect(consistency.record,
      `Record ${captureId} was resurrected as "${consistency.record?.state}" after a mid-capture delete`
    ).toBeNull();
    expect(consistency.orphanTiles, `Tiles written after the delete were not swept`).toBe(0);
    // And no editor tab appeared for the cancelled capture.
    const editorTabs = run.browser.targets().filter((t) => t.type() === "page" && t.url().includes("/src/editor.html?id="));
    expect(editorTabs).toHaveLength(0);
  } finally {
    await closeExactExtension(run);
  }
});

test("regression: a hotkey capture started while a picker overlay is open must not capture or strand the overlay", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });

    // Open the element picker, then fire the visible-capture hotkey without picking.
    const popup = await openPopup(run, page);
    await popup.click("[data-picker='element']");
    await page.waitForSelector("[data-winshot-picker='tip']", { timeout: 10_000 });
    await page.bringToFront();

    const waitingEditor = editorTarget(run);
    await run.worker.evaluate(() => globalThis.__winshotTest.handleCommand("capture-visible"));
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(record, record.error).toMatchObject({ state: "complete" });

    // Starting a capture is part of the capture flow: the picker overlay must be
    // dismissed before pixels are taken. If it is still in the DOM now, it was in
    // the DOM during captureVisibleTab too — the tip/outline are burned into the
    // screenshot and the page is stuck behind a modal event-swallower.
    const overlay = await page.evaluate(() => document.querySelectorAll("[data-winshot-picker]").length);
    expect(overlay, "picker overlay elements still present after a hotkey capture — they were captured into the screenshot and keep swallowing page clicks").toBe(0);
  } finally {
    await closeExactExtension(run);
  }
});
