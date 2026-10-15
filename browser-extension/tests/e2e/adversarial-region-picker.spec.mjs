// Adversarial QA: drag selection / region picker.
// Covers intended-use, break-it, and edge cases NOT exercised by puppeteer-extension.spec.mjs:
//   - picker chrome (dim + crosshair guides + Ctrl-hover highlight) and Esc mid-drag cancel
//   - trailing-click modality leak on tiny-drag cancel and Ctrl+click element pick
//   - starting the picker twice (single overlay, single capture)
//   - starting the picker while a capture is running (honest supersede, picker still works)
//   - horizontal edge auto-scroll producing a wider-than-viewport (multi-monitor-sized) region
//   - drag released with only a sliver of the region visible (tile-plan efficiency)
//   - selection on a pre-scrolled page: document-anchored rect, mid-drag Ctrl square, focus/scroll restore
//   - picker left open/dragging past the 9s state watchdog (scroll must not be yanked mid-drag)
import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "node:fs";
import os from "node:os";

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionPath = process.env.WINSHOT_EXTENSION_PATH || path.resolve(here, "..", "..", "dist", "winshot-capture");

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

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

async function startPicker(run, page, mode) {
  const popup = await openPopup(run, page);
  await popup.click(`[data-picker='${mode}']`);
  await page.waitForSelector("[data-winshot-picker='tip']", { timeout: 10_000 });
}

function editorTargetsOpen(run) {
  return run.browser.targets().filter((target) => target.url().includes("/src/editor.html?id=")).length;
}

