// Adversarial QA spec for the scrolling capture engine.
//
// Focus: devicePixelRatio 2, CSS zoom, overflow:hidden body, transform-scaled
// scroll containers, JS-animated content vs seam confidence, growing pages with
// fixed chrome across growth passes, tab-close aborts, and the time/height limits.
// Everything here is intentionally NOT covered by puppeteer-extension.spec.mjs.
//
// Helper functions are replicated from puppeteer-extension.spec.mjs (they are not
// exported there), with one addition: launchExactExtension takes a viewport so a
// test can run the real extension at deviceScaleFactor 2.

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

async function captureFromPopup(run, fixturePage, selector) {
  const waitingEditor = editorTarget(run);
  const popup = await openPopup(run, fixturePage);
  await popup.click(selector);
  const result = await recordFromEditorTarget(await waitingEditor);
  await popup.close().catch(() => {});
  return result;
}

async function startPicker(run, page, mode) {
  const popup = await openPopup(run, page);
  await popup.click(`[data-picker='${mode}']`);
  await page.waitForSelector("[data-winshot-picker='tip']", { timeout: 10_000 });
}

// Reads all capture records through a real extension page (the service worker
// cannot dynamic-import store.js, so the tests go through an extension page
// exactly like the shipped popup/history surfaces do).
async function listRecordsViaExtensionPage(run) {
  const page = await run.browser.newPage();
  await page.goto(`chrome-extension://${run.extension.id}/src/popup.html`);
  const records = await page.evaluate(async () => {
    const store = await import(chrome.runtime.getURL("src/store.js"));
    const all = await store.listRecords();
    return all.map((record) => ({
      id: record.id,
      state: record.state,
      error: record.error || null,
      report: record.report
    }));
  });
  await page.close();
  return records;
}

