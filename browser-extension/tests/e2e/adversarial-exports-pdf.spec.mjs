// Adversarial QA: editor exports (PNG/JPEG/WebP/PDF), custom page sizes, PDF
// furniture with hostile strings, Unicode text layer, link-annotation safety,
// filename templates with illegal characters and long titles, counter serialization.
//
// Launch/helper pattern replicated from puppeteer-extension.spec.mjs (not exported there).
// Fixtures reuse the existing server on 127.0.0.1:4173; hostile DOM/titles are
// injected into /body at runtime so no fixture-server changes are required.
import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import fs from "node:fs";
import os from "node:os";
import { getDocument } from "pdfjs-dist/legacy/build/pdf.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionPath = process.env.WINSHOT_EXTENSION_PATH || path.resolve(here, "..", "..", "dist", "winshot-capture");

function pdfUtf16Hex(value) {
  return [...value].map((character) => {
    const codePoint = character.codePointAt(0);
    if (codePoint <= 0xffff) return codePoint.toString(16).padStart(4, "0");
    const adjusted = codePoint - 0x10000;
    return `${(0xd800 + (adjusted >> 10)).toString(16).padStart(4, "0")}${(0xdc00 + (adjusted & 0x3ff)).toString(16).padStart(4, "0")}`;
  }).join("").toUpperCase();
}

async function inspectPdf(bytes) {
  const loading = getDocument({ data: new Uint8Array(bytes), isEvalSupported: false, verbosity: 0 });
  const document = await loading.promise;
  try {
    const pageTexts = [];
    const links = [];
    for (let number = 1; number <= document.numPages; number++) {
      const page = await document.getPage(number);
      const content = await page.getTextContent();
      pageTexts.push(content.items.map((item) => item.str).join(" "));
      for (const annotation of await page.getAnnotations()) {
        if (annotation.url) links.push(annotation.url);
      }
    }
    return { pages: document.numPages, pageTexts, text: pageTexts.join(" "), links };
  } finally {
    await loading.destroy();
  }
}

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
  if (!extension) throw new Error("The exact WinShot MV3 extension package did not load.");
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

// Sets document.title and waits until the tabs API (which feeds record.sourceTitle) sees it.
async function setTabTitle(run, page, title) {
  await page.bringToFront();
  await page.evaluate((value) => { document.title = value; }, title);
  await expect.poll(() => run.worker.evaluate(async () => {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    return tab?.title || "";
  }), { timeout: 10_000 }).toBe(title);
}

async function capture(run, page, mode, beforeCapture = null) {
  const waitingEditor = editorTarget(run);
  const popup = await openPopup(run, page);
  if (beforeCapture) await beforeCapture(popup);
  await popup.click(`[data-mode='${mode}']`);
  const result = await recordFromEditorTarget(await waitingEditor);
  await popup.close().catch(() => {});
  return result;
}

async function downloadDirectory(editor) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-adversarial-dl-"));
  const session = await editor.createCDPSession();
  await session.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: root });
  return root;
}

async function waitForFile(root, extension, timeout = 60_000) {
  // Poll until the file exists AND is fully written — a bare existence check can
  // read the PDF while Chrome is still streaming it (0-byte or truncated read).
  const complete = () => {
    const name = fs.readdirSync(root).find((value) => value.endsWith(extension));
    if (!name) return null;
    const bytes = fs.readFileSync(path.join(root, name));
    if (!bytes.length) return null;
    if (extension === ".pdf" && !bytes.subarray(-32).toString("latin1").includes("%%EOF")) return null;
    return name;
  };
  await expect.poll(complete, { timeout }).toBeTruthy();
  const name = complete();
  return { name, bytes: fs.readFileSync(path.join(root, name)) };
}

async function setPdfOption(editor, selector, value) {
  await editor.$eval(selector, (node, next) => {
    node.value = next;
    node.dispatchEvent(new Event("change", { bubbles: true }));
  }, value);
}

