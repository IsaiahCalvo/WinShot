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
