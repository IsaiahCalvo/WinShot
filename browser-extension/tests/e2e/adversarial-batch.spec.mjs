// Adversarial QA: all-tabs and URL-list batch capture.
// Self-contained — replicates the launch/helper pattern from puppeteer-extension.spec.mjs
// (those helpers are not exported). Runs against the fixture server on 4173/4174 that
// playwright.config.mjs starts. Executed serially (workers: 1 in the shared config).

import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "node:fs";
import os from "node:os";

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionPath = process.env.WINSHOT_EXTENSION_PATH || path.resolve(here, "..", "..", "dist", "winshot-capture");
const FIX = "http://127.0.0.1:4173";

function pdfUtf16Hex(value) {
  return [...value].map((character) => {
    const codePoint = character.codePointAt(0);
    if (codePoint <= 0xffff) return codePoint.toString(16).padStart(4, "0");
    const adjusted = codePoint - 0x10000;
    return `${(0xd800 + (adjusted >> 10)).toString(16).padStart(4, "0")}${(0xdc00 + (adjusted & 0x3ff)).toString(16).padStart(4, "0")}`;
  }).join("").toUpperCase();
}

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
  const profileRoot = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-advbatch-e2e-"));
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
  if (!extension) {
    const found = [...extensions.values()].map((candidate) => `${candidate.name}:${candidate.id}`).join(", ") || "none";
    throw new Error(`The exact WinShot MV3 extension package did not load. Extensions found: ${found}`);
  }
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
  removeOwnedDirectory(run.profileRoot, "winshot-advbatch-e2e-");
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

function batchTarget(run, timeout = 180_000) {
  const existingTargets = new Set(run.browser.targets());
  return run.browser.waitForTarget(
    (target) => !existingTargets.has(target) && target.type() === "page" && target.url().includes(run.extension.id) && target.url().includes("/src/batch.html?id="),
    { timeout }
  );
}

async function readBatchSummary(run, batchId) {
  return await run.worker.evaluate(async (id) => {
    const stored = await chrome.storage.local.get(`batch:${id}`);
    return stored[`batch:${id}`] || null;
  }, batchId);
}

// Kick background-owned promises off in the service worker and poll them to settlement.
// (No dynamic code construction — extension service workers run under a strict CSP.)
async function startBatchTask(run, key, kind, urls, options) {
  await run.worker.evaluate((slot, batchKind, batchUrls, batchOptions) => {
    globalThis[slot] = undefined;
    globalThis.__winshotTest.runBatch(batchKind, batchUrls, batchOptions).then(
      (value) => { globalThis[slot] = { ok: value ?? true }; },
      (error) => { globalThis[slot] = { err: String((error && error.message) || error) }; }
    );
  }, key, kind, urls, options);
}

async function startCaptureTask(run, key, mode) {
  await run.worker.evaluate((slot, captureMode) => {
    globalThis[slot] = undefined;
    globalThis.__winshotTest.runCapture(captureMode, null, null, { openEditor: false }).then(
      (value) => { globalThis[slot] = { ok: value }; },
      (error) => { globalThis[slot] = { err: String((error && error.message) || error) }; }
    );
  }, key, mode);
}

async function settleWorkerTask(run, key, timeout = 150_000) {
  let outcome = null;
  await expect.poll(async () => {
    outcome = await run.worker.evaluate((slot) => globalThis[slot] ?? null, key);
    return outcome;
  }, { timeout, intervals: [250, 500, 1000] }).not.toBeNull();
  return outcome;
}

async function activeTabUrl(run) {
  return await run.worker.evaluate(async () => {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    return tab?.url || null;
  });
}

// ---------------------------------------------------------------------------

