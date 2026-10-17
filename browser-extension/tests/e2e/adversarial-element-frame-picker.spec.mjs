// Adversarial QA: element picker and frame picker.
//
// Covers cases the main suite (puppeteer-extension.spec.mjs) does not:
//   1. Ctrl-exact-node pick of an element INSIDE an open shadow root
//   2. Element pick of a position:fixed element (pixel-verified)
//   3. Picker-state watchdog firing while the picker is still open (>9s hover)
//   4. Frame pick of an iframe hosted inside a shadow root
//   5. Frame-mode tip counts + non-frame click guidance + scrollable-div fallback
//   6. Frame pick of a same-origin srcdoc frame
//   7. An element that vanishes the instant it is selected (hostile page)
//
// Launch/helper pattern replicated from puppeteer-extension.spec.mjs (helpers
// there are not exported). Fixtures served by tests/fixture-server.mjs on 4173.

import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "node:fs";
import os from "node:os";

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionPath = process.env.WINSHOT_EXTENSION_PATH || path.resolve(here, "..", "..", "dist", "winshot-capture");

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

async function launchExactExtension(viewport = { width: 1200, height: 1000 }) {
  const profileRoot = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-adversarial-e2e-"));
  const errors = [];
  const browser = await launchCaptureBrowser(profileRoot, viewport);
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
  await popup.waitForSelector("body[data-ready='true']");
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
  await popup.close().catch(() => {});
}

for (const mode of ["region", "element"]) test(`a stalled embedded frame cannot block ${mode} selection or revive its closed picker`, async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    await page.goto("http://127.0.0.1:4173/iframes", { waitUntil: "networkidle0" });
    // Fault-inject a slow child-script response, while retaining the real MV3
    // popup, message channels, selection, screenshot API, and saved image.
    await run.worker.evaluate(() => {
      const execute = chrome.scripting.executeScript.bind(chrome.scripting);
      chrome.scripting.executeScript = async details => {
        if (details.target.allFrames || details.target.frameIds?.some(id => id !== 0)) await new Promise(resolve => setTimeout(resolve, 6000));
        return execute(details);
      };
    });
    const ui = await openPopup(run, page);
    const start = performance.now();
    await ui.click(`[data-picker='${mode}']`);
    await page.waitForSelector("[data-winshot-picker='tip']", { timeout: 2500 });
    expect(performance.now() - start).toBeLessThan(2500);
    const waiting = editorTarget(run);
    if (mode === "region") {
      // The drag ends inside an iframe: its mouseup must reach the top picker.
      await page.mouse.move(30, 70); await page.mouse.down();
      await page.mouse.move(300, 170, { steps: 5 }); await page.mouse.up();
    } else {
      await page.keyboard.down("Control"); await page.mouse.click(150, 40); await page.keyboard.up("Control");
    }
    const { record } = await recordFromEditorTarget(await waiting);
    expect(record.state).toBe("complete");
    if (mode === "region") expect(record.report.dimensionsPx).toEqual({ width: 270, height: 100 });
    else expect(record.report.target).toBe("page-element");
    // Wait beyond the injected delay: late completion must never add overlays.
    await new Promise(resolve => setTimeout(resolve, Math.max(0, 6500 - (performance.now() - start))));
    for (const frame of page.frames()) expect(await frame.$$eval("[data-winshot-picker]", nodes => nodes.length)).toBe(0);
    expect(run.errors).toEqual([]);
  } finally { await closeExactExtension(run); }
});

