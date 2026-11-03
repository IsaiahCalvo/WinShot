import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

test("large wrapper mutation bursts preserve descendants without repeated subtree scans", async () => {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-mutation-burst-"));
  const browser = await launchCaptureBrowser(profile);
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const extension = (await browser.extensions()).get(id);
    const workerTarget = await browser.waitForTarget(t => t.type() === "service_worker" && t.url().endsWith("/src/background.js"));
    const worker = await workerTarget.worker();
    const page = await browser.newPage();
    await page.goto("http://127.0.0.1:4173/body");
    await page.evaluate(() => {
      document.body.innerHTML = '<style>body{margin:0;background:white}section{height:800px;box-sizing:border-box;padding:180px 100px}.marker{width:320px;height:160px;background:rgb(26,180,92)}.hidden{opacity:0}footer{position:fixed;bottom:0;left:0;width:100%;height:48px;background:rgb(0,200,223)}</style><main id="burst-wrapper">' +
        Array.from({ length: 6 }, (_, index) => `<section><div class="marker">Section ${index}</div><div class="hidden"><a href="https://example.com/hidden-${index}">Hidden ${index}</a></div>${'<span></span>'.repeat(500)}</section>`).join("") + '</main>';
      window.captureBurstCount = 0;
      addEventListener("scroll", () => {
        if (!scrollY) return;
        window.captureBurstCount++;
        const wrapper = document.querySelector("#burst-wrapper");
        for (let index = 0; index < 150; index++) wrapper.style.setProperty("--capture-burst", String(window.captureBurstCount * 150 + index));
        // The same delivery contains both a changed ancestor and a newly added
        // descendant. The late floating child must still be discovered.
        if (!document.querySelector("footer")) wrapper.append(document.createElement("footer"));
      });
    });
    const [{ id: tabId }] = await worker.evaluate(async url => chrome.tabs.query({ url }), page.url());
    await worker.evaluate(async tabId => chrome.scripting.executeScript({
      target: { tabId }, world: "ISOLATED",
      func: () => {
        const query = Element.prototype.querySelectorAll;
        globalThis.winshotBurstQueries = 0;
        Element.prototype.querySelectorAll = function (...args) {
          if (this.id === "burst-wrapper" && args[0] === "*") globalThis.winshotBurstQueries++;
          return Reflect.apply(query, this, args);
        };
      }
    }), tabId);
    await page.bringToFront();
    const popTarget = browser.waitForTarget(t => t.type() === "page" && t.url().endsWith("/src/popup.html"));
    await extension.triggerAction(page);
    const popup = await (await popTarget).asPage();
    await popup.waitForSelector("body[data-ready='true']");
    const editorTarget = browser.waitForTarget(t => t.type() === "page" && t.url().includes("/src/editor.html?id="), { timeout: 90_000 });
    await popup.click("[data-mode='full-page']");
    const editor = await (await editorTarget).asPage();
    await editor.waitForSelector("#state:not(:empty)");
    const result = await editor.evaluate(async () => {
      const store = await import(chrome.runtime.getURL("src/store.js"));
      const id = new URL(location.href).searchParams.get("id");
      const record = await store.getRecord(id);
      const tiles = await store.getTiles(id);
      const context = new OffscreenCanvas(record.report.dimensionsPx.width, record.report.dimensionsPx.height).getContext("2d", { willReadFrequently: true });
      for (const tile of tiles.sort((a, b) => a.index - b.index)) {
        const bitmap = await createImageBitmap(tile.blob);
        context.drawImage(bitmap, tile.destXPx, tile.destYPx, tile.widthPx, tile.heightPx);
        bitmap.close();
      }
      return {
        record,
        markers: Array.from({ length: 6 }, (_, index) => [...context.getImageData(200, index * 800 + 280, 1, 1).data]),
        footerRows: Array.from({ length: context.canvas.height }, (_, y) => [...context.getImageData(100, y, 1, 1).data]).filter(([r, g, b]) => r === 0 && g === 200 && b === 223).length
      };
    });
    expect(result.record.state, JSON.stringify(result.record.report)).toBe("complete");
    expect(result.record.report.restoration).toBe("verified by read-back");
    expect(result.markers).toEqual(Array.from({ length: 6 }, () => [26, 180, 92, 255]));
    expect(result.footerRows).toBe(48);
    expect(result.record.semantics.text.some(entry => entry.text.startsWith("Hidden"))).toBe(false);
    expect(result.record.semantics.links.some(entry => entry.url.includes("/hidden-"))).toBe(false);
    const bursts = await page.evaluate(() => window.captureBurstCount);
    const [{ result: queries }] = await worker.evaluate(async tabId => chrome.scripting.executeScript({ target: { tabId }, world: "ISOLATED", func: () => globalThis.winshotBurstQueries }), tabId);
    expect(bursts).toBeGreaterThan(0);
    expect(queries, `${bursts} bursts of 150 writes must expand the wrapper once each`).toBe(bursts);
    expect(await page.evaluate(() => ({ x: scrollX, y: scrollY, footerVisibility: document.querySelector("footer").style.visibility }))).toEqual({ x: 0, y: 0, footerVisibility: "" });
  } finally {
    await browser.close();
    fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
});