test("devicePixelRatio 2: sticky full-page capture is complete, 2x-scaled, and shows fixed chrome exactly once", async () => {
  const run = await launchExactExtension({ width: 1200, height: 1000, deviceScaleFactor: 2 });
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/sticky", { waitUntil: "networkidle0" });
    expect(await page.evaluate(() => window.devicePixelRatio)).toBe(2);
    await page.evaluate(() => scrollTo(0, 255));

    const { editor, record } = await captureFromPopup(run, page, "[data-mode='full-page']");
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.tileCount).toBeGreaterThan(1);
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.seamConfidence.minimum).toBeGreaterThanOrEqual(0.7);
    expect(record.report.restoration).toBe("verified by read-back");
    // The pixel dimensions must be the CSS dimensions times the device scale.
    expect(record.report.dimensionsPx.height / record.report.dimensionsCss.height).toBeCloseTo(2, 1);
    expect(record.report.dimensionsPx.width / record.report.dimensionsCss.width).toBeCloseTo(2, 1);

    // Sticky header and fixed footer each appear in exactly one tile even when
    // every destination offset is scaled by 2 before rounding.
    const colors = await editor.evaluate(async () => {
      const id = new URL(location.href).searchParams.get("id");
      const store = await import(chrome.runtime.getURL("src/store.js"));
      const tiles = await store.getTiles(id);
      const results = [];
      for (const tile of tiles) {
        const bitmap = await createImageBitmap(tile.blob);
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const context = canvas.getContext("2d", { willReadFrequently: true });
        context.drawImage(bitmap, 0, 0);
        const pixels = context.getImageData(0, 0, bitmap.width, bitmap.height).data;
        let header = 0;
        let footer = 0;
        for (let y = 0; y < bitmap.height; y += 8) {
          for (let x = 0; x < bitmap.width; x += 8) {
            const offset = (y * bitmap.width + x) * 4;
            const [r, g, b] = [pixels[offset], pixels[offset + 1], pixels[offset + 2]];
            if (r > 240 && g > 180 && g < 230 && b < 90) header++;
            if (r < 90 && g > 170 && b > 110 && b < 190) footer++;
          }
        }
        bitmap.close();
        results.push({ destY: tile.destYPx, header, footer });
      }
      return results;
    });
    expect(colors.filter((tile) => tile.header > 10)).toHaveLength(1);
    expect(colors.filter((tile) => tile.footer > 10)).toHaveLength(1);
    expect(colors.find((tile) => tile.footer > 10).destY).toBe(Math.max(...colors.map((tile) => tile.destY)));

    expect(await page.evaluate(() => scrollY)).toBeCloseTo(255, 0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("CSS-zoomed page (body zoom 1.5) captures the full zoomed extent with verified restoration", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/css-zoom", { waitUntil: "networkidle0" });
    const pageHeight = await page.evaluate(() => document.scrollingElement.scrollHeight);
    expect(pageHeight).toBeGreaterThan(3000); // zoom actually inflated the layout

    const { record } = await captureFromPopup(run, page, "[data-mode='full-page']");
    expect(record, record.error || record.report?.warnings?.join(" | ")).toMatchObject({ state: "complete" });
    expect(record.report.tileCount).toBeGreaterThan(1);
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.seamConfidence.minimum).toBeGreaterThanOrEqual(0.7);
    expect(record.report.restoration).toBe("verified by read-back");
    // The captured extent must match the zoomed document, not the unzoomed layout.
    expect(Math.abs(record.report.dimensionsCss.height - pageHeight)).toBeLessThanOrEqual(9);
    expect(await page.evaluate(() => scrollY)).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("overflow:hidden body never silently claims a complete full-page capture", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/hidden-body", { waitUntil: "networkidle0" });
    // overflow:hidden blocks USER scrolling only — programmatic scrollTo still
    // works (standard CSS behavior), which is exactly what the engine relies on.
    // Probe that, then put the page back at the origin before capturing.
    expect(await page.evaluate(() => { scrollTo(0, 500); const y = scrollY; scrollTo(0, 0); return y; })).toBe(500);

    const { editor, record } = await captureFromPopup(run, page, "[data-mode='full-page']");
    const tiles = await editor.evaluate(async () => {
      const id = new URL(location.href).searchParams.get("id");
      const store = await import(chrome.runtime.getURL("src/store.js"));
      return (await store.getTiles(id)).map((tile) => ({ destYPx: tile.destYPx, heightPx: tile.heightPx }));
    });

    if (record.state === "complete") {
      // If the engine ever learns to capture such a page fully (e.g. by finding the
      // real inner scroller), the claim must be backed by tiles at distinct offsets.
      expect(record.report.coverage.complete).toBe(true);
      expect(new Set(tiles.map((tile) => tile.destYPx)).size).toBeGreaterThan(1);
    } else {
      // Today the honest outcome: a partial capture that admits its coverage gap
      // instead of stitching the same viewport repeatedly and calling it the page.
      expect(record.state).toBe("partial");
      expect(record.report.coverage.complete).toBe(false);
      expect(record.report.warnings.length).toBeGreaterThan(0);
    }
    expect(record.report.restoration).toBe("verified by read-back");
    expect(await page.evaluate(() => scrollY)).toBe(0);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("rAF canvas pixels are snapshotted consistently without stopping or replacing their context", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/js-animated", { waitUntil: "networkidle0" });

    const { editor, record } = await captureFromPopup(run, page, "[data-mode='full-page']");
    expect(record.report.tileCount).toBeGreaterThanOrEqual(3);
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.seamConfidence.minimum).toBe(1);
    expect(record.state, JSON.stringify(record.report)).toBe("complete");
    const pixels = await editor.evaluate(async () => {
      const store = await import(chrome.runtime.getURL('src/store.js'));
      const tile = await store.getTile(new URL(location.href).searchParams.get('id'), 0);
      const bitmap = await createImageBitmap(tile.blob), ctx = new OffscreenCanvas(bitmap.width, bitmap.height).getContext('2d');
      ctx.drawImage(bitmap,0,0);bitmap.close();
      let black=0,white=0;
      for(let y=20;y<800;y+=40) for(let x=20;x<1000;x+=40) {
        const [r,g,b]=ctx.getImageData(x,y,1,1).data;
        if(r<10&&g<10&&b<10) black++;if(r>245&&g>245&&b>245) white++;
      }
      return {black,white};
    });
    expect(pixels.black).toBeGreaterThan(100); expect(pixels.white).toBeGreaterThan(100);
    await page.bringToFront();
    expect(await page.evaluate(()=>({copies:document.querySelectorAll('[data-winshot-surface]').length,visibility:document.querySelector('#anim').style.visibility}))).toEqual({copies:0,visibility:''});
    const initial=await page.$eval('#anim',n=>n.toDataURL());
    await expect.poll(()=>page.$eval('#anim',n=>n.toDataURL())).not.toBe(initial);
    expect(record.report.warnings.join(" ")).toMatch(/Canvas\/WebGL/);
    expect(record.report.restoration).toBe("verified by read-back");
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

for (const variant of ["static", "fixed", "idle"]) test(`a ${variant} WebGL canvas with a non-preserved buffer captures real pixels and resumes`, async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    await page.goto("http://127.0.0.1:4173/body");
    const supported = await page.evaluate(variant => {
      const position = variant === "fixed" ? "fixed" : "static";
      document.body.innerHTML='<canvas id="webgl" width="1000" height="900"></canvas><div style="height:3600px">AFTER WEBGL</div>';
      const canvas=document.querySelector('#webgl');canvas.style.cssText=`display:block;position:${position};top:0;left:0`;
      const gl=canvas.getContext('webgl',{preserveDrawingBuffer:false});if(!gl)return false;
      window.originalCanvas=canvas;window.originalContext=gl;window.paintCount=0;
      function draw(time){window.paintCount++;gl.clearColor(.8+.1*Math.sin(time/120),.04,.02,1);gl.clear(gl.COLOR_BUFFER_BIT);if(variant!=="idle")requestAnimationFrame(draw);}
      requestAnimationFrame(draw);return true;
    }, variant);
    expect(supported, 'WebGL must be available; this regression must not silently skip').toBe(true);
    const { editor, record }=await captureFromPopup(run,page,"[data-mode='full-page']");
    expect(record.state,JSON.stringify(record.report)).toBe('complete');
    expect(record.report.seamConfidence.minimum).toBe(1);
    const pixel=await editor.evaluate(async()=>{
      const s=await import(chrome.runtime.getURL('src/store.js')),t=await s.getTile(new URL(location.href).searchParams.get('id'),0);
      const b=await createImageBitmap(t.blob),c=new OffscreenCanvas(b.width,b.height).getContext('2d');c.drawImage(b,0,0);b.close();return[...c.getImageData(20,20,1,1).data];
    });
    expect(pixel[0]).toBeGreaterThan(170);expect(pixel[1]).toBeLessThan(25);expect(pixel[2]).toBeLessThan(15);expect(pixel[3]).toBe(255);
    await page.bringToFront();
    const restored=await page.evaluate(()=>({same:document.querySelector('#webgl')===window.originalCanvas,lost:window.originalContext.isContextLost(),copies:document.querySelectorAll('[data-winshot-surface]').length,visibility:window.originalCanvas.style.visibility,count:window.paintCount}));
    expect(restored).toMatchObject({same:true,lost:false,copies:0,visibility:''});
    if(variant!=="idle") await expect.poll(()=>page.evaluate(()=>window.paintCount)).toBeGreaterThan(restored.count);
    expect(record.report.restoration).toBe('verified by read-back');
  } finally {await closeExactExtension(run);}
});

