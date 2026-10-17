import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { getDocument } from "pdfjs-dist/legacy/build/pdf.mjs";
import { launchCaptureBrowser } from "./launch-browser.mjs";

async function runEditor(verify) {
  const profile = fs.mkdtempSync(
    path.join(os.tmpdir(), "winshot-image-editor-"),
  );
  const browser = await launchCaptureBrowser(profile);
  const errors = [];
  try {
    const id = await browser.installExtension(
      process.env.WINSHOT_EXTENSION_PATH ||
        fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)),
    );
    const extension = (await browser.extensions()).get(id),
      page = await browser.newPage();
    await page.goto("http://127.0.0.1:4173/body");
    await page.evaluate(() => {
      document.title = "Browser image editor QA";
      document.body.innerHTML =
        '<main style="width:100%;height:100vh;background:#fff;color:#111;font:32px sans-serif"><a href="https://example.com/private" style="position:absolute;left:40px;top:45px">SECRET ORIGINAL TEXT</a><div style="position:absolute;left:600px;top:500px;width:200px;height:100px;background:#04c870">Source</div></main>';
    });
    const target = browser.waitForTarget(
      (t) => t.type() === "page" && t.url().includes("/src/editor.html?id="),
      { timeout: 60000 },
    );
    const pt = browser.waitForTarget(
      (t) => t.type() === "page" && t.url().endsWith("/src/popup.html"),
    );
    await extension.triggerAction(page);
    const popup = await (await pt).asPage();
    await popup.waitForSelector("body[data-ready='true']");
    await popup.click("[data-mode='visible']");
    const editor = await (await target).asPage();
    editor.on("pageerror", (e) => errors.push(e.message));
    editor.on("console", (m) => {
      if (m.type() === "error") errors.push(m.text());
    });
    await editor.waitForSelector(".annotation-overlay");
    await editor.bringToFront();
    await popup.close().catch(() => {});
    const record = await stored(editor);
    expect(record.state).toBe("complete");
    await verify({ browser, id, page, editor, record, errors });
    expect(errors).toEqual([]);
  } finally {
    await browser.close();
    fs.rmSync(profile, {
      recursive: true,
      force: true,
      maxRetries: 5,
      retryDelay: 100,
    });
  }
}
async function stored(editor) {
  return editor.evaluate(async () => {
    const s = await import(chrome.runtime.getURL("src/store.js"));
    return s.getRecord(new URL(location.href).searchParams.get("id"));
  });
}
async function drag(editor, tool, x, y, x2, y2) {
  await editor.click(`[data-tool='${tool}']`);
  const box = await editor.$eval(".annotation-overlay", (n) => {
    const b = n.getBoundingClientRect();
    return {
      x: b.x,
      y: b.y,
      width: b.width,
      sourceWidth: Number(n.getAttribute("width")),
    };
  });
  const k = box.width / box.sourceWidth;
  await editor.mouse.move(box.x + x * k, box.y + y * k);
  await editor.mouse.down();
  await editor.mouse.move(box.x + x2 * k, box.y + y2 * k, { steps: 8 });
  await editor.mouse.up();
}
async function saved(editor) {
  await editor.waitForFunction(
    () =>
      document.querySelector("#edit-save-state").textContent ===
      "Saved locally",
  );
}
async function exportsFromDocument(editor) {
  return editor.evaluate(async () => {
    const id = new URL(location.href).searchParams.get("id"),
      s = await import(chrome.runtime.getURL("src/store.js")),
      e = await import(chrome.runtime.getURL("src/image-editor.js")),
      x = await import(chrome.runtime.getURL("src/exporters.js")),
      record = await s.getRecord(id),
      tiles = await s.getTiles(id),
      edited = await e.renderEditedCapture(
        record,
        tiles,
        record.editorDocument,
      );
    const png = await x.exportPng(edited.record, edited.tiles),
      bitmap = await createImageBitmap(png),
      canvas = new OffscreenCanvas(bitmap.width, bitmap.height),
      ctx = canvas.getContext("2d");
    ctx.drawImage(bitmap, 0, 0);
    bitmap.close();
    return {
      size: edited.record.report.dimensionsPx,
      semantics: edited.record.semantics || null,
      pixels: [...ctx.getImageData(100, 80, 1, 1).data],
      pdf: [
        ...new Uint8Array(
          await (await x.exportPdf(edited.record, edited.tiles)).arrayBuffer(),
        ),
      ],
    };
  });
}

test("browser image editor draws every annotation, saves history, and supports move delete undo redo", async () => {
  await runEditor(async ({ editor }) => {
    await drag(editor, "arrow", 80, 140, 270, 180);
    await drag(editor, "rectangle", 80, 230, 260, 330);
    await drag(editor, "ellipse", 340, 230, 500, 330);
    await drag(editor, "pen", 80, 390, 250, 420);
    await editor.click("[data-tool=text]");
    await editor.$eval("#edit-text", (n) => {
      n.value = "Browser only\nEditable text";
      n.dispatchEvent(new Event("change", { bubbles: true }));
    });
    const b = await editor.$eval(".annotation-overlay", (n) => {
      const b = n.getBoundingClientRect();
      return { x: b.x, y: b.y, k: b.width / Number(n.getAttribute("width")) };
    });
    await editor.mouse.click(b.x + 340 * b.k, b.y + 390 * b.k);
    await drag(editor, "redact", 40, 40, 460, 100);
    await saved(editor);
    let document = (await stored(editor)).editorDocument;
    expect(document.objects.map((x) => x.type)).toEqual([
      "arrow",
      "rectangle",
      "ellipse",
      "pen",
      "text",
      "redact",
    ]);
    // Real selection/move then delete; undo and redo preserve object geometry.
    await drag(editor, "select", 140, 270, 180, 290);
    await saved(editor);
    document = (await stored(editor)).editorDocument;
    expect(document.objects[1].x).toBeCloseTo(120, 0);
    expect(document.objects[1].y).toBeCloseTo(250, 0);
    await editor.click("#edit-delete");
    await saved(editor);
    expect((await stored(editor)).editorDocument.objects).toHaveLength(5);
    await editor.click("#edit-undo");
    await saved(editor);
    expect((await stored(editor)).editorDocument.objects).toHaveLength(6);
    await editor.click("#edit-redo");
    await saved(editor);
    expect((await stored(editor)).editorDocument.objects).toHaveLength(5);
    await editor.click("#edit-undo");
    await saved(editor);
    await editor.reload();
    await editor.waitForSelector("[data-object-id]");
    expect(await editor.$$eval("[data-object-id]", (n) => n.length)).toBe(6);
    expect((await stored(editor)).editorDocument.objects[1].x).toBeCloseTo(
      120,
      0,
    );
    if (process.env.WINSHOT_EDITOR_EVIDENCE_DIR) {
      const root = process.env.WINSHOT_EDITOR_EVIDENCE_DIR;
      fs.mkdirSync(root, { recursive: true });
      await editor.waitForFunction(() =>
        [...document.querySelectorAll("#preview img")].every(
          (i) => i.complete && i.naturalWidth,
        ),
      );
      await editor.screenshot({
        path: path.join(root, "browser-image-editor-desktop.png"),
      });
      await editor.setViewport({ width: 390, height: 844 });
      await editor.screenshot({
        path: path.join(root, "browser-image-editor-mobile.png"),
      });
      const mobile = await editor.evaluate(() => ({
        width: innerWidth,
        scroll: document.documentElement.scrollWidth,
        overflow: [...document.querySelectorAll("body *")]
          .map((n) => ({
            tag: n.tagName,
            id: n.id,
            class: n.className?.baseVal || n.className,
            right: n.getBoundingClientRect().right,
            width: n.getBoundingClientRect().width,
          }))
          .filter((n) => n.right > innerWidth + 1 && n.width > 0),
      }));
      expect(mobile.scroll, JSON.stringify(mobile)).toBeLessThanOrEqual(
        mobile.width,
      );
    }
  });
});

