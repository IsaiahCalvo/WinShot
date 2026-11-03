// Editor markup: the desktop-style tools draw on the capture, the marks persist with
// the record, every export burns them in (with crop and resize), and FireShot-style
// after-capture actions and image import work end to end in the packaged extension.
import { test, expect } from "@playwright/test";
import puppeteer from "puppeteer";
import { sandboxArgs } from "./launch-args.mjs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "node:fs";
import os from "node:os";

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionPath = path.resolve(here, "..", "..", "dist", "winshot-capture");
const shotDir = path.resolve(here, "..", "..", "..", "output", "editor-screens");
const SETTINGS_KEY = "winshot:capture-settings";

function removeOwnedDirectory(directory, prefix) {
  const resolved = path.resolve(directory);
  if (path.dirname(resolved) !== path.resolve(os.tmpdir()) || !path.basename(resolved).startsWith(prefix)) {
    throw new Error(`Refusing to clean unexpected test path: ${resolved}`);
  }
  fs.rmSync(resolved, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
}

async function launch({ downloadDir = null } = {}) {
  const profileRoot = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-editor-e2e-"));
  if (downloadDir) {
    // A profile download folder (not CDP's download override, which renames files to
    // GUIDs and drops sub-folders) so the extension's own folder choice is observable.
    fs.mkdirSync(path.join(profileRoot, "Default"), { recursive: true });
    fs.writeFileSync(path.join(profileRoot, "Default", "Preferences"), JSON.stringify({
      download: { default_directory: downloadDir, prompt_for_download: false, directory_upgrade: true }
    }));
  }
  const errors = [];
  const browser = await puppeteer.launch({
    executablePath: await puppeteer.executablePath(),
    headless: true,
    userDataDir: profileRoot,
    enableExtensions: true,
    defaultViewport: { width: 1280, height: 900 },
    args: [...sandboxArgs, "--window-size=1280,900"]
  });
  const watch = (page) => {
    page.on("console", (message) => { if (message.type() === "error") errors.push(`console: ${message.text()}`); });
    page.on("pageerror", (error) => errors.push(`pageerror: ${error.message}`));
  };
  browser.on("targetcreated", async (target) => {
    if (target.type() !== "page") return;
    const page = await target.page().catch(() => null);
    if (page) watch(page);
  });
  const extensionId = await browser.installExtension(extensionPath);
  const extension = (await browser.extensions()).get(extensionId);
  const workerTarget = await browser.waitForTarget(
    (target) => target.type() === "service_worker" && target.url().endsWith("/src/background.js"), { timeout: 15_000 });
  const worker = await workerTarget.worker();
  return { browser, extension, worker, profileRoot, errors };
}

async function close(run) {
  await run.browser.close();
  removeOwnedDirectory(run.profileRoot, "winshot-editor-e2e-");
}

function newPageTarget(run, fragment) {
  const existing = new Set(run.browser.targets());
  return run.browser.waitForTarget(
    (target) => !existing.has(target) && target.type() === "page" && target.url().includes(run.extension.id) && target.url().includes(fragment),
    { timeout: 120_000 });
}

async function captureVisible(run, page) {
  await page.bringToFront();
  const waiting = newPageTarget(run, "/src/editor.html?id=");
  await run.worker.evaluate(async () => {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    await globalThis.__winshotTest.runCapture("visible", null, tab);
  });
  const editor = await (await waiting).asPage();
  await editor.waitForSelector("#state:not(:empty)");
  await editor.waitForFunction(() => globalThis.__winshotEditor && document.querySelector("#view").width > 0);
  return editor;
}

async function downloadsTo(page) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-editor-dl-"));
  const session = await page.createCDPSession();
  await session.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: root });
  return root;
}

async function waitForFile(root, extension, timeout = 60_000) {
  const done = () => {
    const name = fs.readdirSync(root).find((value) => value.endsWith(extension));
    if (!name) return null;
    const bytes = fs.readFileSync(path.join(root, name));
    return bytes.length ? name : null;
  };
  await expect.poll(done, { timeout }).toBeTruthy();
  return fs.readFileSync(path.join(root, done()));
}

