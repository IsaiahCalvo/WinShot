import { getRecord, getTiles, updateRecord } from "./store.js";
import { exportJpeg, exportPdf, exportPng, exportWebp, exportBmp, exportGif } from "./exporters.js";
import { loadSettings, saveSettings, normalizeSettings } from "./settings.js";
import { ImageEditor, renderEditedCapture } from "./image-editor.js";
import { saveProject, saveEditablePng } from "./project-file.js";
import { initializeSharing } from "./sharing.js";

const params = new URLSearchParams(location.search);
const id = params.get("id");
const title = document.querySelector("#title");
const state = document.querySelector("#state");
const report = document.querySelector("#report");
const warnings = document.querySelector("#warnings");
const preview = document.querySelector("#preview");
const toast = document.querySelector("#toast");
let formatAnchor = document.querySelector("#save-webp");
for (const [id, text, tip] of [["save-bmp", "Save BMP", "Full-color still image; transparent areas become white"], ["save-gif", "Save GIF", "Still image with up to 256 colors; transparent areas become white"], ["save-editable-png", "Editable PNG", "Includes the ORIGINAL image and editable objects. Use Save PNG for a flattened image to share."]]) {
  if (!document.getElementById(id)) {
    const button = document.createElement("button"); button.id = id; button.textContent = text; button.title = tip; button.disabled = true;
    formatAnchor.after(button); formatAnchor = button;
  }
}
const buttons = [...document.querySelectorAll("header button")];
let record;
let tiles = [];
let imageEditor;
let settingsUnavailable = false;
let settings = await loadSettings().catch(() => {
  settingsUnavailable = true;
  return normalizeSettings();
});
const objectUrls = [];

function showToast(message) {
  toast.textContent = message;
  toast.classList.add("show");
  clearTimeout(showToast.timer);
  showToast.timer = setTimeout(() => toast.classList.remove("show"), 3500);
}

function addReport(label, value) {
  const dt = document.createElement("dt");
  const dd = document.createElement("dd");
  dt.textContent = label;
  dd.textContent = value;
  report.append(dt, dd);
}

function renderRecord() {
  title.textContent = record.sourceTitle || "WinShot capture";
  state.textContent = record.state;
  state.className = `pill ${record.state}`;
  const css = record.report.dimensionsCss;
  const px = record.report.dimensionsPx;
  addReport("Mode", record.report.mode);
  addReport("Target", record.report.target);
  if (css)
    addReport(
      "CSS size",
      `${Math.round(css.width)} × ${Math.round(css.height)}`,
    );
  if (px) addReport("Pixel size", `${px.width} × ${px.height}`);
  addReport("Tiles", String(record.report.tileCount));
  if (record.report.seamConfidence) {
    addReport(
      "Seam confidence",
      `${Math.round(record.report.seamConfidence.minimum * 100)}% minimum`,
    );
  }
  if (record.report.coverage)
    addReport(
      "Coverage",
      `${(record.report.coverage.ratio * 100).toFixed(2)}%`,
    );
  addReport("Page restored", record.report.restoration);
  if (record.report.fallbackUsed)
    addReport("Fallback", record.report.fallbackUsed);

  const items = [...(record.report.warnings || [])];
  if (record.error && !items.includes(record.error)) items.push(record.error);
  if (items.length) {
    warnings.hidden = false;
    const list = warnings.querySelector("ul");
    for (const item of [...new Set(items)]) {
      const li = document.createElement("li");
      li.textContent = item;
      list.append(li);
    }
  }

  if (!px || !tiles.length) {
    document
      .querySelectorAll(
        "button,.editor-bar input,.editor-bar select,.editor-context input,.editor-context textarea",
      )
      .forEach((button) => {
        button.disabled = true;
      });
    preview.textContent = "No verified pixels are available for export.";
    preview.style.padding = "30px";
    return;
  }

  imageEditor = new ImageEditor({
    preview,
    stage: document.querySelector(".stage"),
    record,
    tiles,
    notify: showToast,
    persist: async (editorDocument) => {
      const current = await getRecord(id);
      if (!current || !(await updateRecord({ ...current, editorDocument })))
        throw new Error(
          "This capture was removed from history. Save a project to keep your edits.",
        );
      record.editorDocument = editorDocument;
    },
  });
  initializeSharing({ getPayload: async () => {
    const input = await imageEditor.exportInput();
    return { blob: await exportPng(input.record, input.tiles), title: record.sourceTitle || "WinShot capture", fileName: `${record.fileName || "winshot-capture"}.png`, sourceUrl: record.sourceUrl || "" };
  }, notify: showToast });
}