test("hostile page titles are reduced to legal filesystem filenames", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });

    // Capture A: every Windows-illegal character plus a literal percent sequence.
    await setTabTitle(run, page, 'Q4 <secret>:"pipe|q?*/\\ 100%usd deal');
    const first = await capture(run, page, "visible");
    expect(first.record, first.record.error).toMatchObject({ state: "complete" });
    expect(first.record.fileName).not.toMatch(/[<>:"/\\|?*\x00-\x1f]/);
    expect(first.record.fileName.length).toBeLessThanOrEqual(180);
    // A percent sign in a page title is legal in a filename and must survive verbatim.
    // Today renderFileName runs FireShot %-token replacement over the already-substituted
    // {title} text, so "%u" inside the title expands to the page URL (settings.js).
    expect.soft(first.record.fileName).toContain("100%usd");

    // The hostile name must actually download.
    const downloadRoot = await downloadDirectory(first.editor);
    try {
      await first.editor.click("#save-png");
      const png = await waitForFile(downloadRoot, ".png", 30_000);
      expect([...png.bytes.subarray(0, 8)]).toEqual([137, 80, 78, 71, 13, 10, 26, 10]);
    } finally {
      removeOwnedDirectory(downloadRoot, "winshot-adversarial-dl-");
    }
    await first.editor.close();

    // Capture B: a 220-character title crafted so the 180-character cut lands right
    // after a dot. Windows rejects filenames ending in "." or " " — the trailing-junk
    // strip in safePart runs before the final slice(0,180), so it can reappear.
    await setTabTitle(run, page, `${"A".repeat(179)}.${"B".repeat(40)}`);
    const second = await capture(run, page, "visible");
    expect(second.record, second.record.error).toMatchObject({ state: "complete" });
    expect(second.record.fileName.length).toBeLessThanOrEqual(180);
    expect(second.record.fileName).not.toMatch(/[<>:"/\\|?*\x00-\x1f]/);
    expect.soft(second.record.fileName).not.toMatch(/[. ]$/);
    await second.editor.close();
  } finally {
    await closeExactExtension(run);
  }
});

test("filename counter serializes across captures in both {counter} and %n styles", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });

    const first = await capture(run, page, "visible", async (popup) => {
      await popup.$eval("#filename-template", (node) => {
        node.value = "adv-{counter}";
        node.dispatchEvent(new Event("change", { bubbles: true }));
      });
    });
    expect(first.record, first.record.error).toMatchObject({ state: "complete", fileName: "adv-001" });
    await first.editor.close();

    // FireShot-style %n must continue the same counter, not restart it.
    const second = await capture(run, page, "visible", async (popup) => {
      await popup.$eval("#filename-template", (node) => {
        node.value = "shot-%n";
        node.dispatchEvent(new Event("change", { bubbles: true }));
      });
    });
    expect(second.record, second.record.error).toMatchObject({ state: "complete", fileName: "shot-002" });
    await second.editor.close();

    // The reservation must be persisted, not just in-memory.
    // Dynamic import() is disallowed in MV3 service workers, so read the raw
    // storage key (SETTINGS_KEY in src/settings.js) instead of loadSettings().
    const stored = await run.worker.evaluate(async () => {
      const all = await chrome.storage.local.get("winshot:capture-settings");
      return all["winshot:capture-settings"]?.fileNameCounter;
    });
    expect(stored).toBe(3);
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("PDF from a hostile page: injection-proof furniture, unsafe links excluded, Unicode text layer intact", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    await page.evaluate(() => {
      const unicode = document.createElement("p");
      unicode.id = "adv-unicode";
      unicode.style.cssText = "font-size:20px;padding:12px;margin:0";
      unicode.textContent = "UNICODE-LAYER é \u{1F389} 中文测试";
      document.body.prepend(unicode);
      const anchors = [
        ["javascript:alert(1)", "JS-EVIL-LINK"],
        ["data:text/html,<h1>evil</h1>", "DATA-EVIL-LINK"],
        ["https://example.com/adversarial-safe", "SAFE-ADV-LINK"]
      ];
      for (const [href, label] of anchors) {
        const anchor = document.createElement("a");
        anchor.setAttribute("href", href);
        anchor.textContent = label;
        anchor.style.cssText = "display:block;padding:8px;font-size:18px";
        document.body.prepend(anchor);
      }
    });
    // Title carries PDF string metacharacters aimed at the header/footer literals
    // and at the trailer /Title.
    await setTabTitle(run, page, 'ADV (secret) \\ evil) Tj ET (report');

    const { editor, record } = await capture(run, page, "full-page");
    expect(record, record.error).toMatchObject({ state: "complete" });
    // javascript:/data: anchors must never reach the capture's semantic link layer.
    const urls = (record.semantics?.links || []).map((entry) => entry.url);
    expect(urls.some((url) => /^(javascript|data):/i.test(url))).toBe(false);
    expect(urls).toContain("https://example.com/adversarial-safe");

    const downloadRoot = await downloadDirectory(editor);
    try {
      await setPdfOption(editor, "#pdf-page-size", "a4");
      await setPdfOption(editor, "#pdf-layout", "multipage");
      await setPdfOption(editor, "#pdf-header", "H {title}");
      await setPdfOption(editor, "#pdf-footer", "Page {page} of {pages}");
      await setPdfOption(editor, "#pdf-watermark", "ADV-WM (guard)");
      await editor.click("#save-pdf");
      const { bytes } = await waitForFile(downloadRoot, ".pdf");
      const raw = bytes.toString("latin1");
      expect(bytes.subarray(0, 8).toString("ascii")).toBe("%PDF-1.7");
      expect(raw.trimEnd().endsWith("%%EOF")).toBe(true);
      // Unicode text layer bytes: CJK, an emoji surrogate pair, and a combining accent.
      expect(raw).toContain(pdfUtf16Hex("中文测试"));
      expect(raw).toContain(pdfUtf16Hex("\u{1F389}"));
      expect(raw).toContain(pdfUtf16Hex("é"));

      const parsed = await inspectPdf(bytes);
      expect(parsed.pages).toBeGreaterThan(1);
      // Header renders the hostile title as text — parentheses stayed data, and the
      // page still parses (no injected "Tj ET" escaped the string literal).
      expect(parsed.pageTexts[0]).toContain("H ADV (secret)");
      expect(parsed.pageTexts[0]).toContain("Page 1 of");
      expect(parsed.pageTexts[parsed.pages - 1]).toContain(`Page ${parsed.pages} of ${parsed.pages}`);
      expect(parsed.text).toContain("ADV-WM (guard)");
      expect(parsed.text).toContain("中文测试");
      expect(parsed.text).toContain("\u{1F389}");
      // Link annotations: only http(s) targets, and the safe one survives.
      expect(parsed.links.some((url) => /^(javascript|data):/i.test(url))).toBe(false);
      expect(parsed.links).toContain("https://example.com/adversarial-safe");
      expect(parsed.links).toContain("https://example.com/winshot-link");
    } finally {
      removeOwnedDirectory(downloadRoot, "winshot-adversarial-dl-");
    }
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("absurd custom page sizes clamp (0.1in -> 1in, 100in) and paginate to an honest {pages}", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    const { editor, record } = await capture(run, page, "visible");
    expect(record, record.error).toMatchObject({ state: "complete" });

    const downloadRoot = await downloadDirectory(editor);
    try {
      await setPdfOption(editor, "#pdf-page-size", "custom");
      await setPdfOption(editor, "#pdf-layout", "multipage");
      await setPdfOption(editor, "#pdf-header", "H");
      await setPdfOption(editor, "#pdf-footer", "Page {page} of {pages}");
      await setPdfOption(editor, "#pdf-width", "0.1");
      await setPdfOption(editor, "#pdf-height", "0.1");
      // Settings round-trip must clamp 0.1in to the 1in floor and show it.
      expect(await editor.$eval("#pdf-width", (node) => node.value)).toBe("1");
      expect(await editor.$eval("#pdf-height", (node) => node.value)).toBe("1");
      await editor.click("#save-pdf");
      const tiny = await waitForFile(downloadRoot, ".pdf");
      const tinyRaw = tiny.bytes.toString("latin1");
      expect(tinyRaw).toContain("/MediaBox [0 0 72.000 72.000]");
      const tinyParsed = await inspectPdf(tiny.bytes);
      // 1in pages with header+footer leave almost no image area: many pages, but the
      // export must terminate and number them honestly.
      expect(tinyParsed.pages).toBeGreaterThan(5);
      expect(tinyParsed.pages).toBeLessThan(200);
      // pdf.js getTextContent drops glyphs positioned outside the MediaBox, and on a
      // 1-inch page the tail of "Page 1 of NN" at 10pt lands past x=72, so assert the
      // honest footer on the raw content stream instead of the clipped extraction.
      expect(tinyRaw).toContain(`(Page 1 of ${tinyParsed.pages}) Tj`);
      expect(tinyRaw).toContain(`(Page ${tinyParsed.pages} of ${tinyParsed.pages}) Tj`);
      fs.rmSync(path.join(downloadRoot, tiny.name));

      await setPdfOption(editor, "#pdf-width", "100");
      await setPdfOption(editor, "#pdf-height", "100");
      await editor.click("#save-pdf");
      const huge = await waitForFile(downloadRoot, ".pdf");
      const hugeRaw = huge.bytes.toString("latin1");
      expect(hugeRaw).toContain("/MediaBox [0 0 7200.000 7200.000]");
      const hugeParsed = await inspectPdf(huge.bytes);
      expect(hugeParsed.pages).toBe(1);
      expect(hugeParsed.pageTexts[0]).toContain("Page 1 of 1");
    } finally {
      removeOwnedDirectory(downloadRoot, "winshot-adversarial-dl-");
    }
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("giant capture: JPEG and WebP refuse gracefully with guidance and leave the editor usable", async () => {
  test.setTimeout(180_000);
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/long", { waitUntil: "networkidle0" });
    const { editor, record } = await capture(run, page, "full-page");
    expect(record, record.error).toMatchObject({ state: "complete" });
    expect(record.report.dimensionsPx.height).toBeGreaterThan(16_384);

    const downloadRoot = await downloadDirectory(editor);
    try {
      for (const [selector, extension] of [["#save-jpeg", ".jpg"], ["#save-webp", ".webp"]]) {
        await editor.waitForSelector(`${selector}:not([disabled])`);
        await editor.click(selector);
        // The single-canvas guard must fail fast with actionable guidance, not hang or crash.
        try {
          await editor.waitForFunction(() =>
            /very large captures/i.test(document.querySelector("#toast")?.textContent || ""), { timeout: 15_000 });
        } catch (error) {
          await test.info().attach("large-export-ui", { contentType: "application/json", body: JSON.stringify(await editor.evaluate(() => ({
            ready: document.body.dataset.ready,
            state: document.querySelector("#state")?.textContent,
            toast: document.querySelector("#toast")?.textContent,
            buttons: [...document.querySelectorAll("header button")].map(node => ({ id: node.id, disabled: node.disabled, text: node.textContent })),
          })), null, 2) });
          await test.info().attach("large-export-screen", { contentType: "image/png", body: await editor.screenshot() });
          throw error;
        }
        expect(fs.readdirSync(downloadRoot).filter((name) => name.endsWith(extension))).toHaveLength(0);
      }
      // Buttons must be re-enabled after a failed export so PNG/PDF stay available.
      await editor.waitForSelector("#save-png:not([disabled])");
      await editor.waitForSelector("#save-pdf:not([disabled])");
      expect(fs.readdirSync(downloadRoot)).toHaveLength(0);
    } finally {
      removeOwnedDirectory(downloadRoot, "winshot-adversarial-dl-");
    }
    expect(run.errors).toEqual([]);
  } finally {
    await closeExactExtension(run);
  }
});

test("a watermark at the escape boundary must not corrupt the PDF content stream", async () => {
  const run = await launchExactExtension();
  try {
    const page = await run.browser.newPage();
    monitorPage(page, run.errors);
    await page.goto("http://127.0.0.1:4173/body", { waitUntil: "networkidle0" });
    const { editor, record } = await capture(run, page, "visible");
    expect(record, record.error).toMatchObject({ state: "complete" });

    const downloadRoot = await downloadDirectory(editor);
    try {
      // 299 filler characters followed by "(": pdfLiteral escapes the paren to "\("
      // and the exporter then slices the ESCAPED string to 300 chars, leaving a bare
      // trailing backslash that escapes the literal's closing ")" and swallows the
      // rest of the content stream (exporters.js watermark path).
      await setPdfOption(editor, "#pdf-page-size", "auto");
      await setPdfOption(editor, "#pdf-layout", "multipage");
      await setPdfOption(editor, "#pdf-header", "HEADER-OK");
      await setPdfOption(editor, "#pdf-footer", "FOOTER-OK");
      await setPdfOption(editor, "#pdf-watermark", `${"W".repeat(299)}(X`);
      await editor.click("#save-pdf");
      const { bytes } = await waitForFile(downloadRoot, ".pdf");
      const parsed = await inspectPdf(bytes);
      expect(parsed.pages).toBe(1);
      // The truncated watermark must still be drawn as text. pdf.js getTextContent
      // only reports glyphs inside the page bounds, and the 34pt rotated watermark
      // runs off the page after ~20 glyphs — so assert a shorter run. If the escape
      // bug existed, the watermark Tj would be swallowed and NO W run would appear.
      expect(parsed.text).toMatch(/W{10}/);
      // ...and the operators after it must survive (the watermark is emitted after
      // the header/footer lines, but a runaway string corrupts stream parsing).
      expect(parsed.text).toContain("HEADER-OK");
      expect(parsed.text).toContain("FOOTER-OK");
    } finally {
      removeOwnedDirectory(downloadRoot, "winshot-adversarial-dl-");
    }
  } finally {
    await closeExactExtension(run);
  }
});
