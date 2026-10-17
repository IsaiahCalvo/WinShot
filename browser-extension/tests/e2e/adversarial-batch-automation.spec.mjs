import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";

const root = "http://127.0.0.1:4173";
async function run(check) {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-actions-api-"));
  const browser = await launchCaptureBrowser(path.join(temp, "profile"));
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const extension = (await browser.extensions()).get(id);
    const worker = await (await browser.waitForTarget(t => t.type() === "service_worker" && t.url().endsWith("/src/background.js"))).worker();
    const page = await browser.newPage(); await page.goto(`${root}/body`);
    const downloads = path.join(temp, "downloads"); fs.mkdirSync(downloads);
    await (await page.createCDPSession()).send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: downloads });
    const errors = []; page.on("pageerror", error => errors.push(error.message));
    await check({ browser, id, extension, worker, page, downloads, temp });
    expect(errors).toEqual([]);
  } finally { await browser.close(); fs.rmSync(temp, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); }
}
async function popupFor({ browser, extension }, page) {
  await page.bringToFront();
  const before = new Set(browser.targets());
  const waiting = browser.waitForTarget(t => !before.has(t) && t.type() === "page" && t.url().endsWith("/src/popup.html"));
  await extension.triggerAction(page);
  const popup = await (await waiting).asPage(); await popup.waitForSelector("body[data-ready='true']"); return popup;
}
async function grant(context, enabled = true) {
  const popup = await popupFor(context, context.page);
  await popup.$eval("#automation-settings", node => { node.open = true; });
  await popup.$eval("#automation-origins", (node, value) => { node.value = value; }, root);
  await popup.$eval("#automation-enabled", (node, value) => { node.checked = value; }, enabled);
  await popup.click("#automation-save");
  await popup.waitForFunction(async enabled => (await chrome.storage.local.get("winshot:capture-settings"))["winshot:capture-settings"]?.automation.enabled === enabled, {}, enabled);
  await popup.close(); await context.page.bringToFront();
  if (enabled) await context.page.waitForFunction(() => Boolean(window.WinShotCapture));
  else await context.page.waitForFunction(() => !window.WinShotCapture);
}
async function records(page, id) {
  const helper = await page.browser().newPage();
  try { await helper.goto(`chrome-extension://${id}/src/history.html`); return await helper.evaluate(async () => (await import("./store.js")).listRecords()); }
  finally { await helper.close(); await page.bringToFront(); }
}
async function completedDownload(downloads, suffix) {
  await expect.poll(() => fs.readdirSync(downloads).filter(name => name.endsWith(suffix))).not.toEqual([]);
  return path.join(downloads, fs.readdirSync(downloads).find(name => name.endsWith(suffix)));
}

test("remembered capture actions persist per mode and a real PNG auto-download runs once", async () => {
  await run(async context => {
    const { browser, page, id, downloads } = context;
    const popup = await popupFor(context, page);
    await popup.$eval("details", node => { node.open = true; });
    await popup.select("#capture-action-mode", "visible");
    await popup.select("#capture-action-output", "png");
    const waiting = browser.waitForTarget(t => t.type() === "page" && t.url().includes("/src/editor.html?id="));
    await popup.click("[data-mode=visible]");
    const editor = await (await waiting).asPage();
    await editor.waitForSelector(".annotation-overlay");
    const file = await completedDownload(downloads, ".png"), bytes = fs.readFileSync(file);
    expect(bytes.subarray(1, 4).toString()).toBe("PNG");
    expect(bytes.readUInt32BE(16)).toBeGreaterThan(500); expect(bytes.readUInt32BE(20)).toBeGreaterThan(300);
    expect(new URL(editor.url()).searchParams.has("action")).toBe(false);
    const count = fs.readdirSync(downloads).length;
    await editor.reload(); await editor.waitForSelector(".annotation-overlay");
    await editor.waitForFunction(() => [...document.querySelectorAll("#preview img")].every(img => img.complete));
    expect(fs.readdirSync(downloads)).toHaveLength(count);
    const again = await popupFor(context, page);
    await again.$eval("details", node => { node.open = true; });
    await again.select("#capture-action-mode", "visible");
    expect(await again.$eval("#capture-action-output", node => node.value)).toBe("png");
    await again.select("#capture-action-mode", "full-page");
    expect(await again.$eval("#capture-action-output", node => node.value)).toBe("editor");
    expect((await records(page, id))).toHaveLength(1);
  });
});

