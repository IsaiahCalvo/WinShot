import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { getDocument } from "pdfjs-dist/legacy/build/pdf.mjs";
import { launchCaptureBrowser } from "../tests/e2e/launch-browser.mjs";

const urls = process.argv.slice(2);
if (!urls.length || urls.some((url) => !/^https?:\/\//i.test(url))) {
  throw new Error("Usage: npm run test:live -- https://example.com/article [more URLs]");
}
const output = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-live-results-"));
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-live-profile-"));
const proxy = process.env.HTTPS_PROXY || process.env.HTTP_PROXY;
const results = [];
let browser;
try {
  browser = await launchCaptureBrowser(profile, { width: 1200, height: 1000 }, { args: proxy ? [`--proxy-server=${proxy}`] : [] });
  const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../dist/winshot-capture", import.meta.url)));
  const extension = (await browser.extensions()).get(id);
  for (const [index, url] of urls.entries()) {
    const folder = path.join(output, String(index + 1));
    fs.mkdirSync(folder);
    const page = await browser.newPage();
    let editor;
    try {
      const response = await page.goto(url, { waitUntil: "domcontentloaded", timeout: 45_000 });
      if (!response?.ok()) throw new Error(`HTTP ${response?.status() || "no response"}`);
      await page.bringToFront();
      const editorTarget = browser.waitForTarget((target) => target.type() === "page" && target.url().includes("/src/editor.html?id="), { timeout: 180_000 });
      const popupTarget = browser.waitForTarget((target) => target.type() === "page" && target.url().endsWith("/src/popup.html"));
      await extension.triggerAction(page);
      const popup = await (await popupTarget).asPage();
      await popup.waitForSelector("body[data-ready='true']");
      await popup.evaluate(() => {
        document.querySelector("#capture-height").value = "50000";
        document.querySelector("#capture-timeout").value = "2";
        document.querySelector("#capture-tiles").value = "100";
      });
      const start = performance.now();
      await popup.click("[data-mode='full-page']");
      editor = await (await editorTarget).asPage();
      await editor.waitForFunction(() => document.querySelector("#preview img") || document.querySelector("#state.failed"));
      const record = await editor.evaluate(async () => {
        const { getRecord } = await import(chrome.runtime.getURL("src/store.js"));
        return getRecord(new URL(location.href).searchParams.get("id"));
      });
      const elapsedMs = Math.round(performance.now() - start);
      await popup.close().catch(() => {});
      if (!record.report.dimensionsPx) throw new Error(record.error || "No captured pixels.");
      await editor.screenshot({ path: path.join(folder, "editor.png") });
      const client = await editor.createCDPSession();
      await client.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: folder });
      const downloaded = async (suffix) => {
        const deadline = Date.now() + 60_000;
        while (Date.now() < deadline) {
          const name = fs.readdirSync(folder).find((name) => name !== "editor.png" && name.endsWith(suffix));
          if (name) return fs.readFileSync(path.join(folder, name));
          await new Promise((resolve) => setTimeout(resolve, 100));
        }
        throw new Error(`${suffix} export timed out.`);
      };
      await editor.click("#save-png");
      const png = await downloaded(".png");
      if (png.readUInt32BE(16) !== record.report.dimensionsPx.width || png.readUInt32BE(20) !== record.report.dimensionsPx.height) throw new Error("PNG dimensions do not match capture.");
      await editor.waitForSelector("#save-pdf:not([disabled])");
      await editor.click("#save-pdf");
      const pdf = await downloaded(".pdf");
      const task = getDocument({ data: new Uint8Array(pdf), isEvalSupported: false, verbosity: 0 });
      let pdfPages;
      try { pdfPages = (await task.promise).numPages; } finally { await task.destroy(); }
      const result = { url, state: record.state, elapsedMs, pdfPages, pngBytes: png.length, pdfBytes: pdf.length, report: record.report };
      results.push(result);
      console.log(JSON.stringify(result));
    } catch (error) {
      const result = { url, error: error.message };
      results.push(result); console.log(JSON.stringify(result));
    } finally {
      await editor?.close().catch(() => {});
      await page.close().catch(() => {});
    }
  }
} finally {
  await browser?.close();
  fs.rmSync(profile, { recursive: true, force: true });
  fs.writeFileSync(path.join(output, "results.json"), JSON.stringify(results, null, 2));
  console.log(`Evidence: ${output}`);
}
if (results.some((result) => result.error || result.state !== "complete")) process.exitCode = 1;