test("browser image editor crop and opaque redaction reach PNG JPEG WebP PDF clipboard print", async () => {
  await runEditor(async ({ editor, browser, id }) => {
    await drag(editor, "redact", 20, 20, 520, 120);
    await drag(editor, "crop", 0, 0, 850, 650);
    await editor.click("#crop-apply");
    await saved(editor);
    const result = await exportsFromDocument(editor);
    expect(result.size.width).toBeGreaterThanOrEqual(849);
    expect(result.size.width).toBeLessThanOrEqual(850);
    expect(result.size.height).toBeGreaterThanOrEqual(649);
    expect(result.size.height).toBeLessThanOrEqual(650);
    expect(result.pixels).toEqual([17, 17, 17, 255]);
    expect(result.semantics).toBeNull();
    const task = getDocument({
      data: new Uint8Array(result.pdf),
      verbosity: 0,
      isEvalSupported: false,
    });
    try {
      const pdf = await task.promise;
      for (let i = 1; i <= pdf.numPages; i++) {
        const page = await pdf.getPage(i);
        expect(
          (await page.getTextContent()).items.map((x) => x.str).join(" "),
        ).not.toContain("SECRET");
        expect((await page.getAnnotations()).filter((a) => a.url)).toEqual([]);
      }
    } finally {
      await task.destroy();
    }
    const folder = fs.mkdtempSync(
        path.join(os.tmpdir(), "winshot-image-download-"),
      ),
      cdp = await editor.createCDPSession();
    await cdp.send("Browser.setDownloadBehavior", {
      behavior: "allow",
      downloadPath: folder,
    });
    try {
      for (const [button, suffix, type] of [
        ["save-png", ".png", "image/png"],
        ["save-jpeg", ".jpg", "image/jpeg"],
        ["save-webp", ".webp", "image/webp"],
      ]) {
        await editor.click(`#${button}`);
        await expect
          .poll(() => fs.readdirSync(folder).find((x) => x.endsWith(suffix)))
          .toBeTruthy();
        const name = fs.readdirSync(folder).find((x) => x.endsWith(suffix));
        const bytes = [...fs.readFileSync(path.join(folder, name))];
        const image = await editor.evaluate(
          async ({ bytes, type }) => {
            const b = await createImageBitmap(
                new Blob([new Uint8Array(bytes)], { type }),
              ),
              c = new OffscreenCanvas(b.width, b.height),
              ctx = c.getContext("2d");
            ctx.drawImage(b, 0, 0);
            const p = [...ctx.getImageData(100, 80, 1, 1).data];
            return { width: b.width, height: b.height, p };
          },
          { bytes, type },
        );
        expect(image.width).toBe(result.size.width);
        expect(image.height).toBe(result.size.height);
        expect(image.p[0]).toBeLessThan(25);
        expect(image.p[1]).toBeLessThan(25);
        expect(image.p[2]).toBeLessThan(25);
      }
      await editor.click("#copy-clipboard");
      await editor.waitForFunction(() =>
        /Copied|Copy failed/.test(document.querySelector("#toast").textContent),
      );
      expect(await editor.$eval("#toast", (n) => n.textContent)).toBe(
        "Copied to clipboard.",
      );
      await browser
        .defaultBrowserContext()
        .overridePermissions(`chrome-extension://${id}`, [
          "clipboard-read",
          "clipboard-write",
        ]);
      const clipboard = await editor.evaluate(async () => {
        const e = await navigator.clipboard.read(),
          blob = await e
            .find((i) => i.types.includes("image/png"))
            .getType("image/png"),
          b = await createImageBitmap(blob),
          c = new OffscreenCanvas(b.width, b.height),
          ctx = c.getContext("2d");
        ctx.drawImage(b, 0, 0);
        return {
          width: b.width,
          height: b.height,
          p: [...ctx.getImageData(100, 80, 1, 1).data],
        };
      });
      expect(clipboard).toEqual({ ...result.size, p: [17, 17, 17, 255] });
      await editor.click("#print-capture");
      await editor.waitForFunction(
        () =>
          document.querySelector("#print-image")?.complete &&
          document.querySelector("#print-capture").textContent === "Print",
      );
      expect(
        await editor.$eval("#print-image", (n) => ({
          width: n.naturalWidth,
          height: n.naturalHeight,
        })),
      ).toEqual(result.size);
      await editor.click("#save-project");
      await expect
        .poll(() =>
          fs.readdirSync(folder).find((x) => x.endsWith(".winshot.json")),
        )
        .toBeTruthy();
      const project = JSON.parse(
        fs.readFileSync(
          path.join(
            folder,
            fs.readdirSync(folder).find((x) => x.endsWith(".winshot.json")),
          ),
        ),
      );
      expect(project.editorDocument.objects[0].type).toBe("redact");
      expect(project.width).toBeGreaterThan(result.size.width);
    } finally {
      fs.rmSync(folder, {
        recursive: true,
        force: true,
        maxRetries: 5,
        retryDelay: 100,
      });
    }
    await editor.click("#edit-undo");
    await saved(editor);
    expect((await stored(editor)).editorDocument.crop.width).toBeGreaterThan(
      result.size.width,
    );
  });
});

test("browser image editor renders annotations on captures taller than the single canvas limit without losing source tiles", async () => {
  await runEditor(async ({ editor }) => {
    const result = await editor.evaluate(async () => {
      const { renderEditedCapture } = await import(
          chrome.runtime.getURL("src/image-editor.js")
        ),
        { exportPng } = await import(chrome.runtime.getURL("src/exporters.js"));
      const width = 160,
        height = 19000,
        tiles = [];
      for (let y = 0; y < height; y += 1000) {
        const h = Math.min(1000, height - y),
          c = new OffscreenCanvas(width, h),
          ctx = c.getContext("2d");
        ctx.fillStyle = y % 2000 === 0 ? "#04c870" : "#0088ff";
        ctx.fillRect(0, 0, width, h);
        tiles.push({
          index: tiles.length,
          destXPx: 0,
          destYPx: y,
          widthPx: width,
          heightPx: h,
          blob: await c.convertToBlob({ type: "image/png" }),
        });
      }
      const record = {
        id: "tall-editor-fixture",
        report: {
          dimensionsPx: { width, height },
          dimensionsCss: { width, height },
          tileCount: tiles.length,
        },
        semantics: { text: [{ text: "HIDDEN" }] },
      };
      const document = {
        version: 1,
        crop: { x: 0, y: 0, width, height },
        objects: [
          {
            id: "redact",
            type: "redact",
            x: 0,
            y: 18000,
            width: 160,
            height: 300,
            color: "#111111",
            stroke: 4,
          },
        ],
        past: [],
        future: [],
      };
      const edited = await renderEditedCapture(record, tiles, document),
        blob = await exportPng(edited.record, edited.tiles),
        bytes = new DataView(await blob.slice(0, 24).arrayBuffer());
      const last = edited.tiles.find(
          (t) => t.destYPx <= 18100 && t.destYPx + t.heightPx > 18100,
        ),
        bitmap = await createImageBitmap(last.blob),
        c = new OffscreenCanvas(last.widthPx, last.heightPx),
        ctx = c.getContext("2d");
      ctx.drawImage(bitmap, 0, 0);
      return {
        width: bytes.getUint32(16),
        height: bytes.getUint32(20),
        count: edited.tiles.length,
        maxEdge: Math.max(
          ...edited.tiles.flatMap((t) => [t.widthPx, t.heightPx]),
        ),
        pixel: [...ctx.getImageData(40, 18100 - last.destYPx, 1, 1).data],
        originalCount: tiles.length,
      };
    });
    expect(result).toEqual({
      width: 160,
      height: 19000,
      count: 19,
      maxEdge: 1024,
      pixel: [17, 17, 17, 255],
      originalCount: 19,
    });
  });
});