test("growing page with a tall fixed footer leaves no stale footer band mid-image", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/growing-footer", { waitUntil: "networkidle0" });

    const { editor, record } = await captureFromPopup(run, page, "[data-mode='full-page']");
    expect(record.report.dimensionsCss.height).toBeGreaterThan(2000); // growth pass actually ran

    // Composite the tiles exactly like the exporters do (ascending index paint
    // order), then locate every magenta footer band in the final image. The fixed
    // footer must appear exactly once, at the true bottom of the page. A second
    // band is a stale footer from an earlier growth pass that later tiles failed
    // to overpaint (footer taller than the 64px tile overlap).
    const analysis = await editor.evaluate(async () => {
      const id = new URL(location.href).searchParams.get("id");
      const store = await import(chrome.runtime.getURL("src/store.js"));
      const tiles = await store.getTiles(id);
      const width = Math.max(...tiles.map((tile) => tile.destXPx + tile.widthPx));
      const height = Math.max(...tiles.map((tile) => tile.destYPx + tile.heightPx));
      const canvas = new OffscreenCanvas(width, height);
      const context = canvas.getContext("2d", { willReadFrequently: true });
      context.fillStyle = "#ffffff";
      context.fillRect(0, 0, width, height);
      for (const tile of tiles.sort((a, b) => a.index - b.index)) {
        const bitmap = await createImageBitmap(tile.blob);
        context.drawImage(bitmap, tile.destXPx, tile.destYPx, tile.widthPx, tile.heightPx);
        bitmap.close();
      }
      const bands = [];
      for (let y = 0; y < height; y += 2) {
        const row = context.getImageData(0, y, width, 1).data;
        let magenta = 0;
        let sampled = 0;
        for (let x = 0; x < width; x += 8) {
          const offset = x * 4;
          sampled++;
          if (row[offset] > 240 && row[offset + 1] < 60 && row[offset + 2] > 150) magenta++;
        }
        if (magenta > sampled * 0.5) {
          const last = bands[bands.length - 1];
          if (last && y - last.end <= 12) last.end = y;
          else bands.push({ start: y, end: y });
        }
      }
      return { bands, width, height };
    });

    expect(analysis.height).toBeGreaterThan(2800);
    expect(analysis.bands.length, `footer bands found at ${JSON.stringify(analysis.bands)} in a ${analysis.height}px image`).toBe(1);
    expect(analysis.bands[0].start).toBeGreaterThan(analysis.height - 130);
    expect(await page.evaluate(() => scrollY)).toBe(0);
  } finally {
    await closeExactExtension(run);
  }
});