test("region picker chrome works and Esc mid-drag stops auto-scroll and restores scroll", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    await page.evaluate(() => scrollTo(0, 500));
    await startPicker(run, page, "region");

    // Dimmed page + crosshair guides while idle.
    await page.mouse.move(500, 400);
    await page.waitForFunction(() => {
      const guides = [...document.querySelectorAll("[data-winshot-picker='guide']")];
      const dim = document.querySelector("[data-winshot-picker='dim']");
      return guides.length === 2 && guides.every((node) => getComputedStyle(node).display === "block") && Boolean(dim);
    });
    expect(await page.evaluate(() => {
      const guides = [...document.querySelectorAll("[data-winshot-picker='guide']")];
      return guides.map((node) => node.style.top || node.style.left);
    })).toContain("400px");

    // Ctrl-hover highlights the element under the cursor and hides the guides.
    await page.keyboard.down("Control");
    await page.mouse.move(500, 600);
    await page.waitForFunction(() => {
      const outline = document.querySelector("[data-winshot-picker='outline']");
      const guides = [...document.querySelectorAll("[data-winshot-picker='guide']")];
      return outline && getComputedStyle(outline).display === "block" && guides.every((node) => getComputedStyle(node).display === "none");
    });
    await page.keyboard.up("Control");

    // Drag into the bottom edge: auto-scroll must engage.
    await page.mouse.move(300, 300);
    await page.mouse.down();
    await page.mouse.move(600, 985, { steps: 4 });
    await sleep(800);
    expect(await page.evaluate(() => scrollY)).toBeGreaterThan(900);

    // Esc mid-drag: overlay gone, scroll restored to the pre-picker position, and stays there
    // (a leaked auto-scroll interval would keep scrolling while the cursor sits at the edge).
    await page.keyboard.press("Escape");
    await page.waitForFunction(() => !document.querySelector("[data-winshot-picker]") && scrollY === 500, { timeout: 10_000 });
    await sleep(500);
    expect(await page.evaluate(() => scrollY)).toBe(500);
    expect(editorTargetsOpen(run)).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("cancel-click and Ctrl+click element pick leak no events to the page, including the trailing click", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    await page.evaluate(() => {
      window.__hits = { pointerdown: 0, mousedown: 0, mouseup: 0, click: 0, contextmenu: 0 };
      for (const type of Object.keys(window.__hits)) {
        document.addEventListener(type, () => { window.__hits[type] += 1; });
      }
    });

    // Right-click while the picker is open must be swallowed.
    await startPicker(run, page, "region");
    await page.mouse.click(400, 600, { button: "right" });

    // A plain click (tiny drag) cancels the picker. The gesture's own trailing `click`
    // event fires AFTER mouseup — if the modal shield is torn down synchronously in the
    // mouseup handler, that click reaches the page (and would follow a link).
    await page.mouse.click(400, 620);
    await page.waitForFunction(() => !document.querySelector("[data-winshot-picker='tip']"), { timeout: 10_000 });
    await sleep(300);
    expect(editorTargetsOpen(run)).toBe(0);

    // Ctrl+click (no drag) captures the hovered element — its trailing click must not
    // hit the page either, or the page mutates at the exact moment capture begins.
    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "region");
    await page.keyboard.down("Control");
    await page.mouse.move(400, 620);
    await page.mouse.click(400, 620);
    await page.keyboard.up("Control");
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("page-element");

    const hits = await page.evaluate(() => window.__hits);
    expect(hits.pointerdown).toBe(0);
    expect(hits.mousedown).toBe(0);
    expect(hits.mouseup).toBe(0);
    expect(hits.contextmenu).toBe(0);
    expect(hits.click).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("starting the picker twice leaves exactly one overlay and one capture per drag", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });

    // First an element picker, then a region picker on top of it — the second start must
    // fully replace the first (no duplicate overlays, no duplicate listeners).
    await startPicker(run, page, "element");
    await startPicker(run, page, "region");
    // The tip selector above is already satisfied by the element picker's tip, so wait
    // for a region-only node before reading overlay counts (the replacement is async).
    await page.waitForSelector("[data-winshot-picker='dim']", { timeout: 10_000 });
    const overlay = await page.evaluate(() => ({
      tips: document.querySelectorAll("[data-winshot-picker='tip']").length,
      outlines: document.querySelectorAll("[data-winshot-picker='outline']").length,
      dims: document.querySelectorAll("[data-winshot-picker='dim']").length,
      guides: document.querySelectorAll("[data-winshot-picker='guide']").length
    }));
    expect(overlay).toEqual({ tips: 1, outlines: 1, dims: 1, guides: 2 });

    // One drag → exactly one capture and one editor. Doubled listeners would send two
    // pick results and start two captures.
    const waitingEditor = editorTarget(run);
    await page.mouse.move(150, 150);
    await page.mouse.down();
    await page.mouse.move(650, 650, { steps: 5 });
    await page.mouse.up();
    const { editor, record } = await recordFromEditorTarget(await waitingEditor);
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("region");
    expect(record.report.dimensionsCss.width).toBeCloseTo(500, 0);
    expect(record.report.dimensionsCss.height).toBeCloseTo(500, 0);
    await sleep(1500);
    expect(editorTargetsOpen(run)).toBe(1);
    const records = await editor.evaluate(async () => {
      const store = await import(chrome.runtime.getURL("src/store.js"));
      return await store.listRecords();
    });
    expect(records).toHaveLength(1);
    expect(await page.evaluate(() => document.querySelectorAll("[data-winshot-picker]").length)).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("starting the region picker during an active capture fails the capture honestly and the picker still works", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/long", { waitUntil: "networkidle0" });

    const doomedEditor = editorTarget(run);
    const popup = await openPopup(run, page);
    await popup.click("[data-mode='full-page']");
    await page.waitForSelector("[data-winshot-capture]", { timeout: 10_000 });
    await sleep(600);
    await popup.close().catch(() => {});
    await page.bringToFront();

    // The capture-region hotkey path has no in-flight guard — this is exactly what
    // pressing the shortcut mid-capture does.
    await run.worker.evaluate(async () => await globalThis.__winshotTest.startPicker("region"));
    await page.waitForSelector("[data-winshot-picker='tip']", { timeout: 10_000 });
    // The superseded capture must release the page immediately (freeze styles removed).
    await page.waitForFunction(() => !document.querySelector("[data-winshot-capture]"), { timeout: 10_000 });

    // The interrupted capture must end as an honest failure, never a silent "complete".
    const { record: doomed } = await recordFromEditorTarget(await doomedEditor);
    expect(doomed.state).toBe("failed");
    expect(doomed.error || "").toMatch(/session|restored|active tab/i);
    expect(doomed.report.coverage?.complete).not.toBe(true);

    // The picker overlay left on the page must still produce a working region capture.
    const waitingEditor = editorTarget(run);
    await page.bringToFront();
    await page.mouse.move(150, 150);
    await page.mouse.down();
    await page.mouse.move(650, 650, { steps: 5 });
    await page.mouse.up();
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("region");
    expect(record.report.dimensionsCss.width).toBeCloseTo(500, 0);
    expect(record.report.coverage.complete).toBe(true);
    expect(await page.evaluate(() => document.querySelectorAll("[data-winshot-capture]").length)).toBe(0);
  } finally {
    await closeExactExtension(run);
  }
});