function download(blob, extension) {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `${record.fileName || "winshot-capture"}.${extension}`;
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}

async function runExport(button, label, extension, exporter) {
  buttons.forEach((item) => {
    item.disabled = true;
  });
  const original = button.textContent;
  try {
    button.textContent = `${label} 0%`;
    const input = await imageEditor.exportInput((progress) => {
      button.textContent = `Applying edits ${Math.round(progress * 100)}%`;
    });
    const blob = await exporter(input.record, input.tiles, (progress) => {
      button.textContent = `${label} ${Math.round(progress * 100)}%`;
    });
    download(blob, extension);
    showToast(`${label} ready (${(blob.size / 1024 / 1024).toFixed(1)} MB).`);
  } catch (error) {
    showToast(error.message);
  } finally {
    button.textContent = original;
    buttons.forEach((item) => {
      item.disabled = false;
    });
  }
}

document
  .querySelector("#save-png")
  .addEventListener("click", (event) =>
    runExport(event.currentTarget, "PNG", "png", exportPng),
  );
document
  .querySelector("#save-jpeg")
  .addEventListener("click", (event) =>
    runExport(event.currentTarget, "JPEG", "jpg", exportJpeg),
  );
document
  .querySelector("#save-webp")
  .addEventListener("click", (event) =>
    runExport(event.currentTarget, "WebP", "webp", exportWebp),
  );
document.querySelector("#save-bmp").addEventListener("click", event => runExport(event.currentTarget, "BMP", "bmp", exportBmp));
document.querySelector("#save-gif").addEventListener("click", event => runExport(event.currentTarget, "GIF", "gif", exportGif));
document.querySelector("#save-editable-png").addEventListener("click", async event => {
  const button = event.currentTarget, original = button.textContent;
  buttons.forEach(item => item.disabled = true);
  try {
    const document = structuredClone(imageEditor.doc), revision = imageEditor.saveRevision;
    await imageEditor.saveChain.catch(() => {});
    button.textContent = "Saving editable PNG…";
    const input = await renderEditedCapture(record, tiles, document);
    const flattened = await exportPng(input.record, input.tiles);
    download(await saveEditablePng(flattened, { ...record, editorDocument: document }, tiles), "editable.png");
    imageEditor.projectRevision = revision;
    showToast("Editable PNG saved. It includes your original image; use Save PNG for a flattened image to share.");
  } catch (error) { showToast(`Editable PNG could not be saved: ${error.message}`); }
  finally { button.textContent = original; buttons.forEach(item => item.disabled = false); }
});

document
  .querySelector("#copy-clipboard")
  .addEventListener("click", async (event) => {
    const button = event.currentTarget;
    const original = button.textContent;
    try {
      button.textContent = "Copying…";
      // ClipboardItem accepts a Promise<Blob>, which also keeps the document "focused enough"
      // for the clipboard permission while the PNG is still being encoded.
      const item = new ClipboardItem({
        "image/png": imageEditor
          .exportInput()
          .then((input) => exportPng(input.record, input.tiles)),
      });
      await navigator.clipboard.write([item]);
      showToast("Copied to clipboard.");
    } catch (error) {
      showToast(
        `Copy failed: ${error.message}. Very large captures can exceed the clipboard limit — use Save PNG instead.`,
      );
    } finally {
      button.textContent = original;
    }
  });

document
  .querySelector("#print-capture")
  .addEventListener("click", async (event) => {
    const button = event.currentTarget;
    const original = button.textContent;
    try {
      // Print the assembled image, not the positioned tiles — raw tiles overlap by
      // design and would print duplicated bands. Print CSS shows only #print-image.
      button.textContent = "Preparing…";
      const input = await imageEditor.exportInput();
      const blob = await exportPng(input.record, input.tiles, (progress) => {
        button.textContent = `Preparing ${Math.round(progress * 100)}%`;
      });
      const url = URL.createObjectURL(blob);
      objectUrls.push(url);
      let image = document.querySelector("#print-image");
      if (!image) {
        image = document.createElement("img");
        image.id = "print-image";
        document.body.append(image);
      }
      image.src = url;
      await new Promise((resolve, reject) => {
        image.onload = resolve;
        image.onerror = () =>
          reject(new Error("The print image failed to load."));
      });
      window.print();
    } catch (error) {
      showToast(`Print failed: ${error.message}`);
    } finally {
      button.textContent = original;
    }
  });