test("selected-tab batches persist scope and download exactly their completed PNG files in one ZIP", async () => {
  await run(async context => {
    const { browser, page, worker, downloads } = context;
    const second = await browser.newPage(); await second.goto(`${root}/body?second`);
    const third = await browser.newPage(); await third.goto(`${root}/body?not-selected`);
    await page.bringToFront();
    const popup = await popupFor(context, page);
    await popup.select("#batch-scope", "selected-tabs"); await popup.select("#batch-mode", "visible"); await popup.select("#batch-output", "png");
    const sourceWindowId = await popup.evaluate(async () => (await chrome.windows.getCurrent()).id);
    // Activating a CDP target to open the action can collapse Chrome's tab-strip
    // multiselection. Establish it after that activation, then inspect the real
    // browser selection immediately before the ordinary capture-button click.
    const selection = await worker.evaluate(async windowId => {
      const tabs = await chrome.tabs.query({ windowId });
      const selected = tabs.filter(tab => tab.url === "http://127.0.0.1:4173/body" || tab.url?.endsWith("/body?second"));
      await chrome.tabs.highlight({ windowId, tabs: selected.map(tab => tab.index) });
      return { expected: selected.map(tab => tab.id).sort(), actual: (await chrome.tabs.query({ windowId, highlighted: true })).map(tab => tab.id).sort() };
    }, sourceWindowId);
    expect(selection.expected).toHaveLength(2);
    expect(selection.actual).toEqual(selection.expected);
    const waiting = browser.waitForTarget(t => t.type() === "page" && t.url().includes("/src/batch.html?id="));
    await popup.click("#all-tabs");
    const report = await (await waiting).asPage();
    await report.waitForFunction(() => document.querySelector("#summary").textContent.includes("captures completed"));
    expect(await report.$eval("#summary", node => node.textContent)).toContain("2 of 2");
    const zip = fs.readFileSync(await completedDownload(downloads, ".zip"));
    let at = 0; const files = [];
    while (zip.readUInt32LE(at) === 0x04034b50) {
      const size = zip.readUInt32LE(at + 18), length = zip.readUInt16LE(at + 26), start = at + 30 + length;
      const bytes = zip.subarray(start, start + size);
      files.push({ name: zip.subarray(at + 30, start).toString("utf8"), png: bytes.subarray(1, 4).toString(), width: bytes.readUInt32BE(16), bytes: [...bytes] });
      at = start + size;
    }
    expect(files).toHaveLength(2); expect(files.every(file => file.name.endsWith(".png") && file.png === "PNG" && file.width > 500)).toBe(true);
    const decoded = await report.evaluate(async files => Promise.all(files.map(async file => { const bitmap = await createImageBitmap(new Blob([new Uint8Array(file.bytes)], { type: "image/png" })); const size = { width: bitmap.width, height: bitmap.height }; bitmap.close(); return size; })), files);
    expect(decoded.every(size => size.width > 500 && size.height > 300)).toBe(true);
    expect(await report.evaluate(async () => (await import("./settings.js")).loadSettings().then(settings => settings.batchScope))).toBe("selected-tabs");
    const saved = await report.evaluate(async () => (await import("./store.js")).listRecords());
    expect(saved.some(item => item.sourceUrl.includes("not-selected"))).toBe(false);
  });
});