test("browser image editor keeps memory edits exportable after the local history record is deleted", async () => {
  await runEditor(async ({ editor }) => {
    await editor.evaluate(async () => {
      const s = await import(chrome.runtime.getURL("src/store.js"));
      await s.deleteCapture(new URL(location.href).searchParams.get("id"));
    });
    await drag(editor, "redact", 20, 20, 520, 120);
    await editor.waitForFunction(
      () =>
        document.querySelector("#edit-save-state").textContent === "Not saved",
    );
    const folder = fs.mkdtempSync(
        path.join(os.tmpdir(), "winshot-image-unsaved-"),
      ),
      cdp = await editor.createCDPSession();
    await cdp.send("Browser.setDownloadBehavior", {
      behavior: "allow",
      downloadPath: folder,
    });
    try {
      await editor.click("#save-project");
      await expect
        .poll(() =>
          fs.readdirSync(folder).find((x) => x.endsWith(".winshot.json")),
        )
        .toBeTruthy();
      const project = JSON.parse(
        fs.readFileSync(
          path.join(
            folder,
            fs.readdirSync(folder).find((x) => x.endsWith(".winshot.json")),
          ),
        ),
      );
      expect(project.editorDocument.objects[0].type).toBe("redact");
      await editor.click("#save-png");
      await expect
        .poll(() => fs.readdirSync(folder).find((x) => x.endsWith(".png")))
        .toBeTruthy();
      expect(await stored(editor)).toBeUndefined();
      expect(await editor.$eval("#edit-save-state", (n) => n.textContent)).toBe(
        "Not saved",
      );
    } finally {
      fs.rmSync(folder, {
        recursive: true,
        force: true,
        maxRetries: 5,
        retryDelay: 100,
      });
    }
  });
});

test("browser image editor keeps PNG and WebP transparency before and after edits while JPEG stays white", async () => {
  await runEditor(async ({ editor }) => {
    const result = await editor.evaluate(async () => {
      const { importImage } = await import(
          chrome.runtime.getURL("src/project-file.js")
        ),
        { getTiles } = await import(chrome.runtime.getURL("src/store.js")),
        { renderEditedCapture } = await import(
          chrome.runtime.getURL("src/image-editor.js")
        ),
        x = await import(chrome.runtime.getURL("src/exporters.js"));
      const canvas = new OffscreenCanvas(200, 160),
        ctx = canvas.getContext("2d");
      ctx.fillStyle = "#04c870";
      ctx.fillRect(50, 50, 100, 60);
      const record = await importImage(
          new File(
            [await canvas.convertToBlob({ type: "image/png" })],
            "transparent.png",
            { type: "image/png" },
          ),
        ),
        tiles = await getTiles(record.id);
      async function sample(blob) {
        const b = await createImageBitmap(blob),
          c = new OffscreenCanvas(b.width, b.height),
          cx = c.getContext("2d");
        cx.drawImage(b, 0, 0);
        b.close();
        return [...cx.getImageData(5, 5, 1, 1).data];
      }
      const before = {
        png: await sample(await x.exportPng(record, tiles)),
        webp: await sample(await x.exportWebp(record, tiles)),
        jpeg: await sample(await x.exportJpeg(record, tiles)),
      };
      record.editorDocument.objects.push({
        id: "private",
        type: "redact",
        x: 70,
        y: 60,
        width: 30,
        height: 30,
        color: "#111111",
        stroke: 4,
      });
      const edited = await renderEditedCapture(
        record,
        tiles,
        record.editorDocument,
      );
      return {
        before,
        after: {
          png: await sample(await x.exportPng(edited.record, edited.tiles)),
          webp: await sample(await x.exportWebp(edited.record, edited.tiles)),
          jpeg: await sample(await x.exportJpeg(edited.record, edited.tiles)),
        },
      };
    });
    expect(result.before.png[3]).toBe(0);
    expect(result.before.webp[3]).toBe(0);
    expect(result.after.png[3]).toBe(0);
    expect(result.after.webp[3]).toBe(0);
    expect(result.before.jpeg).toEqual([255, 255, 255, 255]);
    expect(result.after.jpeg).toEqual([255, 255, 255, 255]);
  });
});

test("browser image editor still exports PDF when saving PDF preferences fails", async () => {
  await runEditor(async ({ editor }) => {
    await editor.evaluate(() => {
      chrome.storage.local.set = async () => {
        throw new Error("Injected preference storage failure");
      };
    });
    await editor.$eval("#pdf-header", (n) => {
      n.value = "Session header";
      n.dispatchEvent(new Event("change", { bubbles: true }));
    });
    await editor.waitForFunction(() =>
      document
        .querySelector("#toast")
        .textContent.includes("could not be saved"),
    );
    const folder = fs.mkdtempSync(
        path.join(os.tmpdir(), "winshot-image-pdf-fault-"),
      ),
      cdp = await editor.createCDPSession();
    await cdp.send("Browser.setDownloadBehavior", {
      behavior: "allow",
      downloadPath: folder,
    });
    try {
      await editor.click("#save-pdf");
      await expect
        .poll(() => fs.readdirSync(folder).find((x) => x.endsWith(".pdf")))
        .toBeTruthy();
      const bytes = fs.readFileSync(
        path.join(
          folder,
          fs.readdirSync(folder).find((x) => x.endsWith(".pdf")),
        ),
      );
      const task = getDocument({
        data: new Uint8Array(bytes),
        verbosity: 0,
        isEvalSupported: false,
      });
      try {
        const pdf = await task.promise;
        expect(
          (await (await pdf.getPage(1)).getTextContent()).items
            .map((x) => x.str)
            .join(" "),
        ).toContain("Session header");
      } finally {
        await task.destroy();
      }
    } finally {
      fs.rmSync(folder, {
        recursive: true,
        force: true,
        maxRetries: 5,
        retryDelay: 100,
      });
    }
  });
});

async function beginGesture(editor, tool, x, y, x2, y2) {
  await editor.click(`[data-tool='${tool}']`);
  const box = await editor.$eval(".annotation-overlay", (node) => {
    node.addEventListener(
      "pointerdown",
      (event) => {
        window.qaPointerId = event.pointerId;
      },
      { once: true },
    );
    const rect = node.getBoundingClientRect();
    return {
      x: rect.x,
      y: rect.y,
      scale: rect.width / Number(node.getAttribute("width")),
    };
  });
  await editor.mouse.move(box.x + x * box.scale, box.y + y * box.scale);
  await editor.mouse.down();
  await editor.mouse.move(box.x + x2 * box.scale, box.y + y2 * box.scale, {
    steps: 4,
  });
  expect(
    await editor.$eval(".annotation-overlay", (node) =>
      node.hasPointerCapture(window.qaPointerId),
    ),
  ).toBe(true);
  return box;
}
async function pressEditorCommand(editor, command) {
  if (command === "Undo" || command === "Redo") {
    await editor.keyboard.down("Control");
    await editor.keyboard.press(command === "Undo" ? "z" : "y");
    await editor.keyboard.up("Control");
  } else await editor.keyboard.press(command);
}
for (const command of ["Escape", "Delete", "Undo", "Redo"]) {
  test(`browser image editor ${command} interrupts an active annotation move and releases pointer capture`, async () => {
    await runEditor(async ({ editor, errors }) => {
      await drag(editor, "rectangle", 80, 80, 280, 180);
      await drag(editor, "ellipse", 340, 80, 480, 180);
      await editor.click("#edit-undo");
      await saved(editor);
      const box = await beginGesture(editor, "select", 120, 120, 150, 150);
      await pressEditorCommand(editor, command);
      const captured = await editor.$eval(".annotation-overlay", (node) =>
        node.hasPointerCapture(window.qaPointerId),
      );
      await editor.mouse.move(
        box.x + 200 * box.scale,
        box.y + 160 * box.scale,
        { steps: 3 },
      );
      await editor.mouse.up();
      await saved(editor);
      const doc = (await stored(editor)).editorDocument;
      expect(errors).toEqual([]);
      expect(captured).toBe(false);
      expect(doc.objects.map((o) => o.type)).toEqual(
        command === "Escape"
          ? ["rectangle"]
          : command === "Redo"
            ? ["rectangle", "ellipse"]
            : [],
      );
      if (doc.objects.length) {
        expect(doc.objects[0].x).toBeCloseTo(80, 0);
        expect(doc.objects[0].y).toBeCloseTo(80, 0);
      }
      // A fresh gesture after the interruption still works and saves normally.
      await drag(editor, "arrow", 80, 260, 280, 300);
      await saved(editor);
      expect((await stored(editor)).editorDocument.objects.at(-1).type).toBe(
        "arrow",
      );
    });
  });
}