for (const dpr of [1, 2]) test(`a fixed sidebar survives frame-tile overlaps intact at DPR ${dpr}`, async () => {
  const run = await launchExactExtension({ width: 1200, height: 1000, deviceScaleFactor: dpr });
  try {
    const page = await run.browser.newPage();
    await page.goto("http://127.0.0.1:4173/iframes", { waitUntil: "networkidle0" });
    await page.$eval("iframe", node => { node.style.cssText = "display:block;margin:100px;width:700px;height:350px;border:0;box-sizing:content-box"; node.scrollIntoView({ block: "center" }); });
    const frame = page.frames().find(frame => frame.url() === "http://127.0.0.1:4173/frame" && frame.parentFrame() === page.mainFrame());
    await frame.evaluate(() => {
      document.body.innerHTML = '<style>body{margin:0;height:2800px;background:#1ab45c}aside{position:fixed;left:0;top:0;width:400px;height:340px;background:#ff00cc}</style><aside></aside>';
    });
    const before = await page.evaluate(() => scrollY);
    const waiting = editorTarget(run);
    await startPicker(run, page, "frame");
    const box = await (await page.$("iframe")).boundingBox();
    await page.mouse.click(box.x + 500, box.y + 150);
    const { editor, record } = await recordFromEditorTarget(await waiting);
    expect(record.state, JSON.stringify(record.report)).toBe("complete");
    const bands = await editor.evaluate(async dpr => {
      const tiles = await (await import(chrome.runtime.getURL("src/store.js"))).getTiles(new URL(location.href).searchParams.get("id"));
      const context = new OffscreenCanvas(700 * dpr, 2800 * dpr).getContext("2d");
      for (const tile of tiles.sort((a, b) => a.index - b.index)) {
        const image = await createImageBitmap(tile.blob);
        context.drawImage(image, tile.destXPx, tile.destYPx, tile.widthPx, tile.heightPx); image.close();
      }
      const column = context.getImageData(50 * dpr, 0, 1, 2800 * dpr).data;
      const bands = [];
      for (let y = 0; y < 2800 * dpr; y++) if (column[y * 4] === 255 && column[y * 4 + 1] === 0 && column[y * 4 + 2] === 204) {
        const last = bands.at(-1);
        if (last && last.end === y - 1) last.end = y; else bands.push({ start: y, end: y });
      }
      return bands;
    }, dpr);
    expect(bands).toEqual([{ start: 0, end: 340 * dpr - 1 }]);
    expect(record.report.seamConfidence.minimum).toBeGreaterThan(0.99);
    expect(await page.evaluate(() => scrollY)).toBe(before);
    expect(await frame.evaluate(() => scrollY)).toBe(0);
  } finally { await closeExactExtension(run); }
});

test("element picker Ctrl-click captures an element inside an open shadow root", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/shadow", { waitUntil: "networkidle0" });
    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "element");
    const point = await page.evaluate(() => {
      const item = document.querySelector("fixture-list").shadowRoot.querySelectorAll(".item")[3];
      const rect = item.getBoundingClientRect();
      return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
    });
    await page.keyboard.down("Control");
    await page.mouse.move(point.x, point.y);
    await page.mouse.click(point.x, point.y);
    await page.keyboard.up("Control");
    const { record } = await recordFromEditorTarget(await waitingEditor);
    // Today this fails: the picker marks the shadow element via composedPath, but
    // classifyTarget re-finds it with document.querySelector, which never pierces
    // shadow roots -> "The selected element is no longer on the page."
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("page-element");
    // .item is content-box inside the shadow root (the document's *{box-sizing:
    // border-box} reset does not pierce shadow roots): 130 + 2*20 padding + 1 border.
    expect(record.report.dimensionsCss.height).toBeCloseTo(171, 0);
    expect(record.report.coverage.complete).toBe(true);
    // The temporary marker attribute must not be left behind on the shadow node.
    expect(await page.evaluate(() =>
      document.querySelector("fixture-list").shadowRoot.querySelectorAll("[data-winshot-target]").length)).toBe(0);
  } finally {
    await closeExactExtension(run);
  }
});

test("element picker on a position:fixed element captures the element's own pixels", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/fixed-badge", { waitUntil: "networkidle0" });
    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "element");
    // Badge occupies viewport (40..260, 300..364) and is position:fixed magenta.
    await page.mouse.move(150, 330);
    await page.mouse.click(150, 330);
    const { editor, record } = await recordFromEditorTarget(await waitingEditor);
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.dimensionsCss.width).toBeCloseTo(220, 0);
    expect(record.report.dimensionsCss.height).toBeCloseTo(64, 0);
    // The captured pixels must be the badge itself, not the page content that sits
    // at the badge's document coordinates after the capture engine scrolls the page.
    // (A fixed element does not move with window.scrollTo; converting its rect to
    // document coordinates and scrolling there crops the content underneath.)
    const magentaRatio = await editor.evaluate(async () => {
      const id = new URL(location.href).searchParams.get("id");
      const store = await import(chrome.runtime.getURL("src/store.js"));
      const tiles = await store.getTiles(id);
      let magenta = 0;
      let total = 0;
      for (const tile of tiles) {
        const bitmap = await createImageBitmap(tile.blob);
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const context = canvas.getContext("2d", { willReadFrequently: true });
        context.drawImage(bitmap, 0, 0);
        const pixels = context.getImageData(0, 0, bitmap.width, bitmap.height).data;
        for (let offset = 0; offset < pixels.length; offset += 16) {
          const [r, g, b] = [pixels[offset], pixels[offset + 1], pixels[offset + 2]];
          if (r > 200 && g < 70 && b > 150) magenta++;
          total++;
        }
        bitmap.close();
      }
      return total ? magenta / total : 0;
    });
    expect(magentaRatio).toBeGreaterThan(0.5);
  } finally {
    await closeExactExtension(run);
  }
});

