// Real public pages + real extension popup/pickers. No personal browser profiles.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { getDocument } from "pdfjs-dist/legacy/build/pdf.mjs";
import { launchCaptureBrowser } from "../tests/e2e/launch-browser.mjs";

const urls = process.argv.slice(2);
if (!urls.length || urls.some(url => !/^https?:\/\//i.test(url))) throw new Error("Provide public HTTP(S) URLs.");
const output = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-live-matrix-"));
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-live-profile-"));
const proxy = process.env.HTTPS_PROXY || process.env.HTTP_PROXY;
const modes = (process.env.WINSHOT_LIVE_MODES || "visible,full-page,region,element").split(",");
if (modes.some(mode => !["visible", "full-page", "region", "element", "frame"].includes(mode))) throw new Error("Unknown capture mode");
const results = [];
let browser;

async function popup(extension, page) {
  await page.bringToFront();
  const prior = new Set(browser.targets());
  const target = browser.waitForTarget(t => !prior.has(t) && t.type() === "page" && t.url().endsWith("/src/popup.html"));
  await extension.triggerAction(page);
  const ui = await (await target).asPage();
  await ui.waitForSelector("[data-mode='full-page']");
  await ui.evaluate(() => {
    document.querySelector("#capture-height").value = "80000";
    document.querySelector("#capture-timeout").value = "2";
    document.querySelector("#capture-tiles").value = "120";
  });
  return ui;
}

async function waitFile(folder, extension) {
  const start = performance.now();
  while (performance.now() - start < 60_000) {
    const file = fs.readdirSync(folder).find(name => !["before.png", "editor.png"].includes(name) && name.endsWith(extension));
    if (file) return fs.readFileSync(path.join(folder, file));
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`${extension} download timed out`);
}

try {
  browser = await launchCaptureBrowser(profile, { width: 1200, height: 1000 }, { args: proxy ? [`--proxy-server=${proxy}`] : [] });
  const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../dist/winshot-capture", import.meta.url)));
  const extension = (await browser.extensions()).get(id);
  for (const [siteIndex, url] of urls.entries()) {
    for (const mode of modes) {
      const folder = path.join(output, String(siteIndex + 1), mode);
      fs.mkdirSync(folder, { recursive: true });
      const page = await browser.newPage();
      let editor, ui, result = { url, mode, folder };
      const pageErrors = [], extensionErrors = [];
      page.on("pageerror", error => pageErrors.push(error.message));
      try {
        await page.setCacheEnabled(false);
        const response = await page.goto(url, { waitUntil: "domcontentloaded", timeout: 45_000 });
        if (!response?.ok() && response?.status() !== 304) throw new Error(`HTTP ${response?.status() || "no response"}`);
        await page.waitForFunction(() => document.body?.innerText.trim().length > 20, { timeout: 15_000 });
        await page.evaluate(() => Promise.race([document.fonts.ready, new Promise(resolve => setTimeout(resolve, 3000))]));
        await new Promise(resolve => setTimeout(resolve, 1200));
        const source = await page.evaluate(() => {
          const nodes = [...document.querySelectorAll("body *")];
          return {
            title: document.title, url: location.href, viewport: { width: innerWidth, height: innerHeight },
            x: scrollX, y: scrollY, text: document.body.innerText.slice(0, 700),
            width: document.scrollingElement.scrollWidth, height: document.scrollingElement.scrollHeight,
            fixed: nodes.filter(node => getComputedStyle(node).position === "fixed").length,
            sticky: nodes.filter(node => getComputedStyle(node).position === "sticky").length,
            animations: document.getAnimations().length, images: document.images.length,
            frames: [...document.querySelectorAll("iframe")].map(node => ({ src: node.src, rect: { x: node.getBoundingClientRect().x, y: node.getBoundingClientRect().y, width: node.clientWidth, height: node.clientHeight } }))
          };
        });
        if (/^(just a moment|access denied|attention required|verify you are human)/i.test(source.title)) throw new Error(`Site blocked automated access: ${source.title}`);
        result.source = source;
        let point;
        if (mode === "element") {
          point = await page.evaluate(() => {
            const choices = [];
            for (let y = 180; y < innerHeight - 150; y += 100) for (let x = 160; x < innerWidth - 150; x += 140) {
              const node = document.elementFromPoint(x, y);
              if (!node || node.closest("nav,header,footer,button,a,dialog") || ["BODY", "HTML"].includes(node.tagName)) continue;
              const rect = node.getBoundingClientRect();
              if (rect.width > 200 && rect.height > 80 && rect.height < 20000) choices.push({ x, y, tag: node.tagName, width: rect.width, height: rect.height });
            }
            return choices.sort((a, b) => b.height - a.height)[0];
          });
          if (!point) throw new Error("No meaningful exact-element target found on the loaded page");
          result.selectedElement = point;
        }
        if (mode === "frame") {
          const frame = source.frames.find(frame => frame.rect.width > 200 && frame.rect.height > 100 && frame.rect.y > 80 && frame.rect.y < 850 && /^https?:/.test(frame.src));
          if (!frame) { result = { ...result, state: "not-applicable", reason: "No visible public HTTP(S) frame" }; continue; }
          point = { x: frame.rect.x + 80, y: frame.rect.y + 80 };
          result.selectedFrame = frame;
        }
        await page.screenshot({ path: path.join(folder, "before.png") });
        const prior = new Set(browser.targets());
        const targetPromise = browser.waitForTarget(t => !prior.has(t) && t.type() === "page" && t.url().includes("/src/editor.html?id="), { timeout: 180_000 });
        // Attach a handler now so a failed picker does not leave an unhandled timeout.
        targetPromise.catch(() => {});
        ui = await popup(extension, page);
        ui.on("pageerror", error => extensionErrors.push(error.message));
        const start = performance.now();
        if (["region", "element", "frame"].includes(mode)) {
          await ui.click(`[data-picker='${mode}']`);
          await page.waitForSelector("[data-winshot-picker='tip']", { timeout: 15_000 });
          if (mode === "region") {
            await page.mouse.move(120, 180);
            await page.mouse.down(); await page.mouse.move(700, 600, { steps: 10 }); await page.mouse.up();
          } else {
            if (mode === "element") await page.keyboard.down("Control");
            await page.mouse.move(point.x, point.y); await page.mouse.click(point.x, point.y);
            if (mode === "element") await page.keyboard.up("Control");
          }
        } else await ui.click(`[data-mode='${mode}']`);
        editor = await (await targetPromise).asPage();
        editor.on("pageerror", error => extensionErrors.push(error.message));
        await editor.waitForSelector("#state:not(:empty)");
        const record = await editor.evaluate(async () => (await import(chrome.runtime.getURL("src/store.js"))).getRecord(new URL(location.href).searchParams.get("id")));
        result.elapsedMs = Math.round(performance.now() - start);
        result.state = record.state; result.report = record.report;
        if (!record.report.dimensionsPx) throw new Error(record.error || "No captured pixels");
        await editor.waitForSelector("#preview img");
        await editor.screenshot({ path: path.join(folder, "editor.png") });
        const client = await editor.createCDPSession();
        await client.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: folder });
        await editor.click("#save-png");
        const png = await waitFile(folder, ".png");
        const size = { width: png.readUInt32BE(16), height: png.readUInt32BE(20) };
        if (size.width !== record.report.dimensionsPx.width || size.height !== record.report.dimensionsPx.height) throw new Error("PNG dimensions do not match saved record");
        if (mode === "visible" && (size.width !== source.viewport.width || size.height !== source.viewport.height)) throw new Error("Visible capture does not match actual viewport");
        if (mode === "region" && (size.width !== 580 || size.height !== 420)) throw new Error(`Selection dimensions wrong: ${JSON.stringify(size)}`);
        result.pngBytes = png.length;
        if (mode === "full-page") {
          await editor.waitForSelector("#save-pdf:not([disabled])");
          await editor.click("#save-pdf");
          const pdf = await waitFile(folder, ".pdf");
          const task = getDocument({ data: new Uint8Array(pdf), isEvalSupported: false, verbosity: 0 });
          try { result.pdfPages = (await task.promise).numPages; } finally { await task.destroy(); }
          result.pdfBytes = pdf.length;
        }
        const restored = await page.evaluate(() => ({ x: scrollX, y: scrollY, captureStyles: document.querySelectorAll("style[data-winshot-capture]").length, pickers: document.querySelectorAll("[data-winshot-picker]").length }));
        result.restored = restored;
        if (Math.abs(restored.x - source.x) > 2 || Math.abs(restored.y - source.y) > 2 || restored.captureStyles || restored.pickers) throw new Error("Page/picker state was not restored");
        if (extensionErrors.length) throw new Error(`Extension errors: ${extensionErrors.join("; ")}`);
      } catch (error) { result.error = error.message; }
      finally {
        result.pageErrors = [...new Set(pageErrors)]; result.extensionErrors = [...new Set(extensionErrors)];
        results.push(result); console.log(JSON.stringify(result));
        await ui?.close().catch(() => {}); await editor?.close().catch(() => {}); await page.close().catch(() => {});
        fs.writeFileSync(path.join(output, "results.json"), JSON.stringify(results, null, 2));
      }
    }
  }
  if (process.env.WINSHOT_LIVE_BATCHES === "1") {
    const batchUrls = urls.slice(0, 3);
    for (const variant of [
      { kind: "all-tabs", mode: "visible" }, { kind: "all-tabs", mode: "full-page" },
      { kind: "urls", mode: "visible" }, { kind: "urls", mode: "full-page" },
      { kind: "all-tabs", mode: "visible", combinePdf: true }
    ]) {
      const name = `${variant.kind}-${variant.mode}${variant.combinePdf ? "-pdf" : ""}`;
      const folder = path.join(output, "batches", name);
      fs.mkdirSync(folder, { recursive: true });
      const pages = []; let summary, ui;
      const result = { batch: name, urls: batchUrls, folder };
      try {
        for (const url of variant.kind === "urls" ? batchUrls.slice(0, 1) : batchUrls) {
          const page = await browser.newPage(); pages.push(page);
          await page.setCacheEnabled(false);
          const response = await page.goto(url, { waitUntil: "domcontentloaded", timeout: 45_000 });
          if (!response?.ok() && response?.status() !== 304) throw new Error(`HTTP ${response?.status()} loading batch input`);
        }
        const client = await pages[0].createCDPSession();
        await client.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: folder });
        const prior = new Set(browser.targets());
        const target = browser.waitForTarget(t => !prior.has(t) && t.type() === "page" && t.url().includes("/src/batch.html?id="), { timeout: 240_000 });
        target.catch(() => {});
        ui = await popup(extension, pages[0]);
        await ui.select("#batch-mode", variant.mode);
        if (variant.kind === "urls") await ui.type("#batch-urls", batchUrls.join("\n"));
        const start = performance.now();
        await ui.click(variant.combinePdf ? "#all-tabs-pdf" : variant.kind === "urls" ? "#url-batch" : "#all-tabs");
        summary = await (await target).asPage();
        await summary.waitForSelector(".item");
        result.elapsedMs = Math.round(performance.now() - start);
        const batch = await summary.evaluate(async () => {
          const key = `batch:${new URL(location.href).searchParams.get("id")}`;
          return (await chrome.storage.local.get(key))[key];
        });
        result.results = batch.results; result.skipped = batch.skipped;
        if (batch.results.length !== batchUrls.length || batch.results.some(item => item.state !== "complete")) throw new Error("Batch contains missing, failed, or partial captures");
        result.records = await summary.evaluate(async results => {
          const store = await import(chrome.runtime.getURL("src/store.js"));
          return Promise.all(results.map(async item => { const record = await store.getRecord(item.captureId); return { state: record.state, report: record.report }; }));
        }, batch.results);
        await summary.screenshot({ path: path.join(folder, "summary.png") });
        if (variant.combinePdf) {
          const pdf = await waitFile(folder, ".pdf");
          const task = getDocument({ data: new Uint8Array(pdf), isEvalSupported: false, verbosity: 0 });
          try { result.pdfPages = (await task.promise).numPages; } finally { await task.destroy(); }
        }
        result.restored = await Promise.all(pages.map(page => page.evaluate(() => ({ x: scrollX, y: scrollY, styles: document.querySelectorAll("style[data-winshot-capture]").length }))));
        if (result.restored.some(state => state.x || state.y || state.styles)) throw new Error("Batch did not restore an input page");
        const remainingWebPages = (await browser.pages()).filter(page => /^https?:/.test(page.url()));
        if (remainingWebPages.length !== pages.length) throw new Error("URL batch leaked disposable tabs");
        result.state = "complete";
      } catch (error) { result.error = error.message; }
      finally {
        results.push(result); console.log(JSON.stringify(result));
        await ui?.close().catch(() => {}); await summary?.close().catch(() => {});
        await Promise.all(pages.map(page => page.close().catch(() => {})));
        fs.writeFileSync(path.join(output, "results.json"), JSON.stringify(results, null, 2));
      }
    }
  }
} finally {
  await browser?.close(); fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  console.log(`Evidence: ${output}`);
}
if (results.some(result => result.error || !["complete", "not-applicable"].includes(result.state))) process.exitCode = 1;