test("transform-scaled scroll container captures its complete rendered extent", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/scaled-scroller", { waitUntil: "networkidle0" });

    const waitingEditor = editorTarget(run);
    await startPicker(run, page, "element");
    const box = await (await page.$("#scaled")).boundingBox();
    const pickX = box.x + box.width / 2;
    const pickY = box.y + box.height / 2;
    await page.mouse.move(pickX, pickY);
    await page.mouse.click(pickX, pickY);
    const { record } = await recordFromEditorTarget(await waitingEditor);

    expect(record.report.target).toBe("self-scroller");
    expect(record.state, JSON.stringify(record.report)).toBe("complete");
    expect(record.report.coverage.complete).toBe(true);
    expect(record.report.seamConfidence.minimum).toBeGreaterThanOrEqual(0.7);
    // 25 rows x 120 layout pixels, rendered at scale(.5). Keep image placement,
    // crop rectangles and scroll offsets in the same visual coordinate space.
    expect(record.report.dimensionsCss.height).toBe(1500);
    expect(record.report.dimensionsPx.height).toBe(1500);
    // Whatever the outcome, the page must be restored exactly.
    expect(await page.$eval("#scaled", (element) => element.scrollTop)).toBe(0);
    expect(await page.$eval("#scaled", (element) => element.getAttribute("data-winshot-target"))).toBeNull();
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("closing the tab mid-capture fails honestly without a phantom complete record", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/long", { waitUntil: "networkidle0" });
    await page.bringToFront();

    // Drive the production entry point directly so no editor tab is expected.
    const resultPromise = run.worker.evaluate(() =>
      globalThis.__winshotTest.runCapture("full-page", null, null, { openEditor: false }));
    await page.waitForSelector("[data-winshot-capture]", { timeout: 10_000 });
    await page.close();

    const result = await resultPromise;
    expect(result.state).toBe("failed");
    expect(result.error).toMatch(/tab closed during capture/i);

    const records = await listRecordsViaExtensionPage(run);
    const record = records.find((candidate) => candidate.id === result.captureId);
    expect(record).toBeTruthy();
    expect(record.state).toBe("failed");
    expect(record.report.coverage?.complete).not.toBe(true);
    // The page is gone, so restoration cannot be "verified" — it must say so.
    expect(record.report.restoration).not.toBe("verified by read-back");
    expect(run.browser.targets().some((target) => target.url().includes("/src/editor.html"))).toBe(false);
  } finally {
    await closeExactExtension(run);
  }
});

test("time and height limits stop the capture honestly and still restore the page", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/long", { waitUntil: "networkidle0" });
    await page.bringToFront();

    // Time limit elapsing before the first tile: must not be reported complete.
    const timedOut = await run.worker.evaluate(() =>
      globalThis.__winshotTest.runCapture("full-page", null, null, { openEditor: false, limits: { maxDurationMs: 0 } }));
    expect(timedOut.state).not.toBe("complete");
    await page.waitForFunction(() => !document.querySelector("[data-winshot-capture]"), { timeout: 15_000 });

    // Height limit: the extent must clamp, the state must be partial, and the
    // warning must say which limit fired.
    const clamped = await run.worker.evaluate(() =>
      globalThis.__winshotTest.runCapture("full-page", null, null, { openEditor: false, limits: { maxHeightCss: 1500 } }));
    expect(clamped.state).toBe("partial");
    await page.waitForFunction(() => !document.querySelector("[data-winshot-capture]") && scrollY === 0, { timeout: 15_000 });

    const records = await listRecordsViaExtensionPage(run);
    const timedRecord = records.find((candidate) => candidate.id === timedOut.captureId);
    expect(timedRecord.state).toBe("partial");
    expect(timedRecord.report.tileCount).toBe(0);
    expect(timedRecord.report.coverage.complete).toBe(false);
    expect(timedRecord.report.warnings.join(" ")).toMatch(/time limit/i);
    expect(timedRecord.report.restoration).toBe("verified by read-back");

    const clampedRecord = records.find((candidate) => candidate.id === clamped.captureId);
    expect(clampedRecord.state).toBe("partial");
    expect(clampedRecord.report.dimensionsCss.height).toBeLessThanOrEqual(1500);
    expect(clampedRecord.report.coverage.complete).toBe(true);
    expect(clampedRecord.report.warnings.join(" ")).toMatch(/safety limit|dimension limit/i);
    expect(clampedRecord.report.restoration).toBe("verified by read-back");
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});