const pdfNodes = {
  layout: document.querySelector("#pdf-layout"),
  pageSize: document.querySelector("#pdf-page-size"),
  customWidthIn: document.querySelector("#pdf-width"),
  customHeightIn: document.querySelector("#pdf-height"),
  header: document.querySelector("#pdf-header"),
  footer: document.querySelector("#pdf-footer"),
  watermark: document.querySelector("#pdf-watermark"),
};

function showPdfSettings() {
  for (const [key, node] of Object.entries(pdfNodes))
    node.value = settings.pdf[key];
}

function readPdfSettings() {
  return {
    layout: pdfNodes.layout.value,
    pageSize: pdfNodes.pageSize.value,
    customWidthIn: Number(pdfNodes.customWidthIn.value),
    customHeightIn: Number(pdfNodes.customHeightIn.value),
    header: pdfNodes.header.value,
    footer: pdfNodes.footer.value,
    watermark: pdfNodes.watermark.value,
  };
}

showPdfSettings();
async function persistPdfSettings() {
  settings = normalizeSettings({ ...settings, pdf: readPdfSettings() });
  if (settingsUnavailable) {
    showToast("PDF options apply only for this tab because saved settings could not be read.");
    showPdfSettings();
    return;
  }
  try {
    settings = await saveSettings(settings);
  } catch (error) {
    showToast(
      `PDF options apply for this session, but could not be saved: ${error.message}`,
    );
  }
  showPdfSettings();
}
for (const node of Object.values(pdfNodes))
  node.addEventListener("change", persistPdfSettings);
document.querySelector("#save-pdf").addEventListener("click", async (event) => {
  const button = event.currentTarget;
  await persistPdfSettings();
  await runExport(button, "PDF", "pdf", (capture, captureTiles, onProgress) =>
    exportPdf(capture, captureTiles, onProgress, settings.pdf),
  );
});

document
  .querySelector("#save-project")
  .addEventListener("click", async (event) => {
    const button = event.currentTarget;
    button.disabled = true;
    try {
      const document = structuredClone(imageEditor.doc),
        revision = imageEditor.saveRevision;
      await imageEditor.saveChain.catch(() => {});
      const blob = await saveProject(
        { ...record, editorDocument: document },
        tiles,
      );
      download(blob, "winshot.json");
      imageEditor.projectRevision = revision;
      showToast(
        "Editable project saved with your original image and annotations.",
      );
    } catch (error) {
      showToast(`Project could not be saved: ${error.message}`);
    } finally {
      button.disabled = false;
    }
  });

addEventListener("unload", () => {
  imageEditor?.destroy();
  objectUrls.forEach((url) => URL.revokeObjectURL(url));
});

try {
  if (!id) throw new Error("Missing capture ID.");
  record = await getRecord(id);
  if (!record)
    throw new Error(
      "Capture not found. It may have been removed from browser storage.",
    );
  tiles = await getTiles(id);
  renderRecord();
  if (imageEditor) {
    // Controls become usable only after settings, source tiles and all handlers
    // are ready. Early clicks must not disappear or export an undefined editor.
    document.querySelectorAll("[data-editor-loading]").forEach(node => { node.inert = false; });
    buttons.forEach(button => { button.disabled = false; });
    document.body.dataset.ready = "true";
  }
  document.body.setAttribute("aria-busy", "false");
  if (settingsUnavailable) showToast("Saved export settings could not be loaded. Using defaults for this tab.");
  // Consume remembered output actions once; reloading the editor must not save
  // another file. Partial/cancelled captures always require a deliberate export.
  const action = params.get("action");
  if (action) { params.delete("action"); history.replaceState(null, "", `${location.pathname}?${params}`); }
  const actions = { png: ["PNG", "png", exportPng], jpeg: ["JPEG", "jpg", exportJpeg], webp: ["WebP", "webp", exportWebp], bmp: ["BMP", "bmp", exportBmp], gif: ["GIF", "gif", exportGif], pdf: ["PDF", "pdf", (capture, captureTiles, progress) => exportPdf(capture, captureTiles, progress, settings.pdf)] };
  if (imageEditor && record.state === "complete" && record.report.coverage?.complete !== false && actions[action]) {
    await runExport(document.querySelector(`#save-${action}`), ...actions[action]);
  }
} catch (error) {
  document.body.setAttribute("aria-busy", "false");
  title.textContent = "Could not load capture";
  state.textContent = "failed";
  state.className = "pill failed";
  addReport("Error", error.message);
  document
    .querySelectorAll(
      "button,.editor-bar input,.editor-bar select,.editor-context input,.editor-context textarea",
    )
    .forEach((button) => {
      button.disabled = true;
    });
}