test("browser image editor undo during a new drawing cancels it without a later pointer-up commit", async () => {
  await runEditor(async ({ editor, errors }) => {
    await drag(editor, "rectangle", 80, 80, 280, 180);
    await saved(editor);
    const box = await beginGesture(editor, "pen", 80, 260, 280, 300);
    await pressEditorCommand(editor, "Undo");
    const captured = await editor.$eval(".annotation-overlay", (node) =>
      node.hasPointerCapture(window.qaPointerId),
    );
    await editor.mouse.move(box.x + 320 * box.scale, box.y + 340 * box.scale, {
      steps: 3,
    });
    await editor.mouse.up();
    await saved(editor);
    expect(errors).toEqual([]);
    expect(captured).toBe(false);
    expect((await stored(editor)).editorDocument.objects).toEqual([]);
    await pressEditorCommand(editor, "Redo");
    await saved(editor);
    expect(
      (await stored(editor)).editorDocument.objects.map((o) => o.type),
    ).toEqual(["rectangle"]);
  });
});

async function importEffectsFixture(
  editor,
  { width = 200, height = 120, checker = false } = {},
) {
  const id = await editor.evaluate(
    async ({ width, height, checker }) => {
      const { importImage } = await import(
        chrome.runtime.getURL("src/project-file.js")
      );
      const canvas = new OffscreenCanvas(width, height),
        ctx = canvas.getContext("2d");
      if (checker) {
        ctx.fillStyle = "#000";
        ctx.fillRect(0, 0, width, height);
        ctx.fillStyle = "#fff";
        for (let x = 0; x < width; x += 8) ctx.fillRect(x, 0, 4, height);
      } else {
        for (const [color, x, y] of [
          ["#ff0000", 0, 0],
          ["#00ff00", width / 2, 0],
          ["#0000ff", 0, height / 2],
          ["#ffff00", width / 2, height / 2],
        ]) {
          ctx.fillStyle = color;
          ctx.fillRect(x, y, width / 2, height / 2);
        }
      }
      const record = await importImage(
        new File(
          [await canvas.convertToBlob({ type: "image/png" })],
          "Effects pixel fixture.png",
          { type: "image/png" },
        ),
      );
      return record.id;
    },
    { width, height, checker },
  );
  await editor.goto(new URL(`editor.html?id=${id}`, editor.url()).href);
  await editor.waitForSelector(".annotation-overlay");
  await editor.waitForFunction(() =>
    [...document.querySelectorAll("#preview img")].every(
      (i) => i.complete && i.naturalWidth,
    ),
  );
}
async function effectPixels(editor, positions) {
  return editor.evaluate(async (positions) => {
    const s = await import(chrome.runtime.getURL("src/store.js")),
      m = await import(chrome.runtime.getURL("src/image-editor.js")),
      e = await import(chrome.runtime.getURL("src/exporters.js")),
      id = new URL(location.href).searchParams.get("id"),
      record = await s.getRecord(id),
      tiles = await s.getTiles(id),
      edited = await m.renderEditedCapture(
        record,
        tiles,
        record.editorDocument,
      ),
      blob = await e.exportPng(edited.record, edited.tiles),
      bitmap = await createImageBitmap(blob),
      canvas = new OffscreenCanvas(bitmap.width, bitmap.height),
      ctx = canvas.getContext("2d");
    ctx.drawImage(bitmap, 0, 0);
    bitmap.close();
    return {
      size: edited.record.report.dimensionsPx,
      pixels: positions.map(([x, y]) => [...ctx.getImageData(x, y, 1, 1).data]),
      semantics: edited.record.semantics ?? null,
      maxTileEdge: Math.max(
        ...edited.tiles.flatMap((t) => [t.widthPx, t.heightPx]),
      ),
    };
  }, positions);
}
async function setEditValue(editor, selector, value, event = "change") {
  await editor.$eval(
    selector,
    (node, { value, event }) => {
      node.value = value;
      node.dispatchEvent(new Event(event, { bubbles: true }));
    },
    { value: String(value), event },
  );
}
async function dragOutput(editor, tool, x, y, x2, y2) {
  await editor.click(`[data-tool='${tool}']`);
  const box = await editor.$eval("#preview", (n) => {
    const r = n.getBoundingClientRect();
    return { x: r.x, y: r.y, width: r.width, height: r.height };
  });
  const doc = (await stored(editor)).editorDocument,
    size = doc.transform || doc.crop;
  await editor.mouse.move(
    box.x + (x * box.width) / size.width,
    box.y + (y * box.height) / size.height,
  );
  await editor.mouse.down();
  await editor.mouse.move(
    box.x + (x2 * box.width) / size.width,
    box.y + (y2 * box.height) / size.height,
    { steps: 6 },
  );
  await editor.mouse.up();
  await saved(editor);
}

test("browser image editor size rotation and flips preserve quadrant pixels undo and editable projects", async () => {
  await runEditor(async ({ editor }) => {
    await importEffectsFixture(editor);
    await setEditValue(editor, "#image-width", 100, "input");
    expect(await editor.$eval("#image-height", (n) => n.value)).toBe("60");
    await editor.click("#image-resize");
    await saved(editor);
    expect(
      await effectPixels(editor, [
        [10, 10],
        [80, 10],
        [10, 50],
        [80, 50],
      ]),
    ).toMatchObject({
      size: { width: 100, height: 60 },
      pixels: [
        [255, 0, 0, 255],
        [0, 255, 0, 255],
        [0, 0, 255, 255],
        [255, 255, 0, 255],
      ],
    });
    await editor.click("#image-rotate-right");
    await saved(editor);
    expect(
      await effectPixels(editor, [
        [10, 10],
        [50, 10],
        [10, 80],
        [50, 80],
      ]),
    ).toMatchObject({
      size: { width: 60, height: 100 },
      pixels: [
        [0, 0, 255, 255],
        [255, 0, 0, 255],
        [255, 255, 0, 255],
        [0, 255, 0, 255],
      ],
    });
    await editor.click("#image-flip-x");
    await saved(editor);
    expect(
      (
        await effectPixels(editor, [
          [10, 10],
          [50, 10],
          [10, 80],
          [50, 80],
        ])
      ).pixels,
    ).toEqual([
      [255, 0, 0, 255],
      [0, 0, 255, 255],
      [0, 255, 0, 255],
      [255, 255, 0, 255],
    ]);
    await editor.click("#image-flip-y");
    await saved(editor);
    const expected = [
      [0, 255, 0, 255],
      [255, 255, 0, 255],
      [255, 0, 0, 255],
      [0, 0, 255, 255],
    ];
    expect(
      (
        await effectPixels(editor, [
          [10, 10],
          [50, 10],
          [10, 80],
          [50, 80],
        ])
      ).pixels,
    ).toEqual(expected);
    await editor.click("#edit-undo");
    await saved(editor);
    expect((await stored(editor)).editorDocument.transform.flipY).toBe(false);
    await editor.click("#edit-redo");
    await saved(editor);
    await editor.reload();
    await editor.waitForSelector(".annotation-overlay");
    expect(
      (
        await effectPixels(editor, [
          [10, 10],
          [50, 10],
          [10, 80],
          [50, 80],
        ])
      ).pixels,
    ).toEqual(expected);
    const roundtrip = await editor.evaluate(async () => {
      const s = await import(chrome.runtime.getURL("src/store.js")),
        p = await import(chrome.runtime.getURL("src/project-file.js")),
        id = new URL(location.href).searchParams.get("id"),
        r = await s.getRecord(id),
        tiles = await s.getTiles(id),
        blob = await p.saveProject(r, tiles),
        restored = await p.importProject(blob);
      return restored.editorDocument.transform;
    });
    expect(roundtrip).toEqual((await stored(editor)).editorDocument.transform);
  });
});