test("a parsed page with an unfinished hidden resource remains usable in an all-tabs batch", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    await page.setRequestInterception(true);
    page.on("request", request => {
      if (request.url().endsWith("/unfinished.png")) return;
      if (request.url().endsWith("/pending-resource")) return request.respond({ contentType: "text/html", body: '<!doctype html><title>READY DOCUMENT</title><h1>READY DOCUMENT</h1><img style="display:none" src="/unfinished.png">' });
      return request.continue();
    });
    await page.goto(`${FIX}/pending-resource`, { waitUntil: "domcontentloaded" });
    expect(await page.evaluate(() => document.readyState)).toBe("interactive");
    const target = batchTarget(run);
    const popup = await openPopup(run, page);
    await popup.select("#batch-mode", "visible");
    const start = performance.now();
    await popup.click("#all-tabs");
    const summary = await (await target).asPage();
    await summary.waitForSelector(".item.complete", { timeout: 10_000 });
    expect(performance.now() - start).toBeLessThan(10_000);
    const batch = await summary.evaluate(async () => {
      const key = `batch:${new URL(location.href).searchParams.get("id")}`;
      return (await chrome.storage.local.get(key))[key];
    });
    expect(batch.results).toHaveLength(1);
    expect(batch.results[0]).toMatchObject({ title: "READY DOCUMENT", state: "complete" });
  } finally { await closeExactExtension(run); }
});