// Decode an image file in the browser and sample pixels.
async function inspectImage(page, bytes, points) {
  return await page.evaluate(async (base64, samples) => {
    const blob = await (await fetch(`data:image/png;base64,${base64}`)).blob();
    const bitmap = await createImageBitmap(blob);
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
    const context = canvas.getContext("2d");
    context.drawImage(bitmap, 0, 0);
    const read = (x, y) => [...context.getImageData(x, y, 1, 1).data.slice(0, 3)];
    return { width: bitmap.width, height: bitmap.height, samples: samples.map(([x, y]) => read(x, y)) };
  }, bytes.toString("base64"), points);
}

test("desktop-style markup burns into exports, persists, and supports crop, resize, undo, and pointer drawing", async () => {
  const run = await launch();
  let root;
  try {
    const page = await run.browser.newPage();
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    const editor = await captureVisible(run, page);
    fs.mkdirSync(shotDir, { recursive: true });

    // Toolbar shows the desktop's tool groups with real icons.
    const toolbar = await editor.evaluate(() => ({
      categories: [...document.querySelectorAll("[data-category]")].map((node) => node.getAttribute("aria-label")),
      iconCount: document.querySelectorAll("#toolbar svg").length,
      // Page and share actions are icon buttons (Lucide), as on the desktop bottom bar.
      actionIcons: ["#copy-clipboard", "#print-capture", "#open-desktop", "#open-history", "#open-options", "#toggle-details", "#zoom-fit"]
        .filter((selector) => document.querySelector(`${selector} svg`)).length,
      detailsHidden: !document.querySelector("#details").classList.contains("open")
    }));
    expect(toolbar.categories).toEqual(["Draw", "Shapes", "Text", "Effects"]);
    expect(toolbar.iconCount).toBeGreaterThanOrEqual(9);
    expect(toolbar.actionIcons).toBe(7);
    // Capture details start tucked into the rail and slide out on demand.
    expect(toolbar.detailsHidden).toBe(true);
    await editor.click("#toggle-details");
    expect(await editor.evaluate(() => document.querySelector("#details").classList.contains("open"))).toBe(true);
    await editor.click("#close-details");
    expect(await editor.evaluate(() => document.querySelector("#details").classList.contains("open"))).toBe(false);

    await editor.evaluate(() => globalThis.__winshotEditor.setEdits({
      annotations: [
        { id: "r1", type: "rect", x: 40, y: 140, w: 240, h: 120, color: "#ff0000", width: 8, fill: "none", dash: "solid" },
        { id: "a1", type: "arrow", x1: 400, y1: 300, x2: 560, y2: 180, color: "#0000ff", width: 6, dash: "solid" },
        { id: "s1", type: "step", x: 700, y: 200, n: 3, color: "#00aa00", fontSize: 28 },
        { id: "t1", type: "text", x: 60, y: 420, text: "WinShot note", color: "#ff00ff", fontSize: 36, textStyle: "bold" },
        { id: "x1", type: "redact", x: 0, y: 0, w: 600, h: 60, redactStyle: "blackout" },
        { id: "h1", type: "highlighter", points: [[300, 520], [700, 520]], color: "#ffcc00", width: 24 }
      ],
      crop: { x: 20, y: 20, w: 900, h: 600 },
      scale: 1
    }));
    await editor.evaluate(() => globalThis.__winshotEditor.persist());
    await new Promise((resolve) => setTimeout(resolve, 300));
    await editor.screenshot({ path: path.join(shotDir, "editor-marked.png") });

    // Export: crop applied, marks burned in at crop-relative positions.
    root = await downloadsTo(editor);
    await editor.click("#save-png");
    const png = await waitForFile(root, ".png");
    const shot = await inspectImage(editor, png, [
      [40 - 20, 200 - 20],   // left edge of the red rectangle
      [100, 10],             // inside the black-out (y 0..60 → crop y 0..40)
      [700 - 20 - 14, 200 - 20], // inside the green number badge, left of its digit
      [150, 300]             // untouched page area inside the rectangle
    ]);
    expect(shot.width).toBe(900);
    expect(shot.height).toBe(600);
    const [edge, blackout, badge, plain] = shot.samples;
    expect(edge[0]).toBeGreaterThan(200);
    expect(edge[1]).toBeLessThan(60);
    expect(blackout.every((value) => value < 20)).toBe(true);
    expect(badge[1]).toBeGreaterThan(120);
    expect(badge[0]).toBeLessThan(80);
    expect(plain[0]).toBeGreaterThan(150);

    // The marks are saved with the capture and come back on reload.
    await editor.reload({ waitUntil: "domcontentloaded" });
    await editor.waitForFunction(() => globalThis.__winshotEditor?.edits?.annotations?.length === 6);

    // Resize applies to exports.
    await editor.evaluate(() => {
      const current = globalThis.__winshotEditor.edits;
      globalThis.__winshotEditor.setEdits({ ...current, scale: 0.5 });
    });
    fs.rmSync(path.join(root, fs.readdirSync(root)[0]));
    await editor.click("#save-png");
    const half = await inspectImage(editor, await waitForFile(root, ".png"), [[0, 0]]);
    expect(half.width).toBe(450);
    expect(half.height).toBe(300);

    // Real pointer drawing with the Rectangle tool, then undo and redo.
    await editor.evaluate(() => globalThis.__winshotEditor.setEdits({ annotations: [], crop: null, scale: 1 }));
    await editor.keyboard.press("r");
    expect(await editor.evaluate(() => globalThis.__winshotEditor.tool)).toBe("rect");
    const start = await editor.evaluate(() => globalThis.__winshotEditor.imageToScreen(100, 100));
    const end = await editor.evaluate(() => globalThis.__winshotEditor.imageToScreen(300, 250));
    const box = await editor.$eval("#stage", (node) => { const r = node.getBoundingClientRect(); return { x: r.left, y: r.top }; });
    await editor.mouse.move(box.x + start.x, box.y + start.y);
    await editor.mouse.down();
    await editor.mouse.move(box.x + (start.x + end.x) / 2, box.y + (start.y + end.y) / 2, { steps: 4 });
    await editor.mouse.move(box.x + end.x, box.y + end.y, { steps: 4 });
    await editor.mouse.up();
    const drawn = await editor.evaluate(() => globalThis.__winshotEditor.edits.annotations);
    expect(drawn).toHaveLength(1);
    expect(drawn[0].type).toBe("rect");
    expect(Math.round(drawn[0].w)).toBeGreaterThan(150);
    await editor.keyboard.down("Control");
    await editor.keyboard.press("z");
    await editor.keyboard.up("Control");
    expect(await editor.evaluate(() => globalThis.__winshotEditor.edits.annotations.length)).toBe(0);
    await editor.keyboard.down("Control");
    await editor.keyboard.press("y");
    await editor.keyboard.up("Control");
    expect(await editor.evaluate(() => globalThis.__winshotEditor.edits.annotations.length)).toBe(1);

    // Text tool: click, type, commit.
    await editor.keyboard.press("t");
    const textAt = await editor.evaluate(() => globalThis.__winshotEditor.imageToScreen(500, 500));
    await editor.mouse.click(box.x + textAt.x, box.y + textAt.y);
    await editor.waitForSelector("#text-editor:not([hidden])");
    await editor.waitForFunction(() => document.activeElement?.id === "text-editor");
    await editor.keyboard.type("Hello");
    await editor.keyboard.down("Control");
    await editor.keyboard.press("Enter");
    await editor.keyboard.up("Control");
    const texts = await editor.evaluate(() => globalThis.__winshotEditor.edits.annotations.filter((a) => a.type === "text").map((a) => a.text));
    expect(texts).toEqual(["Hello"]);
    await editor.screenshot({ path: path.join(shotDir, "editor-drawn.png") });

    expect(run.errors).toEqual([]);
  } finally {
    if (root) removeOwnedDirectory(root, "winshot-editor-dl-");
    await close(run);
  }
});