test("browser image editor draws and crops at the displayed location after rotation and nonuniform resize", async () => {
  await runEditor(async ({ editor }) => {
    await importEffectsFixture(editor);
    await editor.click("#image-rotate-right");
    await saved(editor);
    await editor.click("#image-lock-ratio");
    await setEditValue(editor, "#image-width", 240);
    await setEditValue(editor, "#image-height", 200);
    await editor.click("#image-resize");
    await saved(editor);
    await dragOutput(editor, "redact", 20, 20, 80, 70);
    expect(
      (
        await effectPixels(editor, [
          [40, 40],
          [200, 40],
        ])
      ).pixels,
    ).toEqual([
      [17, 17, 17, 255],
      [255, 0, 0, 255],
    ]);
    await dragOutput(editor, "crop", 0, 0, 120, 100);
    await editor.click("#crop-apply");
    await saved(editor);
    expect((await stored(editor)).editorDocument.crop).toMatchObject({
      width: 100,
      height: 60,
    });
    expect((await effectPixels(editor, [[10, 10]])).size).toEqual({
      width: 60,
      height: 100,
    });
    await editor.click("#edit-undo");
    await saved(editor);
    expect((await effectPixels(editor, [[40, 40]])).size).toEqual({
      width: 240,
      height: 200,
    });
  });
});

test("browser image editor global adjustments change real pixels reset cleanly and preserve opaque redactions", async () => {
  await runEditor(async ({ editor }) => {
    await importEffectsFixture(editor);
    await editor.$eval("#image-effects-options", (n) => (n.open = true));
    await setEditValue(editor, "#effect-grayscale", 100);
    await saved(editor);
    const gray = (await effectPixels(editor, [[20, 20]])).pixels[0];
    expect(Math.abs(gray[0] - gray[1])).toBeLessThanOrEqual(1);
    expect(Math.abs(gray[1] - gray[2])).toBeLessThanOrEqual(1);
    expect(gray[0]).toBeGreaterThan(40);
    expect(gray[0]).toBeLessThan(80);
    await editor.click("#effects-reset");
    await saved(editor);
    expect((await effectPixels(editor, [[20, 20]])).pixels[0]).toEqual([
      255, 0, 0, 255,
    ]);
    await setEditValue(editor, "#effect-brightness", 50);
    await saved(editor);
    const half = (await effectPixels(editor, [[20, 20]])).pixels[0];
    expect(half[0]).toBeGreaterThanOrEqual(127);
    expect(half[0]).toBeLessThanOrEqual(128);
    expect(half.slice(1)).toEqual([0, 0, 255]);
    await setEditValue(editor, "#effect-saturation", 0);
    await saved(editor);
    const desaturated = (await effectPixels(editor, [[20, 20]])).pixels[0];
    expect(Math.abs(desaturated[0] - desaturated[1])).toBeLessThanOrEqual(1);
    await editor.click("#effects-reset");
    await setEditValue(editor, "#effect-sepia", 100);
    await saved(editor);
    const sepia = (await effectPixels(editor, [[20, 20]])).pixels[0];
    expect(sepia[0]).toBeGreaterThan(sepia[1]);
    expect(sepia[1]).toBeGreaterThan(sepia[2]);
    await setEditValue(editor, "#effect-contrast", 0);
    await saved(editor);
    await dragOutput(editor, "redact", 10, 10, 50, 50);
    expect(
      (
        await effectPixels(editor, [
          [20, 20],
          [180, 100],
        ])
      ).pixels[0],
    ).toEqual([17, 17, 17, 255]);
    expect(await editor.$eval("#edited-pdf-note", (n) => n.hidden)).toBe(false);
  });
});

test("browser image editor blur softens source pixels without crossing its bounds and stays below secure redaction", async () => {
  await runEditor(async ({ editor }) => {
    await importEffectsFixture(editor, {
      width: 320,
      height: 160,
      checker: true,
    });
    const before = await effectPixels(editor, [
      [100, 80],
      [104, 80],
      [20, 80],
    ]);
    await dragOutput(editor, "blur", 60, 20, 260, 140);
    const blurred = await effectPixels(editor, [
      [100, 80],
      [104, 80],
      [20, 80],
    ]);
    expect(Math.abs(blurred.pixels[0][0] - blurred.pixels[1][0])).toBeLessThan(
      15,
    );
    expect(Math.abs(before.pixels[0][0] - before.pixels[1][0])).toBeGreaterThan(
      240,
    );
    expect(blurred.pixels[2]).toEqual(before.pixels[2]);
    const box = await editor.$eval("#preview", (n) => {
      const r = n.getBoundingClientRect();
      return { x: r.x, y: r.y, width: r.width, height: r.height };
    });
    const screenshot = await editor.screenshot({ clip: box });
    const previewPixels = await editor.evaluate(
      async (bytes) => {
        const bitmap = await createImageBitmap(
            new Blob([new Uint8Array(bytes)], { type: "image/png" }),
          ),
          canvas = new OffscreenCanvas(bitmap.width, bitmap.height),
          ctx = canvas.getContext("2d");
        ctx.drawImage(bitmap, 0, 0);
        return [
          [...ctx.getImageData(100, 80, 1, 1).data],
          [...ctx.getImageData(104, 80, 1, 1).data],
        ];
      },
      [...screenshot],
    );
    expect(Math.abs(previewPixels[0][0] - previewPixels[1][0])).toBeLessThan(
      15,
    );
    expect(previewPixels[0][0]).toBeGreaterThan(90);
    expect(previewPixels[0][0]).toBeLessThan(165);
    expect(await editor.$eval("#edit-help", (n) => n.textContent)).toContain(
      "not secure redaction",
    );
    await dragOutput(editor, "redact", 80, 50, 160, 110);
    await dragOutput(editor, "blur", 70, 40, 170, 120);
    expect((await effectPixels(editor, [[100, 80]])).pixels[0]).toEqual([
      17, 17, 17, 255,
    ]);
    const roundtrip = await editor.evaluate(async () => {
      const s = await import(chrome.runtime.getURL("src/store.js")),
        p = await import(chrome.runtime.getURL("src/project-file.js")),
        id = new URL(location.href).searchParams.get("id"),
        r = await s.getRecord(id),
        tiles = await s.getTiles(id);
      const saved = await p.saveProject(r, tiles),
        imported = await p.importProject(saved);
      return imported.editorDocument.objects;
    });
    expect(
      roundtrip.filter((s) => s.type === "blur").map((s) => s.radius),
    ).toEqual([12, 12]);
    const seam = await editor.evaluate(async () => {
      const m = await import(chrome.runtime.getURL("src/image-editor.js")),
        e = await import(chrome.runtime.getURL("src/exporters.js")),
        width = 2200,
        height = 160,
        c = new OffscreenCanvas(width, height),
        ctx = c.getContext("2d");
      ctx.fillStyle = "#000";
      ctx.fillRect(0, 0, width, height);
      ctx.fillStyle = "#fff";
      for (let x = 0; x < width; x += 8) ctx.fillRect(x, 0, 4, height);
      const record = {
          id: "blur-seam",
          report: {
            dimensionsPx: { width, height },
            dimensionsCss: { width, height },
          },
        },
        tiles = [
          {
            index: 0,
            destXPx: 0,
            destYPx: 0,
            widthPx: width,
            heightPx: height,
            blob: await c.convertToBlob({ type: "image/png" }),
          },
        ],
        doc = {
          version: 1,
          crop: { x: 0, y: 0, width, height },
          objects: [
            {
              id: "seam",
              type: "blur",
              x: 20,
              y: 20,
              width: 2160,
              height: 120,
              radius: 12,
              color: "#111111",
              stroke: 4,
            },
          ],
        },
        out = await m.renderEditedCapture(record, tiles, doc),
        bitmap = await createImageBitmap(
          await e.exportPng(out.record, out.tiles),
        ),
        canvas = new OffscreenCanvas(width, height),
        pixels = canvas.getContext("2d");
      pixels.drawImage(bitmap, 0, 0);
      return Array.from(
        { length: 16 },
        (_, i) => pixels.getImageData(2040 + i, 80, 1, 1).data[0],
      );
    });
    expect(Math.max(...seam) - Math.min(...seam)).toBeLessThan(4);
    expect(Math.min(...seam)).toBeGreaterThan(90);
    expect(Math.max(...seam)).toBeLessThan(165);
    if (process.env.WINSHOT_EDITOR_EVIDENCE_DIR) {
      const root = process.env.WINSHOT_EDITOR_EVIDENCE_DIR;
      fs.mkdirSync(root, { recursive: true });
      await editor.$eval("#image-effects-options", (n) => (n.open = true));
      await setEditValue(editor, "#editor-zoom", 2);
      await editor.screenshot({
        path: path.join(root, "image-effects-desktop.png"),
      });
      await editor.setViewport({ width: 390, height: 844 });
      await setEditValue(editor, "#editor-zoom", "fit");
      await editor.screenshot({
        path: path.join(root, "image-effects-mobile.png"),
      });
      expect(
        await editor.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
      ).toBe(true);
    }
  });
});