test("all-tabs batch skips restricted tabs with honest reasons and renders an accurate results page", async () => {
  const run = await launchExactExtension();
  try {
    const restricted = await run.browser.newPage();
    await restricted.goto("chrome://version", { waitUntil: "domcontentloaded" });
    const first = await run.browser.newPage();
    const second = await run.browser.newPage();
    monitorPage(first, run.errors);
    monitorPage(second, run.errors);
    await first.goto(`${FIX}/body`, { waitUntil: "networkidle0" });
    await second.goto(`${FIX}/frame`, { waitUntil: "networkidle0" });
    await first.bringToFront();

    const summaryTarget = batchTarget(run);
    const popup = await openPopup(run, first);
    await popup.select("#batch-mode", "visible");
    await popup.click("#all-tabs");
    const summary = await (await summaryTarget).asPage();
    await summary.waitForSelector(".item.complete");

    const batchId = new URL(summary.url()).searchParams.get("id");
    const batch = await readBatchSummary(run, batchId);
    expect(batch).toBeTruthy();
    expect(batch.mode).toBe("visible");
    expect(batch.results).toHaveLength(2);
    expect(batch.results.every((item) => item.state === "complete" && item.captureId)).toBe(true);
    // The initial about:blank tab and chrome://version must be skipped, each with a stated reason.
    expect(batch.skipped).toHaveLength(2);
    expect(batch.skipped.every((item) => item.reason === "This browser page cannot be scripted.")).toBe(true);
    // Chrome hides chrome:// tab titles/urls from extensions without the optional "tabs"
    // permission, so the product falls back to an honest placeholder title.
    expect(batch.skipped.every((item) => typeof item.title === "string" && item.title.length > 0)).toBe(true);

    // Results page accuracy: the visible page must agree with the stored summary.
    const dom = await summary.evaluate(() => ({
      summaryText: document.querySelector("#summary").textContent,
      completeRows: document.querySelectorAll("#results .item.complete").length,
      failedRows: document.querySelectorAll("#results .item.failed").length,
      links: [...document.querySelectorAll("#results a")].map((anchor) => anchor.href),
      skippedRows: document.querySelectorAll("#skipped .item").length,
      skippedText: document.querySelector("#skipped").textContent,
      pdfDisabled: document.querySelector("#save-batch-pdf").disabled
    }));
    expect(dom.summaryText).toBe("2 of 2 captures completed in visible mode; 2 tabs or URLs skipped.");
    expect(dom.completeRows).toBe(2);
    expect(dom.failedRows).toBe(0);
    expect(dom.skippedRows).toBe(2);
    expect(dom.skippedText).toContain("This browser page cannot be scripted.");
    expect(dom.pdfDisabled).toBe(false);
    for (const item of batch.results) {
      expect(dom.links.some((href) => href.includes(encodeURIComponent(item.captureId)))).toBe(true);
    }
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("URL-list hygiene: duplicates, blank lines, and unsupported schemes are skipped with honest reasons", async () => {
  const run = await launchExactExtension();
  try {
    const first = await run.browser.newPage();
    monitorPage(first, run.errors);
    await first.goto(`${FIX}/body`, { waitUntil: "networkidle0" });
    await first.bringToFront();

    const summaryTarget = batchTarget(run);
    const popup = await openPopup(run, first);
    await popup.select("#batch-mode", "visible");
    // Duplicate (with stray whitespace), unsupported schemes, an empty line, and a whitespace-only line.
    await popup.type("#batch-urls", [
      `${FIX}/body`,
      `   ${FIX}/body   `,
      "ftp://example.com/file",
      "javascript:alert(1)",
      "chrome://settings",
      "",
      "   ",
      `${FIX}/frame`
    ].join("\n"));
    await popup.click("#url-batch");
    const summary = await (await summaryTarget).asPage();
    await summary.waitForSelector(".item.complete");

    const batchId = new URL(summary.url()).searchParams.get("id");
    const batch = await readBatchSummary(run, batchId);
    expect(batch.results, "duplicate and blank entries must collapse to two unique captures").toHaveLength(2);
    expect(batch.results.every((item) => item.state === "complete")).toBe(true);
    expect(batch.results.map((item) => item.url).sort()).toEqual([`${FIX}/body`, `${FIX}/frame`]);
    expect(batch.skipped).toHaveLength(3);
    expect(batch.skipped.map((item) => item.title).sort()).toEqual(["chrome://settings", "ftp://example.com/file", "javascript:alert(1)"]);
    expect(batch.skipped.every((item) => item.reason === "Only http:// and https:// URLs are supported.")).toBe(true);
    expect(await summary.$eval("#summary", (node) => node.textContent))
      .toBe("2 of 2 captures completed in visible mode; 3 tabs or URLs skipped.");

    // The disposable tabs must be gone; the original /body tab must survive.
    const pages = await run.browser.pages();
    expect(pages.filter((page) => page.url() === `${FIX}/frame`)).toHaveLength(0);
    expect(pages.filter((page) => page.url() === `${FIX}/body`)).toHaveLength(1);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("URL batch enforces the 20-URL cap with explicit messaging and closes every disposable tab", async () => {
  test.setTimeout(180_000);
  const run = await launchExactExtension();
  try {
    const first = await run.browser.newPage();
    monitorPage(first, run.errors);
    await first.goto(`${FIX}/body`, { waitUntil: "networkidle0" });
    await first.bringToFront();

    const urls = Array.from({ length: 22 }, (_, index) => `${FIX}/cap-${index}`);
    const summaryTarget = batchTarget(run);
    await startBatchTask(run, "__advCapBatch", "urls", urls, { settings: { batchMode: "visible" } });
    const outcome = await settleWorkerTask(run, "__advCapBatch", 160_000);
    expect(outcome.err, `batch rejected: ${outcome.err}`).toBeUndefined();
    const diagnostic = await readBatchSummary(run, outcome.ok.batchId);
    expect(outcome.ok.complete, JSON.stringify(diagnostic)).toBe(20);
    expect(outcome.ok.failed).toBe(0);
    expect(outcome.ok.skipped).toBe(1);

    const batch = await readBatchSummary(run, outcome.ok.batchId);
    expect(batch.results).toHaveLength(20);
    expect(batch.results.every((item) => item.state === "complete")).toBe(true);
    // The first 20 in submitted order are captured; 21 and 22 fall to the cap.
    expect(batch.results.map((item) => item.url)).toEqual(urls.slice(0, 20));
    expect(batch.skipped).toHaveLength(1);
    expect(batch.skipped[0].reason).toMatch(/20-URL safety limit/);

    const summary = await (await summaryTarget).asPage();
    await summary.waitForSelector(".item.complete");
    expect(await summary.$eval("#summary", (node) => node.textContent)).toContain("20 of 20 captures completed");

    const leftover = (await run.browser.pages()).filter((page) => page.url().includes("/cap-"));
    expect(leftover, "every disposable batch tab must be closed").toHaveLength(0);
    // tabs.query scrubs even the extension's own chrome-extension:// tab URLs without the
    // optional "tabs" permission, so the batch tab's url is invisible to activeTabUrl().
    // Assert activeness from the batch page itself: tab.id/active are never scrubbed.
    await expect.poll(
      () => summary.evaluate(async () => (await chrome.tabs.getCurrent())?.active === true),
      { timeout: 15_000 }
    ).toBe(true);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("combined PDF is still produced when a batch member fails, and the failure is reported honestly", async () => {
  test.setTimeout(180_000);
  const run = await launchExactExtension();
  const downloadRoot = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-advbatch-dl-"));
  try {
    const first = await run.browser.newPage();
    await first.goto(`${FIX}/body`, { waitUntil: "networkidle0" });
    await first.bringToFront();
    const session = await first.createCDPSession();
    await session.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: downloadRoot });

    // Second URL points at a port nothing listens on: the tab loads a browser error
    // page and the full-page capture must fail rather than pretend success.
    const urls = [`${FIX}/body`, "http://127.0.0.1:4179/refused"];
    const summaryTarget = batchTarget(run);
    await startBatchTask(run, "__advPdfBatch", "urls", urls, { combinePdf: true, settings: { batchMode: "full-page" } });
    const outcome = await settleWorkerTask(run, "__advPdfBatch", 160_000);
    expect(outcome.err, `batch rejected: ${outcome.err}`).toBeUndefined();
    expect(outcome.ok.complete).toBe(1);
    expect(outcome.ok.failed).toBe(1);

    const batch = await readBatchSummary(run, outcome.ok.batchId);
    expect(batch.combinePdf).toBe(true);
    expect(batch.results).toHaveLength(2);
    const good = batch.results.find((item) => item.url === `${FIX}/body`);
    const bad = batch.results.find((item) => item.url !== `${FIX}/body`);
    expect(good.state).toBe("complete");
    expect(good.captureId).toBeTruthy();
    expect(bad.state).toBe("failed");
    expect(typeof bad.error).toBe("string");
    expect(bad.error.length).toBeGreaterThan(0);

    const summary = await (await summaryTarget).asPage();
    await summary.waitForSelector(".item.failed");
    const failedRowError = await summary.$eval(".item.failed small", (node) => node.textContent);
    expect(failedRowError.length).toBeGreaterThan(0);

    // combinePdf on the results page must auto-build the PDF from the surviving member.
    await summary.waitForFunction(
      () => /Combined PDF ready/i.test(document.querySelector("#pdf-status")?.textContent || ""),
      { timeout: 90_000 }
    );
    await expect.poll(() => fs.readdirSync(downloadRoot).find((name) => name.endsWith(".pdf")), { timeout: 30_000 }).toBeTruthy();
    const pdfName = fs.readdirSync(downloadRoot).find((name) => name.endsWith(".pdf"));
    expect(pdfName).toContain("winshot-urls-");
    const pdf = fs.readFileSync(path.join(downloadRoot, pdfName));
    expect(pdf.subarray(0, 8).toString("ascii")).toBe("%PDF-1.7");
    expect(pdf.subarray(-5).toString("ascii")).toBe("%%EOF");
    // Searchable text from the surviving capture proves the PDF was built from real content.
    expect(pdf.toString("latin1")).toContain(pdfUtf16Hex("SEARCHABLE WINSHOT DOCUMENT"));
  } finally {
    removeOwnedDirectory(downloadRoot, "winshot-advbatch-dl-");
    await closeExactExtension(run);
  }
});

test("a tab closed manually mid-batch fails honestly without derailing the remaining captures", async () => {
  test.setTimeout(180_000);
  const run = await launchExactExtension();
  try {
    const first = await run.browser.newPage();
    const second = await run.browser.newPage();
    const third = await run.browser.newPage();
    monitorPage(first, run.errors);
    monitorPage(second, run.errors);
    await first.goto(`${FIX}/frame`, { waitUntil: "networkidle0" });
    await second.goto(`${FIX}/body`, { waitUntil: "networkidle0" });
    await third.goto(`${FIX}/sticky`, { waitUntil: "networkidle0" });
    await first.bringToFront();

    const summaryTarget = batchTarget(run);
    const popup = await openPopup(run, first);
    await popup.select("#batch-mode", "full-page");
    await popup.click("#all-tabs");
    // Wait until the first tab's capture is actually underway (freeze style attached),
    // which guarantees the batch already snapshotted its tab list — then kill tab 3.
    await first.waitForSelector("[data-winshot-capture]", { timeout: 15_000 });
    await third.close();

    const summary = await (await summaryTarget).asPage();
    await summary.waitForSelector(".item");
    const batchId = new URL(summary.url()).searchParams.get("id");
    const batch = await readBatchSummary(run, batchId);
    expect(batch.results).toHaveLength(3);
    expect(batch.results[0].state, batch.results[0].error).toBe("complete");
    expect(batch.results[1].state, batch.results[1].error).toBe("complete");
    expect(batch.results[2].state).toBe("failed");
    expect(batch.results[2].error).toMatch(/changed or closed/i);
    expect(await summary.$$eval("#results .item.complete", (nodes) => nodes.length)).toBe(2);
    expect(await summary.$$eval("#results .item.failed", (nodes) => nodes.length)).toBe(1);
    expect(await summary.$eval("#summary", (node) => node.textContent)).toContain("2 of 3 captures completed");
  } finally {
    await closeExactExtension(run);
  }
});

test("cancel mid-batch closes disposable tabs, restores the original tab, and releases capture locks", async () => {
  test.setTimeout(180_000);
  const run = await launchExactExtension();
  try {
    const original = await run.browser.newPage();
    monitorPage(original, run.errors);
    await original.goto(`${FIX}/body`, { waitUntil: "networkidle0" });
    await original.bringToFront();

    const disposableTarget = run.browser.waitForTarget(
      (target) => target.type() === "page" && target.url() === `${FIX}/sticky`,
      { timeout: 30_000 }
    );
    await startBatchTask(run, "__advCancelBatch", "urls", [`${FIX}/sticky`, `${FIX}/horizontal`], { settings: { batchMode: "full-page" } });

    // Cancel while the first disposable tab is mid-capture.
    const disposable = await (await disposableTarget).asPage();
    await disposable.waitForSelector("[data-winshot-capture]", { timeout: 20_000 });
    await run.worker.evaluate(async () => await globalThis.__winshotTest.cancelActiveCapture());

    const outcome = await settleWorkerTask(run, "__advCancelBatch", 60_000);
    expect(outcome.err, "cancelling must reject the batch instead of fabricating a report").toBeTruthy();
    expect(outcome.err).toMatch(/cancel/i);

    // Disposable tabs are closed, the original tab is active again, and no report page opened.
    await expect.poll(async () =>
      (await run.browser.pages()).filter((page) => /\/(sticky|horizontal)$/.test(page.url())).length,
    { timeout: 15_000 }).toBe(0);
    await expect.poll(() => activeTabUrl(run), { timeout: 15_000 }).toBe(`${FIX}/body`);
    expect(run.browser.targets().some((target) => target.url().includes("/src/batch.html"))).toBe(false);

    // Both the per-window lock and the batch slot must be free again.
    const followUp = await run.worker.evaluate(async () =>
      await globalThis.__winshotTest.runCapture("visible", null, null, { openEditor: false }));
    expect(followUp.state, followUp.error).toBe("complete");
  } finally {
    await closeExactExtension(run);
  }
});

test("starting a batch while a capture is running must not destroy the in-progress capture", async () => {
  test.setTimeout(180_000);
  const run = await launchExactExtension();
  try {
    // Two web tabs: the capture runs in the second; the batch will want to visit both.
    const other = await run.browser.newPage();
    await other.goto(`${FIX}/body`, { waitUntil: "networkidle0" });
    const capturing = await run.browser.newPage();
    await capturing.goto(`${FIX}/sticky`, { waitUntil: "networkidle0" });
    await capturing.bringToFront();

    await startCaptureTask(run, "__advLockCapture", "full-page");
    await capturing.waitForSelector("[data-winshot-capture]", { timeout: 15_000 });

    // Launch an all-tabs batch while the capture is running. Whatever the batch decides
    // to do (refuse, queue, or fail its members honestly), it must never activate other
    // tabs under the running capture and corrupt or kill it.
    await startBatchTask(run, "__advLockBatch", "all-tabs", [], { settings: { batchMode: "visible" } });
    const batchOutcome = await settleWorkerTask(run, "__advLockBatch", 120_000);
    const captureOutcome = await settleWorkerTask(run, "__advLockCapture", 120_000);

    expect(captureOutcome.err, `runCapture rejected: ${captureOutcome.err}`).toBeUndefined();
    expect(
      captureOutcome.ok.state,
      `a batch launched during an active capture destroyed it (capture error: ${captureOutcome.ok.error}; ` +
      `batch outcome: ${JSON.stringify(batchOutcome)})`
    ).toBe("complete");
  } finally {
    await closeExactExtension(run);
  }
});