test("pixelate and blur redactions hide the original pixels in the export", async () => {
  const run = await launch();
  let root;
  try {
    const page = await run.browser.newPage();
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    const editor = await captureVisible(run, page);
    root = await downloadsTo(editor);
    await editor.click("#save-png");
    const original = await waitForFile(root, ".png");
    fs.rmSync(path.join(root, fs.readdirSync(root)[0]));
    await editor.evaluate(() => globalThis.__winshotEditor.setEdits({
      annotations: [
        { id: "p", type: "redact", x: 0, y: 0, w: 640, h: 120, redactStyle: "pixelate" },
        { id: "b", type: "redact", x: 0, y: 120, w: 640, h: 120, redactStyle: "blur" }
      ]
    }));
    await editor.click("#save-png");
    const redacted = await waitForFile(root, ".png");
    // Compare a heading row of pixels: the redacted rows must differ from the original
    // and a pixelated row must be made of flat blocks.
    const result = await editor.evaluate(async (a, b) => {
      const load = async (base64) => {
        const bitmap = await createImageBitmap(await (await fetch(`data:image/png;base64,${base64}`)).blob());
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
        const context = canvas.getContext("2d");
        context.drawImage(bitmap, 0, 0);
        return context;
      };
      const before = await load(a);
      const after = await load(b);
      const diff = (y) => {
        const x = before.getImageData(0, y, 640, 1).data;
        const z = after.getImageData(0, y, 640, 1).data;
        let total = 0;
        for (let i = 0; i < x.length; i += 4) total += Math.abs(x[i] - z[i]);
        return total;
      };
      const row = after.getImageData(0, 40, 14, 1).data;
      const flat = row.every((value, index) => value === row[index % 4]);
      return { pixelDiff: diff(40), blurDiff: diff(160), untouched: diff(500), flat };
    }, original.toString("base64"), redacted.toString("base64"));
    expect(result.pixelDiff).toBeGreaterThan(0);
    expect(result.blurDiff).toBeGreaterThan(0);
    expect(result.untouched).toBe(0);
    expect(result.flat).toBe(true);
    expect(run.errors).toEqual([]);
  } finally {
    if (root) removeOwnedDirectory(root, "winshot-editor-dl-");
    await close(run);
  }
});