test("browser image editor resize effects remain bounded for tall captures and reject oversized output", async () => {
  await runEditor(async ({ editor }) => {
    const result = await editor.evaluate(async () => {
      const { renderEditedCapture } = await import(
        chrome.runtime.getURL("src/image-editor.js")
      );
      const tiles = [],
        width = 160,
        height = 19000;
      for (let y = 0; y < height; y += 1000) {
        const c = new OffscreenCanvas(width, Math.min(1000, height - y)),
          ctx = c.getContext("2d");
        ctx.fillStyle = "#ff0000";
        ctx.fillRect(0, 0, c.width, c.height);
        tiles.push({
          index: tiles.length,
          destXPx: 0,
          destYPx: y,
          widthPx: c.width,
          heightPx: c.height,
          blob: await c.convertToBlob({ type: "image/png" }),
        });
      }
      const r = {
          id: "tall-effects",
          report: {
            dimensionsPx: { width, height },
            dimensionsCss: { width, height },
          },
        },
        doc = {
          version: 1,
          crop: { x: 0, y: 0, width, height },
          objects: [],
          transform: {
            rotation: 90,
            flipX: false,
            flipY: false,
            width: 9500,
            height: 80,
          },
          effects: { grayscale: 100 },
        };
      const out = await renderEditedCapture(r, tiles, doc);
      let error = "";
      try {
        await renderEditedCapture(r, tiles, {
          ...doc,
          transform: { ...doc.transform, width: 200000, height: 200000 },
        });
      } catch (e) {
        error = e.message;
      }
      const { saveProject, importProject } = await import(
          chrome.runtime.getURL("src/project-file.js")
        ),
        imported = await importProject(
          await saveProject({ ...r, state: "complete" }, tiles),
        );
      return {
        previewId: imported.id,
        size: out.record.report.dimensionsPx,
        maxEdge: Math.max(...out.tiles.flatMap((t) => [t.widthPx, t.heightPx])),
        count: out.tiles.length,
        error,
      };
    });
    expect(result.size).toEqual({ width: 9500, height: 80 });
    expect(result.maxEdge).toBeLessThanOrEqual(2048);
    expect(result.count).toBe(5);
    expect(result.error).toContain("100 million pixel");
    const original = (await stored(editor)).editorDocument;
    await setEditValue(editor, "#image-width", 200000);
    await setEditValue(editor, "#image-height", 200000);
    await editor.click("#image-resize");
    expect(await editor.$eval("#toast", (n) => n.textContent)).toContain(
      "100 million",
    );
    expect((await stored(editor)).editorDocument).toEqual(original);
    // Grouped color filters must still paint the visible viewport at both ends
    // of a tall tiled image, without a whole-page canvas or blank layer limit.
    await editor.goto(
      new URL(`editor.html?id=${result.previewId}`, editor.url()).href,
    );
    await editor.waitForSelector(".annotation-overlay");
    await editor.waitForFunction(() =>
      [...document.querySelectorAll("#preview img")].every(
        (i) => i.complete && i.naturalWidth,
      ),
    );
    await setEditValue(editor, "#effect-brightness", 50);
    await saved(editor);
    const previewSamples = [];
    for (const end of ["top", "bottom"]) {
      const clip = await editor.evaluate(async (end) => {
        const stage =
          document.querySelector("#stage") || document.querySelector(".stage");
        stage.scrollTop = end === "bottom" ? stage.scrollHeight : 0;
        await new Promise((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(resolve)),
        );
        const p = document.querySelector("#preview").getBoundingClientRect(),
          s = stage.getBoundingClientRect();
        return {
          x: p.x + 40,
          y:
            end === "bottom"
              ? Math.min(p.bottom, s.bottom) - 50
              : Math.max(p.top, s.top) + 20,
          width: 20,
          height: 20,
        };
      }, end);
      const png = await editor.screenshot({ type: "png", clip });
      previewSamples.push(
        await editor.evaluate(
          async (bytes) => {
            const b = await createImageBitmap(
                new Blob([new Uint8Array(bytes)], { type: "image/png" }),
              ),
              c = new OffscreenCanvas(20, 20),
              ctx = c.getContext("2d");
            ctx.drawImage(b, 0, 0);
            b.close();
            return [...ctx.getImageData(10, 10, 1, 1).data];
          },
          [...png],
        ),
      );
    }
    for (const pixel of previewSamples) {
      expect(pixel[0]).toBeGreaterThanOrEqual(126);
      expect(pixel[0]).toBeLessThanOrEqual(129);
      expect(pixel.slice(1)).toEqual([0, 0, 255]);
    }
    if (process.env.WINSHOT_EDITOR_EVIDENCE_DIR)
      fs.writeFileSync(
        path.join(
          process.env.WINSHOT_EDITOR_EVIDENCE_DIR,
          "tall-filter-preview.json",
        ),
        JSON.stringify(previewSamples, null, 2),
      );
  });
});

