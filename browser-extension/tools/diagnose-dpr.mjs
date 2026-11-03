import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { launchCaptureBrowser } from "../tests/e2e/launch-browser.mjs";

const root = path.resolve("output/dpr-diagnostics");
fs.mkdirSync(root, { recursive: true });
let failed = 0;
for (let attempt = 1; attempt <= Number(process.env.WINSHOT_DIAGNOSTIC_REPEATS || 5); attempt++) {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-dpr-probe-"));
  const browser = await launchCaptureBrowser(profile, { width: 1200, height: 1000, deviceScaleFactor: 2 });
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../dist/winshot-capture", import.meta.url)));
    const extension = (await browser.extensions()).get(id);
    const worker = await (await browser.waitForTarget(t => t.type() === "service_worker")).worker();
    await worker.evaluate(() => {
      const original = chrome.tabs.captureVisibleTab;
      globalThis.pixelTrace = [];
      chrome.tabs.captureVisibleTab = async (...args) => {
        const result = await original.apply(chrome.tabs, args);
        const [tab] = await chrome.tabs.query({ active: true, windowId: args[0] });
        const [{ result: dom }] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: () => {
          const header = document.querySelector("header"), style = getComputedStyle(header), box = header.getBoundingClientRect();
          return { x: scrollX, y: scrollY, dpr: devicePixelRatio, viewport: [innerWidth, innerHeight], header: { position: style.position, display: style.display, visibility: style.visibility, opacity: style.opacity, background: style.backgroundColor, rect: { x: box.x, y: box.y, width: box.width, height: box.height } } };
        } });
        globalThis.pixelTrace.push(dom);
        return result;
      };
    });
    const page = await browser.newPage();
    await page.goto("http://127.0.0.1:4173/sticky", { waitUntil: "networkidle0" });
    await page.evaluate(() => scrollTo(0, 255));
    const pop = browser.waitForTarget(t => t.type() === "page" && t.url().endsWith("/src/popup.html"));
    await page.bringToFront(); await extension.triggerAction(page);
    const ui = await (await pop).asPage(); await ui.waitForSelector("body[data-ready='true']");
    const target = browser.waitForTarget(t => t.type() === "page" && t.url().includes("/src/editor.html?id="));
    await ui.click("[data-mode='full-page']");
    const editor = await (await target).asPage(); await editor.waitForSelector("#state:not(:empty)");
    const result = await editor.evaluate(async () => {
      const store = await import(chrome.runtime.getURL("src/store.js"));
      const id = new URL(location.href).searchParams.get("id"), record = await store.getRecord(id), tiles = await store.getTiles(id);
      const colors = [], blobs = [];
      for (const tile of tiles) {
        const bitmap = await createImageBitmap(tile.blob), ctx = new OffscreenCanvas(bitmap.width, bitmap.height).getContext("2d", { willReadFrequently: true });
        ctx.drawImage(bitmap, 0, 0); const pixels = ctx.getImageData(0, 0, bitmap.width, bitmap.height).data;
        let header = 0, footer = 0;
        for (let y = 0; y < bitmap.height; y += 8) for (let x = 0; x < bitmap.width; x += 8) {
          const i = (y * bitmap.width + x) * 4, r = pixels[i], g = pixels[i+1], b = pixels[i+2];
          if (r > 240 && g > 180 && g < 230 && b < 90) header++;
          if (r < 90 && g > 170 && b > 110 && b < 190) footer++;
        }
        colors.push({ index: tile.index, destY: tile.destYPx, width: bitmap.width, height: bitmap.height, header, footer,
          leftSamples: [0, 10, 50, 100, 150, 300].filter(y => y < bitmap.height).map(y => ({ y, rgb: [...ctx.getImageData(10, y, 1, 1).data] })) });
        bitmap.close(); blobs.push({ index: tile.index, bytes: [...new Uint8Array(await tile.blob.arrayBuffer())] });
      }
      return { state: record.state, report: record.report, colors, blobs };
    });
    const folder = path.join(root, String(attempt)); fs.mkdirSync(folder);
    for (const blob of result.blobs) fs.writeFileSync(path.join(folder, `tile-${blob.index}.png`), Buffer.from(blob.bytes));
    delete result.blobs; result.trace = await worker.evaluate(() => globalThis.pixelTrace);
    result.attempt = attempt; result.version = (await worker.evaluate(() => chrome.runtime.getManifest())).version;
    result.passed = result.state === "complete" && result.colors.filter(x => x.header > 10).length === 1 && result.colors.filter(x => x.footer > 10).length === 1;
    if (!result.passed) failed++;
    fs.writeFileSync(path.join(folder, "result.json"), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result));
  } finally { await browser.close(); fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); }
}
if (failed) process.exitCode = 1;