test("after-capture 'save' downloads the image without leaving an editor open, into the chosen folder", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-editor-dl-"));
  const run = await launch({ downloadDir: root });
  try {
    await run.worker.evaluate(async (key) => {
      const stored = (await chrome.storage.local.get(key))[key] || {};
      await chrome.storage.local.set({ [key]: { ...stored, afterCapture: "save", downloadFolder: "WinShot/Web" } });
    }, SETTINGS_KEY);
    const page = await run.browser.newPage();
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    const waiting = newPageTarget(run, "/src/editor.html?id=");
    await run.worker.evaluate(async () => {
      const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
      await globalThis.__winshotTest.runCapture("visible", null, tab);
    });
    const target = await waiting;
    expect(target.url()).toContain("then=save");
    const folder = path.join(root, "WinShot", "Web");
    await expect.poll(() => fs.existsSync(folder) && fs.readdirSync(folder).some((name) => name.endsWith(".png") && !name.endsWith(".crdownload")), { timeout: 60_000 }).toBe(true);
    await expect.poll(() => run.browser.targets().some((t) => t.url().includes("/src/editor.html?id=")), { timeout: 15_000 }).toBe(false);
    expect(run.errors).toEqual([]);
  } finally {
    await close(run);
    removeOwnedDirectory(root, "winshot-editor-dl-");
  }
});

test("history opens a local image in the editor and the full right-click menu is registered", async () => {
  const run = await launch();
  try {
    const history = await run.browser.newPage();
    await history.goto(`chrome-extension://${run.extension.id}/src/history.html`, { waitUntil: "domcontentloaded" });
    const id = await history.evaluate(async () => {
      const canvas = new OffscreenCanvas(320, 200);
      const context = canvas.getContext("2d");
      context.fillStyle = "#123456";
      context.fillRect(0, 0, 320, 200);
      const blob = await canvas.convertToBlob({ type: "image/png" });
      const { importImage } = await import(chrome.runtime.getURL("src/import.js"));
      return await importImage(blob, "pasted.png");
    });
    await history.goto(`chrome-extension://${run.extension.id}/src/editor.html?id=${id}`, { waitUntil: "domcontentloaded" });
    await history.waitForFunction(() => globalThis.__winshotEditor && document.querySelector("#title").textContent === "pasted");
    const enabled = await history.$eval("#save-png", (node) => !node.disabled);
    expect(enabled).toBe(true);

    // Options page loads and saves.
    const options = await run.browser.newPage();
    await options.goto(`chrome-extension://${run.extension.id}/src/options.html`, { waitUntil: "domcontentloaded" });
    await options.select("#after-capture", "save-pdf");
    await expect.poll(() => run.worker.evaluate(async (key) => (await chrome.storage.local.get(key))[key]?.afterCapture, SETTINGS_KEY)).toBe("save-pdf");
    expect(await options.$$eval("#shortcuts kbd", (nodes) => nodes.length)).toBeGreaterThanOrEqual(4);
    expect(run.errors).toEqual([]);
  } finally {
    await close(run);
  }
});