test("website automation requires an explicit origin grant and returns only its own capture status", async () => {
  await run(async context => {
    const { browser, page, worker } = context;
    expect(await page.evaluate(() => typeof window.WinShotCapture)).toBe("undefined");
    await grant(context);
    const waiting = browser.waitForTarget(t => t.type() === "page" && t.url().includes("/src/editor.html?id="));
    const result = await page.evaluate(() => window.WinShotCapture.capture({ mode: "visible" }));
    expect(result.state).toBe("complete"); expect(Object.keys(result).sort()).toEqual(["captureId", "state"]);
    const editor = await (await waiting).asPage(); await editor.waitForSelector(".annotation-overlay");
    expect(new URL(editor.url()).searchParams.get("id")).toBe(result.captureId);
    await editor.close(); await page.bringToFront();
    const iframe = await page.evaluate(() => { const iframe = document.createElement("iframe"); iframe.src = "http://127.0.0.1:4174/body"; document.body.append(iframe); return iframe.src; });
    await expect.poll(() => page.frames().some(frame => frame.url() === iframe)).toBe(true);
    const frames = await worker.evaluate(async () => { const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true }); return { tabId: tab.id, frames: await chrome.webNavigation.getAllFrames({ tabId: tab.id }) }; });
    const child = frames.frames.find(frame => frame.url === iframe); expect(child).toBeTruthy();
    await worker.evaluate(async ({ tabId, frameId }) => {
      await chrome.scripting.executeScript({ target: { tabId, frameIds: [frameId] }, files: ["src/automation-content.js"] });
      await chrome.scripting.executeScript({ target: { tabId, frameIds: [frameId] }, world: "MAIN", files: ["src/automation-page.js"] });
    }, { tabId: frames.tabId, frameId: child.frameId });
    const childFrame = page.frames().find(frame => frame.url() === iframe);
    const rejected = await childFrame.evaluate(() => WinShotCapture.capture({ mode: "visible" }).then(() => "unexpected", error => error.message));
    expect(rejected).toContain("not enabled");
    await grant(context, false);
    expect(await page.evaluate(() => typeof window.WinShotCapture)).toBe("undefined");
  });
});

test("website request cancellation and revocation stop only that page capture and leave the source restored", async () => {
  await run(async context => {
    const { browser, page, worker } = context;
    await grant(context);
    const cancelled = await page.evaluate(async () => {
      const request = WinShotCapture.capture({ mode: "full-page" });
      const done = request.then(result => ({ state: result.state }), error => ({ error: error.message }));
      const cancellation = await request.cancel(); return { cancellation, result: await done };
    });
    expect(cancelled.cancellation.cancelled).toBe(true);
    expect(cancelled.result.state === "cancelled" || /cancel/i.test(cancelled.result.error || "")).toBe(true);
    expect(browser.targets().some(target => target.url().includes("/src/editor.html"))).toBe(false);
    await page.evaluate(() => { document.body.innerHTML = '<main style="height:15000px;background:linear-gradient(#38c,#8b4)">API revocation restoration</main>'; scrollTo(0, 170); });
    await page.evaluate(() => { window.pendingApi = WinShotCapture.capture({ mode: "full-page" }).then(result => result, error => ({ error: error.message })); });
    await page.waitForSelector("style[data-winshot-capture]");
    await grant(context, false);
    const revoked = await page.evaluate(() => window.pendingApi);
    expect(revoked.error || revoked.state).toMatch(/revok|cancel/i);
    await page.waitForFunction(() => document.querySelectorAll("style[data-winshot-capture]").length === 0);
    expect(await page.evaluate(() => scrollY)).toBe(170);
    expect(browser.targets().some(target => target.url().includes("/src/editor.html"))).toBe(false);
  });
});