test("horizontal edge auto-scroll selects a wider-than-viewport region and restores scrollX", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/horizontal", { waitUntil: "networkidle0" });
    expect(await page.evaluate(() => scrollX)).toBe(0);

    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "region");
    await page.mouse.move(200, 150);
    await page.mouse.down();
    await page.mouse.move(1180, 700, { steps: 4 });
    await sleep(1300);
    expect(await page.evaluate(() => scrollX)).toBeGreaterThan(800);
    await page.mouse.move(1000, 700);
    await page.mouse.up();
    const { record } = await recordFromEditorTarget(await waitingEditor);

    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("region");
    // Multi-monitor-sized selection: wider than the 1200px viewport, tiled in columns.
    expect(record.report.dimensionsCss.width).toBeGreaterThan(1300);
    expect(record.report.dimensionsCss.height).toBeCloseTo(550, 0);
    expect(record.report.tileCount).toBeGreaterThanOrEqual(2);
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.seamConfidence.minimum).toBeGreaterThanOrEqual(0.7);
    expect(record.report.restoration).toBe("verified by read-back");
    expect(await page.evaluate(() => scrollX)).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("drag released with only a sliver of the region on screen still tiles efficiently", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });

    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "region");
    await page.mouse.move(150, 150);
    await page.mouse.down();
    await page.mouse.move(600, 985, { steps: 4 });
    await sleep(1500);
    expect(await page.evaluate(() => scrollY)).toBeGreaterThan(1200);
    // Overshoot correction: release near the TOP edge, so only ~90px of the tall
    // selection still intersects the viewport at mouseup time.
    await page.mouse.move(600, 90);
    await page.mouse.up();
    const { record } = await recordFromEditorTarget(await waitingEditor);

    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("region");
    const height = record.report.dimensionsCss.height;
    expect(height).toBeGreaterThan(1200);
    expect(height).toBeLessThan(2800);
    expect(record.report.coverage.complete).toBe(true);
    // The tile plan must be sized by what the viewport CAN show while scrolling the
    // region, not by the sliver that happened to be visible at mouseup. An efficient
    // plan needs ~3 viewport-height tiles here, never dozens of 90px strips.
    expect(record.report.tileCount).toBeLessThanOrEqual(Math.ceil(height / 900) + 2);
    expect(await page.evaluate(() => scrollY)).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("selection on a pre-scrolled page is document-anchored, Ctrl mid-drag locks a square, and focus and scroll are restored", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    await page.focus("#state");
    await page.evaluate(() => scrollTo(0, 800));

    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "region");
    // Start at x=10 so the region overlaps the row label text (rows have 16px padding;
    // the "ROW nnnn" glyphs span roughly x 16-95 and a selection starting at x=100
    // would legitimately exclude them from the captured semantics).
    await page.mouse.move(10, 200);
    await page.mouse.down();
    await page.mouse.move(210, 400, { steps: 2 });
    // Ctrl pressed only mid-drag (not before mousedown) must still lock the square.
    await page.keyboard.down("Control");
    await page.mouse.move(410, 500, { steps: 2 });
    await page.mouse.up();
    await page.keyboard.up("Control");
    const { record } = await recordFromEditorTarget(await waitingEditor);

    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("region");
    // 400x300 drag locked to a 400x400 square by mid-drag Ctrl.
    expect(record.report.dimensionsCss.width).toBeCloseTo(400, 0);
    expect(record.report.dimensionsCss.height).toBeCloseTo(400, 0);
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.restoration).toBe("verified by read-back");

    // The rect is anchored in document coordinates (page scrolled 800px down), so the
    // page-top heading must NOT appear in the captured semantics.
    const capturedText = record.semantics.text.map((entry) => entry.text).join(" ");
    expect(capturedText).not.toContain("SEARCHABLE WINSHOT DOCUMENT");
    expect(capturedText).toContain("ROW");

    expect(await page.evaluate(() => ({ y: scrollY, active: document.activeElement?.id })))
      .toEqual({ y: 800, active: "state" });
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("a picker open longer than nine seconds must not yank the page scroll mid-drag", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    await page.evaluate(() => scrollTo(0, 400));

    await startPicker(run, page, "region");
    await page.mouse.move(300, 300);
    await page.mouse.down();
    await page.mouse.move(600, 985, { steps: 4 });
    await sleep(900);
    await page.mouse.move(600, 500); // park the cursor away from the auto-scroll margins
    const midDragScroll = await page.evaluate(() => scrollY);
    expect(midDragScroll).toBeGreaterThan(700);

    // Hold the drag past the picker's internal state timer. A real user easily takes
    // more than nine seconds — the page must stay exactly where they scrolled it.
    await sleep(9500);
    expect(await page.evaluate(() => scrollY)).toBe(midDragScroll);

    // And Esc after the long pause must still restore the pre-picker scroll position.
    await page.keyboard.press("Escape");
    await page.waitForFunction(() => !document.querySelector("[data-winshot-picker]"), { timeout: 10_000 });
    expect(await page.evaluate(() => scrollY)).toBe(400);
    expect(editorTargetsOpen(run)).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});