test("browser image editor resampling matches a monolithic canvas across original and output tile seams", async () => {
  await runEditor(async ({ editor }) => {
    const fixture = await editor.evaluate(async () => {
      const width = 2600,
        height = 600,
        canvas = new OffscreenCanvas(width, height),
        ctx = canvas.getContext("2d"),
        gradient = ctx.createLinearGradient(0, 0, width, height);
      gradient.addColorStop(0, "#123456");
      gradient.addColorStop(0.5, "#cdba90");
      gradient.addColorStop(1, "#ffeedd");
      ctx.fillStyle = gradient;
      ctx.fillRect(0, 0, width, height);
      for (const [x, color] of [
        [980, "#f01030"],
        [1001, "#083ed0"],
        [1780, "#ffffff"],
        [1800, "#000000"],
      ]) {
        ctx.fillStyle = color;
        ctx.fillRect(x, 0, x === 980 || x === 1780 ? 21 : 22, height);
      }
      const png = await canvas.convertToBlob({ type: "image/png" }),
        tiles = [];
      function base64(bytes) {
        let text = "";
        for (let i = 0; i < bytes.length; i += 32768)
          text += String.fromCharCode(...bytes.subarray(i, i + 32768));
        return btoa(text);
      }
      for (const [left, right] of [
        [0, 1001],
        [1001, 1800],
        [1800, width],
      ]) {
        const c = new OffscreenCanvas(right - left, height);
        c.getContext("2d").drawImage(canvas, -left, 0);
        tiles.push({
          index: tiles.length,
          destXPx: left,
          destYPx: 0,
          widthPx: c.width,
          heightPx: height,
          png: base64(
            new Uint8Array(
              await (
                await c.convertToBlob({ type: "image/png" })
              ).arrayBuffer(),
            ),
          ),
        });
      }
      const { importProject } = await import(
          chrome.runtime.getURL("src/project-file.js")
        ),
        record = await importProject(
          new Blob(
            [
              JSON.stringify({
                format: "winshot-browser-project",
                version: 1,
                title: "Resampling oracle",
                fileName: "resampling-oracle",
                width,
                height,
                editorDocument: {
                  version: 1,
                  crop: { x: 0, y: 0, width, height },
                  objects: [],
                },
                tiles,
              }),
            ],
            { type: "application/json" },
          ),
        );
      return {
        id: record.id,
        png: [...new Uint8Array(await png.arrayBuffer())],
      };
    });
    await editor.goto(
      new URL(`editor.html?id=${fixture.id}`, editor.url()).href,
    );
    await editor.waitForSelector(".annotation-overlay");
    await setEditValue(editor, "#effect-brightness", 90);
    await setEditValue(editor, "#effect-contrast", 110);
    await setEditValue(editor, "#effect-saturation", 85);
    await setEditValue(editor, "#effect-grayscale", 15);
    await setEditValue(editor, "#effect-sepia", 5);
    await saved(editor);
    const results = [];
    for (const scale of [0.5, 1.5]) {
      await setEditValue(editor, "#image-width", 2600 * scale, "input");
      await editor.click("#image-resize");
      await saved(editor);
      results.push(
        await editor.evaluate(
          async ({ png, scale }) => {
            const s = await import(chrome.runtime.getURL("src/store.js")),
              m = await import(chrome.runtime.getURL("src/image-editor.js")),
              e = await import(chrome.runtime.getURL("src/exporters.js")),
              r = await s.getRecord(
                new URL(location.href).searchParams.get("id"),
              ),
              tiles = await s.getTiles(r.id),
              edited = await m.renderEditedCapture(r, tiles, r.editorDocument),
              actualBitmap = await createImageBitmap(
                await e.exportPng(edited.record, edited.tiles),
              ),
              original = await createImageBitmap(
                new Blob([new Uint8Array(png)], { type: "image/png" }),
              ),
              width = 2600 * scale,
              height = 600 * scale;
            const actualCanvas = new OffscreenCanvas(width, height),
              actual = actualCanvas.getContext("2d"),
              oracleCanvas = new OffscreenCanvas(width, height),
              oracle = oracleCanvas.getContext("2d");
            actual.drawImage(actualBitmap, 0, 0);
            actualBitmap.close();
            // Independent oracle: resample one full source image once, rather than
            // rebuilding editor tiles or calling its transform/filter helpers.
            oracle.imageSmoothingQuality = "high";
            oracle.filter =
              "brightness(90%) contrast(110%) saturate(85%) grayscale(15%) sepia(5%)";
            oracle.scale(scale, scale);
            oracle.drawImage(original, 0, 0);
            original.close();
            const boundaries = [
                1001 * scale,
                1800 * scale,
                ...(width > 2048 ? [2048] : []),
              ],
              checks = [];
            for (const boundary of boundaries) {
              let maximum = 0,
                alpha = 0,
                example;
              for (
                let x = Math.floor(boundary) - 4;
                x <= Math.ceil(boundary) + 4;
                x++
              )
                for (let y = 10; y < height - 10; y += 17) {
                  const a = [...actual.getImageData(x, y, 1, 1).data],
                    o = [...oracle.getImageData(x, y, 1, 1).data],
                    difference = Math.max(
                      ...a.map((v, i) => Math.abs(v - o[i])),
                    );
                  alpha = Math.max(alpha, Math.abs(a[3] - o[3]));
                  if (difference > maximum) {
                    maximum = difference;
                    example = { x, y, actual: a, expected: o };
                  }
                }
              checks.push({ boundary, maximum, alpha, example });
            }
            return { scale, width, height, checks };
          },
          { png: fixture.png, scale },
        ),
      );
    }
    if (process.env.WINSHOT_EDITOR_EVIDENCE_DIR) {
      fs.mkdirSync(process.env.WINSHOT_EDITOR_EVIDENCE_DIR, {
        recursive: true,
      });
      fs.writeFileSync(
        path.join(
          process.env.WINSHOT_EDITOR_EVIDENCE_DIR,
          "resampling-seams.json",
        ),
        JSON.stringify(results, null, 2),
      );
    }
    for (const result of results)
      for (const check of result.checks)
        expect(check.maximum, JSON.stringify(result)).toBeLessThanOrEqual(2);
  });
});

test("browser image editor resamples rotated alpha and severe reductions within bounded native patches", async () => {
  await runEditor(async ({ editor }) => {
    async function fixture(width, height, uniform) {
      const result = await editor.evaluate(
        async ({ width, height, uniform }) => {
          const canvas = new OffscreenCanvas(width, height),
            ctx = canvas.getContext("2d");
          ctx.fillStyle = "rgba(32,160,224,.5)";
          ctx.fillRect(0, 0, width, height);
          if (!uniform) {
            ctx.clearRect(450, 260, 100, 100);
            ctx.fillStyle = "rgba(230,20,70,.75)";
            ctx.fillRect(501, 0, width - 501, height);
          }
          const png = await canvas.convertToBlob({ type: "image/png" }),
            tiles = [];
          function b64(bytes) {
            let s = "";
            for (let i = 0; i < bytes.length; i += 32768)
              s += String.fromCharCode(...bytes.subarray(i, i + 32768));
            return btoa(s);
          }
          const xs = uniform ? [0, 2001, width] : [0, 501, width],
            ys = uniform ? [0, height] : [0, 301, height];
          for (let row = 0; row < ys.length - 1; row++)
            for (let col = 0; col < xs.length - 1; col++) {
              const c = new OffscreenCanvas(
                xs[col + 1] - xs[col],
                ys[row + 1] - ys[row],
              );
              c.getContext("2d").drawImage(canvas, -xs[col], -ys[row]);
              tiles.push({
                index: tiles.length,
                destXPx: xs[col],
                destYPx: ys[row],
                widthPx: c.width,
                heightPx: c.height,
                png: b64(
                  new Uint8Array(
                    await (
                      await c.convertToBlob({ type: "image/png" })
                    ).arrayBuffer(),
                  ),
                ),
              });
            }
          const { importProject } = await import(
              chrome.runtime.getURL("src/project-file.js")
            ),
            r = await importProject(
              new Blob(
                [
                  JSON.stringify({
                    format: "winshot-browser-project",
                    version: 1,
                    title: "Alpha resampling oracle",
                    width,
                    height,
                    editorDocument: {
                      version: 1,
                      crop: { x: 0, y: 0, width, height },
                      objects: [],
                    },
                    tiles,
                  }),
                ],
                { type: "application/json" },
              ),
            );
          return {
            id: r.id,
            png: [...new Uint8Array(await png.arrayBuffer())],
          };
        },
        { width, height, uniform },
      );
      await editor.goto(
        new URL(`editor.html?id=${result.id}`, editor.url()).href,
      );
      await editor.waitForSelector(".annotation-overlay");
      return result;
    }
    async function compare(png, rotated) {
      return editor.evaluate(
        async ({ png, rotated }) => {
          const s = await import(chrome.runtime.getURL("src/store.js")),
            m = await import(chrome.runtime.getURL("src/image-editor.js")),
            e = await import(chrome.runtime.getURL("src/exporters.js")),
            r = await s.getRecord(
              new URL(location.href).searchParams.get("id"),
            ),
            tiles = await s.getTiles(r.id),
            allocations = [],
            NativeCanvas = OffscreenCanvas;
          let edited;
          // Allocation instrumentation keeps every operation native and restores the
          // constructor before creating the independent monolithic oracle below.
          window.OffscreenCanvas = new Proxy(NativeCanvas, {
            construct(target, args) {
              allocations.push(args);
              return Reflect.construct(target, args);
            },
          });
          try {
            edited = await m.renderEditedCapture(r, tiles, r.editorDocument);
          } finally {
            window.OffscreenCanvas = NativeCanvas;
          }
          const { width, height } = edited.record.report.dimensionsPx,
            actualCanvas = new OffscreenCanvas(width, height),
            actual = actualCanvas.getContext("2d"),
            oracleCanvas = new OffscreenCanvas(width, height),
            oracle = oracleCanvas.getContext("2d"),
            a = await createImageBitmap(
              await e.exportPng(edited.record, edited.tiles),
            ),
            original = await createImageBitmap(
              new Blob([new Uint8Array(png)], { type: "image/png" }),
            );
          actual.drawImage(a, 0, 0);
          a.close();
          oracle.imageSmoothingQuality = "high";
          if (rotated) {
            oracle.translate(width, 0);
            oracle.scale(-1, 1);
            oracle.scale(width / 601, height / 1001);
            oracle.translate(601, 0);
            oracle.rotate(Math.PI / 2);
          } else oracle.scale(width / 3901, height / 997);
          oracle.drawImage(original, 0, 0);
          original.close();
          const points = rotated ? [] : [[0, 0]];
          if (rotated) {
            const bx = (301 * width) / 601,
              by = (501 * height) / 1001;
            for (let x = Math.floor(bx) - 4; x <= Math.ceil(bx) + 4; x++)
              for (let y = 10; y < height - 10; y += 19) points.push([x, y]);
            for (let y = Math.floor(by) - 4; y <= Math.ceil(by) + 4; y++)
              for (let x = 10; x < width - 10; x += 19) points.push([x, y]);
          }
          let maximum = 0,
            alpha = 0,
            example;
          for (const [x, y] of points) {
            const a = [...actual.getImageData(x, y, 1, 1).data],
              o = [...oracle.getImageData(x, y, 1, 1).data],
              difference = Math.max(
                Math.abs(a[3] - o[3]),
                ...a
                  .slice(0, 3)
                  .map((v, i) =>
                    Math.abs((v * a[3]) / 255 - (o[i] * o[3]) / 255),
                  ),
              );
            alpha = Math.max(alpha, Math.abs(a[3] - o[3]));
            if (difference > maximum) {
              maximum = difference;
              example = { x, y, actual: a, expected: o };
            }
          }
          return {
            width,
            height,
            maximum,
            alpha,
            example,
            allocations,
            maxPixels: Math.max(...allocations.map(([w, h]) => w * h)),
          };
        },
        { png, rotated },
      );
    }
    const alphaFixture = await fixture(1001, 601, false);
    await editor.click("#image-rotate-right");
    await editor.click("#image-flip-x");
    await setEditValue(editor, "#image-width", 301, "input");
    await editor.click("#image-resize");
    await saved(editor);
    const rotated = await compare(alphaFixture.png, true);
    expect(rotated.maximum, JSON.stringify(rotated)).toBeLessThanOrEqual(2);
    expect(rotated.alpha, JSON.stringify(rotated)).toBeLessThanOrEqual(2);
    expect(rotated).toMatchObject({ width: 301, height: 501 });
    const severeFixture = await fixture(3901, 997, true);
    await setEditValue(editor, "#image-width", 1, "input");
    await editor.click("#image-resize");
    await saved(editor);
    const severe = await compare(severeFixture.png, false);
    expect(severe).toMatchObject({ width: 1, height: 1 });
    expect(severe.maximum, JSON.stringify(severe)).toBeLessThanOrEqual(2);
    expect(severe.alpha).toBeLessThanOrEqual(2);
    for (const result of [rotated, severe]) {
      expect(result.allocations.length).toBeGreaterThan(0);
      expect(
        result.allocations.every(([w, h]) => w <= 2048 && h <= 1024),
        JSON.stringify(result),
      ).toBe(true);
      expect(result.maxPixels).toBeLessThanOrEqual(2048 * 1024);
    }
    if (process.env.WINSHOT_EDITOR_EVIDENCE_DIR) {
      fs.mkdirSync(process.env.WINSHOT_EDITOR_EVIDENCE_DIR, {
        recursive: true,
      });
      fs.writeFileSync(
        path.join(
          process.env.WINSHOT_EDITOR_EVIDENCE_DIR,
          "resampling-alpha-extreme.json",
        ),
        JSON.stringify({ rotated, severe }, null, 2),
      );
    }
  });
});