test("element picker keeps the user's scroll position while open longer than the state watchdog", async () => {
  test.setTimeout(150_000);
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    await startPicker(run, page, "element");
    // Wheel scrolling is normal while hunting for an element — it is not swallowed.
    await page.mouse.move(600, 500);
    await page.mouse.wheel({ deltaY: 600 });
    await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(400);
    const scrolled = await page.evaluate(() => scrollY);
    // Users routinely take longer than 9 seconds to decide. The picker-state
    // watchdog (WATCHDOG_MS) must not yank the viewport back while the picker
    // overlay is still open and interactive.
    await new Promise((resolve) => setTimeout(resolve, 10_500));
    expect(await page.evaluate(() => Boolean(document.querySelector("[data-winshot-picker='tip']")))).toBe(true);
    expect(await page.evaluate(() => scrollY)).toBeCloseTo(scrolled, 0);
    // The picker must still complete a capture after the long hover.
    const waitingEditor = editorTarget(run);
    await page.mouse.click(600, 300);
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(record, record.error).toMatchObject({ state: "complete" });
  } finally {
    await closeExactExtension(run);
  }
});

test("frame picker captures an iframe hosted inside a shadow root", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/shadow-iframe", { waitUntil: "networkidle0" });
    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "frame");
    const point = await page.evaluate(() => {
      const rect = document.querySelector("frame-holder").shadowRoot.querySelector("iframe").getBoundingClientRect();
      return { x: rect.x + 40, y: rect.y + 40 };
    });
    await page.mouse.click(point.x, point.y);
    const { record } = await recordFromEditorTarget(await waitingEditor);
    // Today this fails: locateFrameHost matches the announced child window with
    // document.querySelectorAll("iframe,frame"), which cannot see a shadow-hosted
    // iframe -> "could not be matched to its exact browser frame identity."
    expect(record, record.error).toMatchObject({ state: "complete", frameUrl: "http://127.0.0.1:4173/frame" });
    expect(record.report.dimensionsCss.height).toBe(1680);
    expect(record.report.tileCount).toBeGreaterThan(1);
    expect(record.report.coverage.complete).toBe(true);
  } finally {
    await closeExactExtension(run);
  }
});

test("frame-mode tip counts blocked frames, guides non-frame clicks, and captures a scrollable div", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/mixed-frames", { waitUntil: "networkidle0" });
    await startPicker(run, page, "frame");
    const tip = () => page.$eval("[data-winshot-picker='tip']", (node) => node.textContent);
    // In current Chrome, scripting.executeScript({allFrames:true}) injects into the
    // data: iframe too (verified empirically: getAllFrames and executeScript both
    // report 3 frames), so zero frames are blocked and the tip must NOT warn.
    expect(await tip()).toBe("Click inside the frame you want to capture · Esc cancels");

    // Clicking plain non-scrollable page content must give guidance and keep the picker open.
    const plain = await (await page.$("#plain")).boundingBox();
    await page.mouse.click(plain.x + plain.width / 2, plain.y + plain.height / 2);
    await expect.poll(tip).toMatch(/isn't a frame or scrolling area/i);
    expect(await page.evaluate(() => Boolean(document.querySelector("[data-winshot-picker='tip']")))).toBe(true);

    // Clicking a scrollable div in frame mode captures it fully, like the element picker.
    const waitingEditor = editorTarget(run);
    const panel = await (await page.$("#panel")).boundingBox();
    await page.mouse.click(panel.x + panel.width / 2, panel.y + panel.height / 2);
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.target).toBe("self-scroller");
    expect(record.report.dimensionsCss.height).toBe(1200);
    expect(record.report.coverage.complete).toBe(true);
    // Picker chrome must be gone after the capture started.
    expect(await page.evaluate(() => document.querySelectorAll("[data-winshot-picker]").length)).toBe(0);
  } finally {
    await closeExactExtension(run);
  }
});

test("same-origin srcdoc frame is fully captured through the frame picker", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/srcdoc-frame", { waitUntil: "networkidle0" });
    const before = await page.evaluate(() => scrollY);
    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "frame");
    const box = await (await page.$("#sd")).boundingBox();
    await page.mouse.click(box.x + 40, box.y + 40);
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(record, record.error).toMatchObject({ state: "complete", frameUrl: "about:srcdoc" });
    // 10 rows x 120px, margin 0 — the full srcdoc document, not just its 300px viewport.
    expect(record.report.dimensionsCss.height).toBe(1200);
    expect(record.report.tileCount).toBeGreaterThan(3);
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.restoration).toBe("verified by read-back");
    expect(await page.evaluate(() => scrollY)).toBeCloseTo(before, 0);
  } finally {
    await closeExactExtension(run);
  }
});

