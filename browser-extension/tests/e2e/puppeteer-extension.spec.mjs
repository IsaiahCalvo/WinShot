import { test, expect } from "@playwright/test";
import puppeteer from "puppeteer";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "node:fs";
import os from "node:os";

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionPath = path.resolve(here, "..", "..", "dist", "winshot-capture");

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
  const profileRoot = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-extension-e2e-"));
  const errors = [];
  const browser = await puppeteer.launch({
    executablePath: await puppeteer.executablePath(),
    headless: true,
    userDataDir: profileRoot,
    enableExtensions: true,
    defaultViewport: { width: 1200, height: 1000 },
    args: ["--window-size=1200,1000"]
  });
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
  removeOwnedDirectory(run.profileRoot, "winshot-extension-e2e-");
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

async function captureFromPopup(run, fixturePage, selector) {
  const existingTargets = new Set(run.browser.targets());
  const editorTarget = run.browser.waitForTarget(
    (target) => !existingTargets.has(target) && target.type() === "page" && target.url().includes(run.extension.id) && target.url().includes("/src/editor.html?id="),
    { timeout: 120_000 }
  );
  const popup = await openPopup(run, fixturePage);
  await popup.click(selector);
  const editor = await (await editorTarget).asPage();
  monitorPage(editor, run.errors);
  await editor.waitForSelector("#state:not(:empty)");
  const captureId = new URL(editor.url()).searchParams.get("id");
  const record = await editor.evaluate(async (id) => {
    const store = await import(chrome.runtime.getURL("src/store.js"));
    return await store.getRecord(id);
  }, captureId);
  await popup.close().catch(() => {});
  return { editor, record };
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

function editorTarget(run) {
  const existingTargets = new Set(run.browser.targets());
  return run.browser.waitForTarget(
    (target) => !existingTargets.has(target) && target.type() === "page" && target.url().includes(run.extension.id) && target.url().includes("/src/editor.html?id="),
    { timeout: 120_000 }
  );
}

function batchTarget(run) {
  const existingTargets = new Set(run.browser.targets());
  return run.browser.waitForTarget(
    (target) => !existingTargets.has(target) && target.type() === "page" && target.url().includes(run.extension.id) && target.url().includes("/src/batch.html?id="),
    { timeout: 180_000 }
  );
}

async function startPicker(run, page, mode) {
  const popup = await openPopup(run, page);
  await popup.click(`[data-picker='${mode}']`);
  await page.waitForSelector("[data-winshot-picker='tip']", { timeout: 10_000 });
}

test("exact MV3 popup captures the full page, exports PNG, and restores state", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    await page.focus("#state");
    await page.evaluate(() => scrollTo(0, 377));
    const before = await page.evaluate(() => ({
      x: scrollX, y: scrollY, active: document.activeElement?.id,
      captureStyles: document.querySelectorAll("[data-winshot-capture]").length
    }));

    const waitingEditor = editorTarget(run);
    const popup = await openPopup(run, page);
    await run.worker.evaluate(async () => {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      await chrome.tabs.setZoom(tab.id, 1.25);
    });
    await popup.click("[data-mode='full-page']");
    const { editor, record } = await recordFromEditorTarget(await waitingEditor);
    await popup.close().catch(() => {});
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.tileCount).toBeGreaterThan(1);
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.seamConfidence.minimum).toBeGreaterThanOrEqual(0.7);
    expect(record.report.restoration).toBe("verified");
    expect(record.report.dimensionsPx.width / record.report.dimensionsCss.width).toBeCloseTo(1.25, 1);

    const after = await page.evaluate(() => ({
      x: scrollX, y: scrollY, active: document.activeElement?.id,
      captureStyles: document.querySelectorAll("[data-winshot-capture]").length
    }));
    expect(after.x).toBeCloseTo(before.x, 1);
    expect(after.y).toBeCloseTo(before.y, 0);
    expect(after.active).toBe(before.active);
    expect(after.captureStyles).toBe(before.captureStyles);

    const downloadRoot = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-download-e2e-"));
    try {
      const session = await editor.createCDPSession();
      await session.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: downloadRoot });
      await editor.click("#save-png");
      await expect.poll(() => fs.readdirSync(downloadRoot).find((name) => name.endsWith(".png")), { timeout: 30_000 }).toBeTruthy();
      const name = fs.readdirSync(downloadRoot).find((value) => value.endsWith(".png"));
      const bytes = fs.readFileSync(path.join(downloadRoot, name));
      expect([...bytes.subarray(0, 8)]).toEqual([137, 80, 78, 71, 13, 10, 26, 10]);
      expect(bytes.readUInt32BE(16)).toBe(record.report.dimensionsPx.width);
      expect(bytes.readUInt32BE(20)).toBe(record.report.dimensionsPx.height);

      await editor.waitForSelector("#save-jpeg:not([disabled])");
      await editor.click("#save-jpeg");
      await expect.poll(() => fs.readdirSync(downloadRoot).find((value) => value.endsWith(".jpg")), { timeout: 30_000 }).toBeTruthy();
      const jpeg = fs.readFileSync(path.join(downloadRoot, fs.readdirSync(downloadRoot).find((value) => value.endsWith(".jpg"))));
      expect([...jpeg.subarray(0, 2)]).toEqual([0xff, 0xd8]);

      await editor.waitForSelector("#save-webp:not([disabled])");
      await editor.click("#save-webp");
      await expect.poll(() => fs.readdirSync(downloadRoot).find((value) => value.endsWith(".webp")), { timeout: 30_000 }).toBeTruthy();
      const webp = fs.readFileSync(path.join(downloadRoot, fs.readdirSync(downloadRoot).find((value) => value.endsWith(".webp"))));
      expect(webp.subarray(0, 4).toString("ascii")).toBe("RIFF");
      expect(webp.subarray(8, 12).toString("ascii")).toBe("WEBP");

      await editor.waitForSelector("#save-pdf:not([disabled])");
      await editor.click("#save-pdf");
      await expect.poll(() => fs.readdirSync(downloadRoot).find((value) => value.endsWith(".pdf")), { timeout: 30_000 }).toBeTruthy();
      const pdf = fs.readFileSync(path.join(downloadRoot, fs.readdirSync(downloadRoot).find((value) => value.endsWith(".pdf"))));
      expect(pdf.subarray(0, 8).toString("ascii")).toBe("%PDF-1.7");
      expect(pdf.subarray(-5).toString("ascii")).toBe("%%EOF");
      const pdfText = pdf.toString("latin1");
      expect(pdfText).toContain("SEARCHABLE WINSHOT DOCUMENT");
      expect(pdfText).toContain("/Subtype /Link");
      expect(pdfText).toContain("https://example.com/winshot-link");
    } finally {
      removeOwnedDirectory(downloadRoot, "winshot-download-e2e-");
    }
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("all-tabs batch captures each web tab and restores every captured page", async () => {
  const run = await launchExactExtension();
  try {
    const first = await run.browser.newPage();
    const second = await run.browser.newPage();
    monitorPage(first, run.errors);
    monitorPage(second, run.errors);
    await first.goto("http://127.0.0.1:4173/frame", { waitUntil: "networkidle0" });
    await second.goto("http://127.0.0.1:4173/nested", { waitUntil: "networkidle0" });
    await first.evaluate(() => scrollTo(0, 333));
    await second.$eval("#outer", (element) => { element.scrollTop = 75; });
    await first.bringToFront();

    const allTabsSummaryTarget = batchTarget(run);
    const popup = await openPopup(run, first);
    await popup.click("#all-tabs");
    const allTabsSummary = await (await allTabsSummaryTarget).asPage();
    await allTabsSummary.waitForSelector(".item.complete");
    const allTabsBatch = await allTabsSummary.evaluate(async () => {
      const id = new URL(location.href).searchParams.get("id");
      return (await chrome.storage.local.get(`batch:${id}`))[`batch:${id}`];
    });
    expect(allTabsBatch.results).toHaveLength(2);
    expect(allTabsBatch.results.every((item) => item.state === "complete")).toBe(true);
    expect(allTabsBatch.results.every((item) => item.captureId)).toBe(true);
    expect(await first.evaluate(() => scrollY)).toBeCloseTo(333, 0);
    expect(await second.$eval("#outer", (element) => element.scrollTop)).toBeCloseTo(75, 0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("URL-list batch closes its disposable fixture tabs", async () => {
  const run = await launchExactExtension();
  try {
    const first = await run.browser.newPage();
    monitorPage(first, run.errors);
    await first.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    await first.bringToFront();
    const urlSummaryTarget = batchTarget(run);
    const urlPopup = await openPopup(run, first);
    await urlPopup.type("#batch-urls", "http://127.0.0.1:4173/frame\nhttp://127.0.0.1:4173/sticky");
    await urlPopup.click("#url-batch");
    const urlSummary = await (await urlSummaryTarget).asPage();
    await urlSummary.waitForSelector(".item.complete");
    const urlBatch = await urlSummary.evaluate(async () => {
      const id = new URL(location.href).searchParams.get("id");
      return (await chrome.storage.local.get(`batch:${id}`))[`batch:${id}`];
    });
    expect(urlBatch.results).toHaveLength(2);
    expect(urlBatch.results.every((item) => item.state === "complete")).toBe(true);
    const remainingFixturePages = (await run.browser.pages()).filter((page) => /^http:\/\/127\.0\.0\.1:4173\/(frame|sticky)$/.test(page.url()));
    expect(remainingFixturePages).toHaveLength(0);
    expect((await run.browser.pages()).some((page) => page.url() === "http://127.0.0.1:4173/body")).toBe(true);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("Ctrl-hover picker captures a nested independently scrolling element", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/nested", { waitUntil: "networkidle0" });
    await page.$eval("#outer", (element) => { element.scrollTop = 450; });
    const original = await page.evaluate(() => ({
      outer: document.querySelector("#outer").scrollTop,
      inner: document.querySelector("#inner").scrollTop
    }));
    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "element");
    const box = await (await page.$("#inner")).boundingBox();
    const pickX = box.x + 2;
    const pickY = box.y + 2;
    await page.keyboard.down("Control");
    await page.mouse.move(pickX, pickY);
    await page.waitForFunction(() => {
      const outline = document.querySelector("[data-winshot-picker='outline']");
      return outline && getComputedStyle(outline).display === "block" && getComputedStyle(outline).borderColor === "rgb(244, 189, 0)";
    });
    await page.mouse.click(pickX, pickY);
    await page.keyboard.up("Control");
    const { record } = await recordFromEditorTarget(await waitingEditor);

    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("self-scroller");
    expect(record.report.dimensionsCss.width).toBe(960);
    expect(record.report.dimensionsCss.height).toBe(1800);
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.seamConfidence.minimum).toBeGreaterThanOrEqual(0.7);
    expect(record.report.restoration).toBe("verified");
    const restored = await page.evaluate(() => ({
      outer: document.querySelector("#outer").scrollTop,
      inner: document.querySelector("#inner").scrollTop,
      targetAttribute: document.querySelector("#inner").getAttribute("data-winshot-target")
    }));
    expect(restored).toEqual({ ...original, targetAttribute: null });
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("drag selection auto-scrolls at the edge and restores the pre-picker page state", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "region");
    await page.mouse.move(100, 180);
    await page.mouse.down();
    await page.mouse.move(600, 985, { steps: 12 });
    await new Promise((resolve) => setTimeout(resolve, 1400));
    expect(await page.evaluate(() => scrollY)).toBeGreaterThan(1000);
    await page.mouse.up();
    const { record } = await recordFromEditorTarget(await waitingEditor);

    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("region");
    expect(record.report.dimensionsCss.width).toBeGreaterThan(490);
    expect(record.report.dimensionsCss.width).toBeLessThan(510);
    expect(record.report.dimensionsCss.height).toBeGreaterThan(1700);
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.restoration).toBe("verified");
    expect(await page.evaluate(() => scrollY)).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("tile-backed capture exceeds 32K without a single giant canvas", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/long", { waitUntil: "networkidle0" });
    const { editor, record } = await captureFromPopup(run, page, "[data-mode='full-page']");
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.dimensionsPx.height).toBeGreaterThan(32_000);
    expect(record.report.tileCount).toBeGreaterThan(30);
    expect(record.report.coverage.complete).toBe(true);
    const downloadRoot = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-download-e2e-"));
    try {
      const session = await editor.createCDPSession();
      await session.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: downloadRoot });
      await editor.click("#save-png");
      await expect.poll(() => fs.readdirSync(downloadRoot).find((name) => name.endsWith(".png")), { timeout: 60_000 }).toBeTruthy();
      const name = fs.readdirSync(downloadRoot).find((value) => value.endsWith(".png"));
      const bytes = fs.readFileSync(path.join(downloadRoot, name));
      expect(bytes.readUInt32BE(20)).toBe(record.report.dimensionsPx.height);
      expect(bytes.readUInt32BE(20)).toBeGreaterThan(32_000);
    } finally {
      removeOwnedDirectory(downloadRoot, "winshot-download-e2e-");
    }
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("popup cancellation discards the result and exactly restores the page", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/long", { waitUntil: "networkidle0" });
    await page.evaluate(() => scrollTo(0, 640));
    const popup = await openPopup(run, page);
    await popup.click("[data-mode='full-page']");
    await page.waitForSelector("[data-winshot-capture]", { timeout: 10_000 });
    await new Promise((resolve) => setTimeout(resolve, 800));
    await popup.click("#cancel");
    await page.waitForFunction(() => !document.querySelector("[data-winshot-capture]") && scrollY === 640, { timeout: 15_000 });
    const records = await popup.evaluate(async () => {
      const store = await import(chrome.runtime.getURL("src/store.js"));
      return await store.listRecords();
    });
    expect(records).toHaveLength(1);
    expect(records[0].state).toBe("cancelled");
    expect(run.browser.targets().some((target) => target.url().includes("/src/editor.html"))).toBe(false);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("navigation aborts capture and reports failure instead of silent corruption", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/long", { waitUntil: "networkidle0" });
    const waitingEditor = editorTarget(run);
    const popup = await openPopup(run, page);
    await popup.click("[data-mode='full-page']");
    await page.waitForSelector("[data-winshot-capture]", { timeout: 10_000 });
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(record.state).toBe("failed");
    expect(record.error).toMatch(/navigated during capture/i);
    expect(record.report.coverage?.complete).not.toBe(true);
    expect(await page.$("[data-winshot-capture]")).toBeNull();
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("service-worker termination triggers page watchdog cleanup and restart recovery", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/long", { waitUntil: "networkidle0" });
    await page.evaluate(() => scrollTo(0, 720));
    const popup = await openPopup(run, page);
    await popup.click("[data-mode='full-page']");
    await page.waitForSelector("[data-winshot-capture]", { timeout: 10_000 });
    await run.worker.close();
    await page.waitForFunction(() => !document.querySelector("[data-winshot-capture]") && scrollY === 720, { timeout: 15_000 });
    await popup.close().catch(() => {});

    const newWorkerTarget = run.browser.waitForTarget(
      (target) => target.type() === "service_worker" && target.url().endsWith("/src/background.js"),
      { timeout: 15_000 }
    );
    const recoveryPage = await run.browser.newPage();
    await recoveryPage.goto(`chrome-extension://${run.extension.id}/src/popup.html`);
    await recoveryPage.click("#cancel");
    const newWorker = await (await newWorkerTarget).worker();
    expect(newWorker).not.toBe(run.worker);
    await new Promise((resolve) => setTimeout(resolve, 500));
    const records = await recoveryPage.evaluate(async () => {
      const store = await import(chrome.runtime.getURL("src/store.js"));
      return await store.listRecords();
    });
    expect(records).toHaveLength(1);
    expect(records[0].state).toBe("failed");
    expect(records[0].error).toMatch(/service worker stopped/i);

    await recoveryPage.close();
    await page.bringToFront();
    const restartedPopup = await openPopup(run, page);
    const waitingEditor = editorTarget(run);
    await restartedPopup.click("[data-mode='visible']");
    const { record: restartedCapture } = await recordFromEditorTarget(await waitingEditor);
    expect(restartedCapture.state).toBe("complete");
    expect(restartedCapture.report.tileCount).toBe(1);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("registered visible-capture command runs through the production command handler", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    const commands = await run.worker.evaluate(async () => await chrome.commands.getAll());
    expect(commands.find((command) => command.name === "capture-visible")?.shortcut).toContain("Alt+Shift+V");
    const popup = await openPopup(run, page);
    await popup.close();
    const waitingEditor = editorTarget(run);
    await run.worker.evaluate(async () => await globalThis.__winshotTest.handleCommand("capture-visible"));
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(record.state).toBe("complete");
    expect(record.report.mode).toBe("visible");
    expect(record.report.tileCount).toBe(1);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("virtualized feed is captured adaptively as nodes are recycled", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/virtual", { waitUntil: "networkidle0" });
    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "element");
    const box = await (await page.$("#virtual")).boundingBox();
    await page.keyboard.down("Control");
    await page.mouse.move(box.x + 2, box.y + 2);
    await page.waitForFunction(() => getComputedStyle(document.querySelector("[data-winshot-picker='outline']")).borderColor === "rgb(244, 189, 0)");
    await page.mouse.click(box.x + 2, box.y + 2);
    await page.keyboard.up("Control");
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("self-scroller");
    expect(record.report.dimensionsCss.height).toBe(15_360);
    expect(record.report.tileCount).toBeGreaterThan(25);
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.seamConfidence.minimum).toBeGreaterThanOrEqual(0.7);
    expect(await page.$eval("#virtual", (element) => element.scrollTop)).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("same-origin and permitted cross-origin frames scroll deeply and restore state", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/iframes", { waitUntil: "networkidle0" });
    const frameUrls = ["http://127.0.0.1:4173/frame", "http://127.0.0.1:4174/frame"];

    for (const frameUrl of frameUrls) {
      const frame = page.frames().find((candidate) => candidate.url() === frameUrl);
      expect(frame).toBeTruthy();
      await frame.evaluate(() => scrollTo(0, 240));
      await page.$$eval("iframe", (frames, url) => {
        frames.find((candidate) => candidate.src === url)?.scrollIntoView({ block: "center" });
      }, frameUrl);
      const selectionTop = await page.evaluate(() => scrollY);
      const waitingEditor = editorTarget(run);
      await startPicker(run, page, "frame");
      const frameHandles = await page.$$("iframe");
      let selectedFrameHandle = null;
      for (const handle of frameHandles) {
        if (await handle.evaluate((element) => element.src) === frameUrl) selectedFrameHandle = handle;
      }
      const box = await selectedFrameHandle.boundingBox();
      await page.mouse.click(box.x + 40, box.y + 40);
      const { record } = await recordFromEditorTarget(await waitingEditor);
      expect(record, record.error).toMatchObject({ state: "complete", frameUrl });
      expect(record.frameId).toBeGreaterThan(0);
      expect(record.report.dimensionsCss.height).toBe(1680);
      expect(record.report.tileCount).toBeGreaterThan(4);
      expect(record.report.coverage.complete).toBe(true);
      expect(record.report.restoration).toBe("verified");
      expect(await frame.evaluate(() => scrollY)).toBeCloseTo(240, 0);
      expect(await page.evaluate(() => scrollY)).toBeCloseTo(selectionTop, 0);
    }

    const nestedHost = page.frames().find((candidate) => candidate.url() === "http://127.0.0.1:4173/nested-frame-host");
    const nestedFrame = page.frames().find((candidate) => candidate.url() === "http://127.0.0.1:4173/frame" && candidate.parentFrame() === nestedHost);
    expect(nestedFrame).toBeTruthy();
    await nestedFrame.evaluate(() => scrollTo(0, 360));
    await page.$eval('iframe[src="/nested-frame-host"]', (frame) => frame.scrollIntoView({ block: "center" }));
    const nestedSelectionTop = await page.evaluate(() => scrollY);
    const nestedEditorTarget = editorTarget(run);
    await startPicker(run, page, "frame");
    const nestedFrameElement = await nestedHost.$("iframe");
    const nestedBox = await nestedFrameElement.boundingBox();
    await page.mouse.click(nestedBox.x + 40, nestedBox.y + 40);
    const { record: nestedRecord } = await recordFromEditorTarget(await nestedEditorTarget);
    expect(nestedRecord, nestedRecord.error).toMatchObject({ state: "complete", frameUrl: "http://127.0.0.1:4173/frame" });
    expect(nestedRecord.report.dimensionsCss.height).toBe(1680);
    expect(nestedRecord.report.coverage.complete).toBe(true);
    expect(await nestedFrame.evaluate(() => scrollY)).toBeCloseTo(360, 0);
    expect(await page.evaluate(() => scrollY)).toBeCloseTo(nestedSelectionTop, 0);

    const nestedCrossHost = page.frames().find((candidate) => candidate.url() === "http://127.0.0.1:4173/nested-cross-frame-host");
    const nestedCrossFrame = page.frames().find((candidate) => candidate.url() === "http://127.0.0.1:4174/frame" && candidate.parentFrame() === nestedCrossHost);
    expect(nestedCrossFrame).toBeTruthy();
    await nestedCrossFrame.evaluate(() => scrollTo(0, 420));
    await page.$eval('iframe[src="/nested-cross-frame-host"]', (frame) => frame.scrollIntoView({ block: "center" }));
    const nestedCrossSelectionTop = await page.evaluate(() => scrollY);
    const nestedCrossEditorTarget = editorTarget(run);
    await startPicker(run, page, "frame");
    const nestedCrossFrameElement = await nestedCrossHost.$("iframe");
    const nestedCrossBox = await nestedCrossFrameElement.boundingBox();
    await page.mouse.click(nestedCrossBox.x + 40, nestedCrossBox.y + 40);
    const { record: nestedCrossRecord } = await recordFromEditorTarget(await nestedCrossEditorTarget);
    expect(nestedCrossRecord, nestedCrossRecord.error).toMatchObject({ state: "complete", frameUrl: "http://127.0.0.1:4174/frame" });
    expect(nestedCrossRecord.report.dimensionsCss.height).toBe(1680);
    expect(nestedCrossRecord.report.coverage.complete).toBe(true);
    expect(await nestedCrossFrame.evaluate(() => scrollY)).toBeCloseTo(420, 0);
    expect(await page.evaluate(() => scrollY)).toBeCloseTo(nestedCrossSelectionTop, 0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("dynamic, horizontal, frame, and pixel-surface fixtures report honest results", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);

    await page.goto("http://127.0.0.1:4173/lazy", { waitUntil: "networkidle0" });
    const lazy = await captureFromPopup(run, page, "[data-mode='full-page']");
    expect(lazy.record, lazy.record.error).toMatchObject({ state: "complete" });
    expect(await page.$$eval(".lazy[data-loaded='true']", (nodes) => nodes.length)).toBe(35);

    await page.goto("http://127.0.0.1:4173/horizontal", { waitUntil: "networkidle0" });
    const horizontal = await captureFromPopup(run, page, "[data-mode='full-page']");
    expect(horizontal.record, horizontal.record.error).toMatchObject({ state: "complete" });
    expect(horizontal.record.report.dimensionsCss.width).toBe(5400);
    expect(horizontal.record.report.coverage.complete).toBe(true);

    await page.goto("http://127.0.0.1:4173/iframes", { waitUntil: "networkidle0" });
    const frames = await captureFromPopup(run, page, "[data-mode='full-page']");
    expect(frames.record.state).toBe("complete");
    expect(frames.record.report.warnings.join(" ")).toMatch(/cross-origin frame/i);

    await page.goto("http://127.0.0.1:4173/media", { waitUntil: "networkidle0" });
    const media = await captureFromPopup(run, page, "[data-mode='full-page']");
    expect(media.record.state).toBe("complete");
    expect(media.record.report.warnings.join(" ")).toMatch(/Canvas\/WebGL/);
    expect(media.record.report.warnings.join(" ")).toMatch(/DRM-protected video/);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});