test("all-window batches capture each window's own pixels and restore the original window", async () => {
  await run(async context => {
    const { browser, page, worker } = context;
    await page.evaluate(() => { document.body.innerHTML = '<div style="position:fixed;inset:0;background:#dc1e1e"></div>'; });
    const origin = await worker.evaluate(async () => (await chrome.tabs.query({ active: true, lastFocusedWindow: true }))[0]);
    const other = await worker.evaluate(async () => chrome.windows.create({ url: "http://127.0.0.1:4173/body?other-window", focused: false }));
    const otherPage = await (await browser.waitForTarget(target => target.type() === "page" && target.url().includes("?other-window"))).asPage();
    await otherPage.waitForSelector("body");
    await otherPage.evaluate(() => { document.body.innerHTML = '<div style="position:fixed;inset:0;background:#1edc1e"></div>'; });
    const focusPreflight = await worker.evaluate(async ids => {
      const entries = [];
      // Exercise focus independently of WinShot's batch implementation first.
      for (const id of ids) {
        let result, error;
        try { result = await chrome.windows.update(id, { focused: true }); } catch (failure) { error = failure.message; }
        entries.push({ requested: id, result, error, lastFocused: await chrome.windows.getLastFocused(), windows: await chrome.windows.getAll() });
      }
      const native = chrome.windows.update;
      globalThis.nativeWindowFocusCalls = [];
      chrome.windows.update = async function(...args) {
        const entry = { windowId: args[0], options: args[1], at: Date.now() };
        globalThis.nativeWindowFocusCalls.push(entry);
        try { const result = await Reflect.apply(native, this, args); entry.result = result; return result; }
        catch (error) { entry.error = error.message; throw error; }
      };
      return entries;
    }, [origin.windowId, other.id, origin.windowId]);
    const preflightPath = test.info().outputPath("native-window-focus-preflight.json");
    fs.mkdirSync(path.dirname(preflightPath), { recursive: true }); fs.writeFileSync(preflightPath, JSON.stringify(focusPreflight, null, 2));
    await test.info().attach("native-window-focus-preflight", { path: preflightPath, contentType: "application/json" });
    const popup = await popupFor(context, page);
    expect(await popup.evaluate(async () => (await chrome.windows.getCurrent()).id)).toBe(origin.windowId);
    await popup.select("#batch-scope", "all-windows"); await popup.select("#batch-mode", "visible");
    const waiting = browser.waitForTarget(target => target.type() === "page" && target.url().includes("/src/batch.html?id="));
    await popup.click("#all-tabs");
    const report = await (await waiting).asPage();
    await report.waitForFunction(() => document.querySelector("#summary").textContent.includes("2 of 2"));
    const pixels = await report.evaluate(async () => {
      const store = await import("./store.js"), results = [];
      for (const record of await store.listRecords()) {
        const tile = (await store.getTiles(record.id))[0], bitmap = await createImageBitmap(tile.blob), canvas = new OffscreenCanvas(1, 1), ctx = canvas.getContext("2d");
        ctx.drawImage(bitmap, 10, 10, 1, 1, 0, 0, 1, 1); bitmap.close();
        results.push({ windowId: record.windowId, pixel: [...ctx.getImageData(0, 0, 1, 1).data] });
      }
      return results;
    });
    expect(pixels.find(item => item.windowId === origin.windowId)?.pixel).toEqual([220, 30, 30, 255]);
    expect(pixels.find(item => item.windowId === other.id)?.pixel).toEqual([30, 220, 30, 255]);
    expect(await report.evaluate(async () => (await chrome.tabs.getCurrent()).windowId)).toBe(origin.windowId);
    const focusEvidence = await worker.evaluate(async () => ({ calls: globalThis.nativeWindowFocusCalls, windows: await chrome.windows.getAll(), lastFocused: await chrome.windows.getLastFocused() }));
    const evidencePath = test.info().outputPath("native-window-focus.json");
    fs.mkdirSync(path.dirname(evidencePath), { recursive: true }); fs.writeFileSync(evidencePath, JSON.stringify({ preflight: focusPreflight, afterBatch: focusEvidence }, null, 2));
    await test.info().attach("native-window-focus", { path: evidencePath, contentType: "application/json" });
    expect(focusEvidence.calls.at(-1)?.windowId).toBe(origin.windowId);
    expect(focusEvidence.calls.at(-1)?.error).toBeUndefined();
    await expect.poll(() => worker.evaluate(async () => (await chrome.windows.getLastFocused()).id)).toBe(origin.windowId);
  });
});