test("a frame that finishes navigating after picker startup becomes selectable", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    await page.goto("http://127.0.0.1:4173/srcdoc-frame", { waitUntil: "networkidle0" });
    await startPicker(run, page, "frame");
    await page.evaluate(() => {
      const frame = document.createElement("iframe");
      frame.id = "late-frame";
      frame.style.cssText = "display:block;width:700px;height:320px;border:0";
      frame.src = "/frame";
      document.body.prepend(frame);
    });
    const frame = await (await page.waitForSelector("#late-frame")).contentFrame();
    await frame.waitForSelector("[data-winshot-picker='tip']", { timeout: 10_000 });
    const waiting = editorTarget(run);
    const box = await (await page.$("#late-frame")).boundingBox();
    await page.mouse.click(box.x + 100, box.y + 100);
    const { record } = await recordFromEditorTarget(await waiting);
    expect(record.state, record.error).toBe("complete");
    expect(record.frameUrl).toBe("http://127.0.0.1:4173/frame");
    expect(record.report.tileCount).toBeGreaterThan(1);
    expect(record.report.restoration).toBe("verified by read-back");
  } finally { await closeExactExtension(run); }
});

test("a frame picker stays usable when its only embedded document is still loading", async () => {
  const run=await launchExactExtension();
  try {
    const page=await run.browser.newPage();await page.goto('http://127.0.0.1:4173/srcdoc-frame',{waitUntil:'networkidle0'});
    await run.worker.evaluate(()=>{
      const execute=chrome.scripting.executeScript.bind(chrome.scripting);
      globalThis.__holdChildInjection=true;
      chrome.scripting.executeScript=(details)=>details.target.frameIds?.some(id=>id!==0)&&globalThis.__holdChildInjection?new Promise(()=>{}):execute(details);
    });
    await startPicker(run,page,'frame');
    expect(await page.$eval("[data-winshot-picker='tip']",n=>n.textContent)).toContain('still loading or restricted');
    await run.worker.evaluate(()=>{globalThis.__holdChildInjection=false;});
    await page.$eval('iframe',n=>{n.removeAttribute('srcdoc');n.src='/frame';});
    const frame=await(await page.$('iframe')).contentFrame();
    await frame.waitForSelector("[data-winshot-picker='tip']",{timeout:10000});
    const waiting=editorTarget(run),box=await(await page.$('iframe')).boundingBox();
    await page.mouse.click(box.x+100,box.y+100);
    const {record}=await recordFromEditorTarget(await waiting);
    expect(record.state,JSON.stringify(record.report)).toBe('complete');
    expect(record.frameUrl).toBe('http://127.0.0.1:4173/frame');
    expect(record.report.restoration).toBe('verified by read-back');
  } finally {await closeExactExtension(run);}
});

test("an element removed the instant it is selected fails gracefully and cleans up", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/vanishing", { waitUntil: "networkidle0" });
    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "element");
    const box = await (await page.$("#doomed")).boundingBox();
    await page.mouse.move(box.x + 30, box.y + 30);
    await page.mouse.click(box.x + 30, box.y + 30);
    // The page's MutationObserver removes #doomed as soon as the picker marks it.
    const { record } = await recordFromEditorTarget(await waitingEditor);
    expect(record.state).toBe("failed");
    expect(record.error).toMatch(/no longer on the page/i);
    // No picker chrome, no freeze styles, no marker attribute may remain.
    expect(await page.evaluate(() => document.querySelectorAll("[data-winshot-picker]").length)).toBe(0);
    expect(await page.evaluate(() => document.querySelectorAll("[data-winshot-capture]").length)).toBe(0);
    expect(await page.evaluate(() => document.querySelectorAll("[data-winshot-target]").length)).toBe(0);
    // A follow-up capture on the same tab must still work (no stuck lock or state).
    const secondEditor = editorTarget(run);
    await startPicker(run, page, "element");
    const row = await (await page.$("[data-row='2']")).boundingBox();
    await page.mouse.click(row.x + 20, row.y + 20);
    const { record: second } = await recordFromEditorTarget(await secondEditor);
    expect(second, second.error).toMatchObject({ state: "complete" });
  } finally {
    await closeExactExtension(run);
  }
});
