import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";

for (const settingsFailure of [false, true]) test(`image export controls wait for ${settingsFailure ? "failed settings" : "delayed loading"} and the first ready click saves real pixels`, async () => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-editor-readiness-"));
  const browser = await launchCaptureBrowser(path.join(folder, "profile"));
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const worker = await (await browser.waitForTarget(t => t.type() === "service_worker" && t.url().endsWith("/src/background.js"))).worker();
    const page = await browser.newPage(), errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto(`chrome-extension://${id}/src/library.html`);
    const captureId = await page.evaluate(async () => {
      await chrome.storage.local.set({ "winshot:capture-settings": { pdf: { header: "Stored preference" } } });
      const canvas = new OffscreenCanvas(300, 180), ctx = canvas.getContext("2d");
      ctx.fillStyle = "rgb(23,145,67)"; ctx.fillRect(0, 0, 300, 180);
      return (await (await import("./project-file.js")).importImage(new File([await canvas.convertToBlob({ type: "image/png" })], "readiness.png", { type: "image/png" }))).id;
    });
    const downloads = path.join(folder, "downloads"); fs.mkdirSync(downloads);
    await (await page.createCDPSession()).send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: downloads });
    await page.evaluateOnNewDocument(failure => {
      if (!location.pathname.endsWith("/editor.html")) return;
      const native = chrome.storage.local.get;
      const gate = new Promise(resolve => { window.releaseEditorSettings = resolve; });
      chrome.storage.local.get = async function (...args) {
        window.editorSettingsReadStarted = true;
        await gate;
        if (failure) throw new Error("Simulated settings read failure");
        return Reflect.apply(native, chrome.storage.local, args);
      };
    }, settingsFailure);
    const navigation = page.goto(`chrome-extension://${id}/src/editor.html?id=${captureId}`);
    navigation.catch(() => {});
    await page.waitForFunction(() => window.editorSettingsReadStarted === true);
    expect(await page.$$eval("header button", nodes => nodes.every(node => node.disabled))).toBe(true);
    expect(await page.$eval(".editor-bar", node => node.inert)).toBe(true);
    expect(await page.$eval(".zoom-row", node => node.inert)).toBe(true);
    await page.click("#save-png");
    expect(fs.readdirSync(downloads)).toEqual([]);
    await page.evaluate(() => window.releaseEditorSettings()); await navigation;
    await page.waitForSelector('body[data-ready="true"]');
    expect(await page.$eval(".editor-bar", node => node.inert)).toBe(false);
    if (settingsFailure) expect(await page.$eval("#toast", node => node.textContent)).toContain("Using defaults for this tab");
    await page.click("#save-png");
    await expect.poll(() => fs.readdirSync(downloads).filter(name => name.endsWith(".png"))).toHaveLength(1);
    const bytes = fs.readFileSync(path.join(downloads, fs.readdirSync(downloads).find(name => name.endsWith(".png"))));
    const pixels = await page.evaluate(async data => {
      const bitmap = await createImageBitmap(new Blob([new Uint8Array(data)], { type: "image/png" }));
      const c = new OffscreenCanvas(bitmap.width, bitmap.height), ctx = c.getContext("2d"); ctx.drawImage(bitmap, 0, 0); bitmap.close();
      return { width: c.width, height: c.height, pixel: [...ctx.getImageData(25, 25, 1, 1).data] };
    }, [...bytes]);
    expect(pixels).toEqual({ width: 300, height: 180, pixel: [23, 145, 67, 255] });
    if (settingsFailure) {
      await page.$eval("#pdf-options", node => { node.open = true; });
      await page.$eval("#pdf-header", node => { node.value = "Session-only preference"; node.dispatchEvent(new Event("change", { bubbles: true })); });
      await page.waitForFunction(() => document.querySelector("#toast").textContent.includes("only for this tab"));
      expect(await worker.evaluate(async () => (await chrome.storage.local.get("winshot:capture-settings"))["winshot:capture-settings"].pdf.header)).toBe("Stored preference");
    }
    expect(errors).toEqual([]);
  } finally { await browser.close(); fs.rmSync(folder, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); }
});