test("manual popup captures and delayed Stop remain bound to the originating window", async () => {
  await run(async context => {
    const { browser, page, worker, id } = context;
    await page.evaluate(() => { document.body.innerHTML = '<div style="position:fixed;inset:0;background:#dc1e1e"></div>'; });
    const source = await worker.evaluate(async () => (await chrome.tabs.query({ active: true, lastFocusedWindow: true }))[0]);
    const other = await worker.evaluate(async () => chrome.windows.create({ url: "http://127.0.0.1:4173/body?manual-other", focused: false }));
    const otherPage = await (await browser.waitForTarget(target => target.type() === "page" && target.url().endsWith("?manual-other"))).asPage();
    await otherPage.waitForSelector("body");
    await otherPage.evaluate(() => { document.body.innerHTML = '<div style="position:fixed;inset:0;background:#1edc1e"></div>'; });
    const popup = await popupFor(context, page);
    expect(await popup.evaluate(async () => (await chrome.windows.getCurrent()).id)).toBe(source.windowId);
    expect(await popup.$eval("body", node => node.dataset.busy)).toBe("false");
    const waiting = browser.waitForTarget(target => target.type() === "page" && target.url().includes("/src/editor.html?id="));
    await popup.click("[data-mode=visible]");
    const editor = await (await waiting).asPage(); await editor.waitForSelector(".annotation-overlay");
    const captured = await editor.evaluate(async () => {
      const store = await import("./store.js"), record = await store.getRecord(new URL(location.href).searchParams.get("id")), tile = (await store.getTiles(record.id))[0];
      const bitmap = await createImageBitmap(tile.blob), canvas = new OffscreenCanvas(1, 1), ctx = canvas.getContext("2d"); ctx.drawImage(bitmap, 10, 10, 1, 1, 0, 0, 1, 1); bitmap.close();
      return { windowId: record.windowId, tabId: record.tabId, pixel: [...ctx.getImageData(0, 0, 1, 1).data] };
    });
    expect(captured).toEqual({ windowId: source.windowId, tabId: source.id, pixel: [220, 30, 30, 255] });
    await editor.close();
    const delayed = await popupFor(context, page);
    await delayed.$eval("details", node => { node.open = true; });
    await delayed.select("#capture-delay", "30000");
    await delayed.click("[data-mode=visible]");
    await expect.poll(() => delayed.evaluate(async windowId => (await chrome.runtime.sendMessage({ type: "WINSHOT_GET_CAPTURE_STATUS", windowId })).busy, source.windowId)).toBe(true);
    const statuses = await delayed.evaluate(async ({ sourceId, otherId }) => ({
      source: await chrome.runtime.sendMessage({ type: "WINSHOT_GET_CAPTURE_STATUS", windowId: sourceId }),
      other: await chrome.runtime.sendMessage({ type: "WINSHOT_GET_CAPTURE_STATUS", windowId: otherId })
    }), { sourceId: source.windowId, otherId: other.id });
    expect(statuses.source.message).toContain("Waiting to capture"); expect(statuses.other.busy).toBe(false);
    await delayed.click("#cancel");
    await delayed.waitForFunction(() => document.body.dataset.busy === "false");
    expect(await delayed.evaluate(async windowId => (await chrome.runtime.sendMessage({ type: "WINSHOT_GET_CAPTURE_STATUS", windowId })).busy, source.windowId)).toBe(false);
    expect(browser.targets().some(target => target.url().includes("/src/editor.html"))).toBe(false);
    expect(await delayed.evaluate(async () => (await (await import("./store.js")).listRecords()).length)).toBe(1);
  });
});