test("slow real screenshot readbacks share the cross-window quota without an extra completion delay", async () => {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-readback-quota-"));
  const browser = await launchCaptureBrowser(profile);
  try {
    const extensionId = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const worker = await (await browser.waitForTarget(t => t.type() === "service_worker" && t.url().endsWith("/src/background.js"))).worker();
    const page = await browser.newPage();
    await page.goto("http://127.0.0.1:4173/body");
    await page.bringToFront();
    const result = await worker.evaluate(async () => {
      const [first] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      const other = await chrome.windows.create({ url: "http://127.0.0.1:4173/frame", type: "normal", focused: false, width: 1200, height: 1000 });
      let second;
      const deadline = performance.now() + 10_000;
      do {
        second = await chrome.tabs.get(other.tabs[0].id);
        if (second.status === "complete") break;
        await new Promise(resolve => setTimeout(resolve, 25));
      } while (performance.now() < deadline);
      if (second.status !== "complete") throw new Error("Second fixture window did not load");
      const colors = [[26, 180, 92], [0, 200, 223]];
      for (const [index, tab] of [first, second].entries()) await chrome.scripting.executeScript({ target: { tabId: tab.id }, args: [colors[index]], func: color => {
        document.body.innerHTML = "";
        document.body.style.cssText = `margin:0;height:100vh;min-height:100vh;background:rgb(${color})`;
      } });
      const nativeCapture = chrome.tabs.captureVisibleTab;
      const calls = [];
      chrome.tabs.captureVisibleTab = async (...args) => {
        const call = { windowId: args[0], started: performance.now() };
        calls.push(call);
        const pixels = await Reflect.apply(nativeCapture, chrome.tabs, args);
        // A slow response is injected, but every screenshot still comes from
        // the real native API. The serialized queue must remain held throughout.
        if (calls.length === 1) await new Promise(resolve => setTimeout(resolve, 800));
        call.returned = performance.now();
        return pixels;
      };
      try {
        const records = await Promise.all([first, second].map(tab => globalThis.__winshotTest.runCapture("visible", null, tab, { openEditor: false })));
        return { records, calls, colors };
      } finally { chrome.tabs.captureVisibleTab = nativeCapture; }
    });
    expect(result.records.map(record => record.state), JSON.stringify(result)).toEqual(["complete", "complete"]);
    const auditor = await browser.newPage();
    await auditor.goto(`chrome-extension://${extensionId}/src/editor.html?id=${result.records[0].captureId}`);
    const pixels = await auditor.evaluate(async records => {
      const { getTiles } = await import(chrome.runtime.getURL("src/store.js"));
      const pixels = [];
      for (const record of records) {
        const [tile] = await getTiles(record.captureId);
        if (!tile) throw new Error(`No actual screenshot tile: ${record.error}`);
        const image = await createImageBitmap(tile.blob);
        const context = new OffscreenCanvas(image.width, image.height).getContext("2d", { willReadFrequently: true });
        context.drawImage(image, 0, 0); image.close();
        pixels.push([...context.getImageData(200, 200, 1, 1).data]);
      }
      return pixels;
    }, result.records);
    expect(pixels).toEqual(result.colors.map(color => [...color, 255]));
    await test.info().attach("native-call-timings", {
      contentType: "application/json",
      body: JSON.stringify({
        calls: result.calls,
        invocationSpacingMs: result.calls[1].started - result.calls[0].started,
        nextInvocationAfterReadbackMs: result.calls[1].started - result.calls[0].returned,
        requiredMinimumInvocationSpacingMs: 550,
        injectedFirstResponseDelayMs: 800,
        pixels
      }, null, 2)
    });
    expect(result.calls).toHaveLength(2);
    expect(new Set(result.calls.map(call => call.windowId)).size).toBe(2);
    expect(result.calls[1].started - result.calls[0].started).toBeGreaterThanOrEqual(550);
    expect(result.calls[1].started).toBeGreaterThanOrEqual(result.calls[0].returned);
    // The prior call has already occupied the entire 550ms quota interval.
    // Leave ample scheduler tolerance while rejecting a second full 550ms wait.
    expect(result.calls[1].started - result.calls[0].returned).toBeLessThan(400);
  } finally {
    await browser.close();
    fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
});