test("browser image editor adjusts overlapping transparent source tiles after composition in preview and exports", async () => {
  await runEditor(async ({ editor }) => {
    const fixture = await editor.evaluate(async () => {
      const tiles = [],
        source = new OffscreenCanvas(100, 100),
        ctx = source.getContext("2d");
      for (const color of ["rgba(0,0,0,.5)", "rgba(255,255,255,.5)"]) {
        const tile = new OffscreenCanvas(100, 100),
          tc = tile.getContext("2d");
        tc.fillStyle = color;
        tc.fillRect(0, 0, 100, 100);
        ctx.drawImage(tile, 0, 0);
        const bytes = new Uint8Array(
          await (await tile.convertToBlob({ type: "image/png" })).arrayBuffer(),
        );
        tiles.push({
          index: tiles.length,
          destXPx: 0,
          destYPx: 0,
          widthPx: 100,
          heightPx: 100,
          png: btoa(String.fromCharCode(...bytes)),
        });
      }
      const { importProject } = await import(
        chrome.runtime.getURL("src/project-file.js")
      );
      const record = await importProject(
        new Blob(
          [
            JSON.stringify({
              format: "winshot-browser-project",
              version: 1,
              title: "Transparent overlap",
              fileName: "transparent-overlap",
              width: 100,
              height: 100,
              editorDocument: {
                version: 1,
                crop: { x: 0, y: 0, width: 100, height: 100 },
                objects: [],
              },
              tiles,
            }),
          ],
          { type: "application/json" },
        ),
      );
      return {
        id: record.id,
        png: [
          ...new Uint8Array(
            await (
              await source.convertToBlob({ type: "image/png" })
            ).arrayBuffer(),
          ),
        ],
      };
    });
    await editor.goto(
      new URL(`editor.html?id=${fixture.id}`, editor.url()).href,
    );
    await editor.waitForSelector(".annotation-overlay");
    await setEditValue(editor, "#effect-contrast", 200);
    await saved(editor);
    await editor.waitForFunction(() =>
      [...document.querySelectorAll("#preview img")].every(
        (i) => i.complete && i.naturalWidth,
      ),
    );
    const clip = await editor.$eval("#preview", (n) => {
      const b = n.getBoundingClientRect();
      return { x: b.x + 20, y: b.y + 20, width: 20, height: 20 };
    });
    const preview = [...(await editor.screenshot({ type: "png", clip }))];
    const results = [];
    for (const width of [100, 150]) {
      if (width === 150) {
        await setEditValue(editor, "#image-width", width, "input");
        await editor.click("#image-resize");
        await saved(editor);
      }
      results.push(
        await editor.evaluate(
          async ({ png, preview, width }) => {
            const s = await import(chrome.runtime.getURL("src/store.js")),
              m = await import(chrome.runtime.getURL("src/image-editor.js")),
              e = await import(chrome.runtime.getURL("src/exporters.js"));
            const r = await s.getRecord(
                new URL(location.href).searchParams.get("id"),
              ),
              edited = await m.renderEditedCapture(
                r,
                await s.getTiles(r.id),
                r.editorDocument,
              );
            async function bitmap(bytes) {
              return createImageBitmap(
                new Blob([new Uint8Array(bytes)], { type: "image/png" }),
              );
            }
            const original = await bitmap(png),
              oracle = new OffscreenCanvas(width, width),
              o = oracle.getContext("2d"),
              actual = new OffscreenCanvas(width, width),
              a = actual.getContext("2d");
            o.filter = "contrast(200%)";
            o.drawImage(original, 0, 0, width, width);
            original.close();
            const rendered = await createImageBitmap(
              await e.exportPng(edited.record, edited.tiles),
            );
            a.drawImage(rendered, 0, 0);
            rendered.close();
            const expected = [...o.getImageData(20, 20, 1, 1).data],
              pixel = [...a.getImageData(20, 20, 1, 1).data];
            const screen = await bitmap(preview),
              pc = new OffscreenCanvas(20, 20),
              p = pc.getContext("2d");
            p.drawImage(screen, 0, 0);
            screen.close();
            const screenPixel = [...p.getImageData(10, 10, 1, 1).data];
            return {
              width,
              pixel,
              expected,
              screenPixel,
              screenExpected: expected
                .slice(0, 3)
                .map((v) =>
                  Math.round((v * expected[3]) / 255 + 255 - expected[3]),
                ),
            };
          },
          { png: fixture.png, preview, width },
        ),
      );
    }
    if (process.env.WINSHOT_EDITOR_EVIDENCE_DIR)
      fs.writeFileSync(
        path.join(
          process.env.WINSHOT_EDITOR_EVIDENCE_DIR,
          "overlap-alpha-effects.json",
        ),
        JSON.stringify(results, null, 2),
      );
    for (const result of results) {
      expect(
        Math.max(
          ...result.pixel.map((v, i) => Math.abs(v - result.expected[i])),
        ),
        JSON.stringify(result),
      ).toBeLessThanOrEqual(2);
      expect(
        Math.max(
          ...result.screenExpected.map((v, i) =>
            Math.abs(v - result.screenPixel[i]),
          ),
        ),
        JSON.stringify(result),
      ).toBeLessThanOrEqual(2);
    }
  });
});
