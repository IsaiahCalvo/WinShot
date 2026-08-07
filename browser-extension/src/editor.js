import { getRecord, getTiles } from "./store.js";
import { exportJpeg, exportPdf, exportPng, exportWebp } from "./exporters.js";
import { loadSettings, saveSettings } from "./settings.js";

const params = new URLSearchParams(location.search);
const id = params.get("id");
const title = document.querySelector("#title");
const state = document.querySelector("#state");
const report = document.querySelector("#report");
const warnings = document.querySelector("#warnings");
const preview = document.querySelector("#preview");
const toast = document.querySelector("#toast");
const buttons = [...document.querySelectorAll("header button")];
let record;
let tiles = [];
let settings = await loadSettings();
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
  if (css) addReport("CSS size", `${Math.round(css.width)} × ${Math.round(css.height)}`);
  if (px) addReport("Pixel size", `${px.width} × ${px.height}`);
  addReport("Tiles", String(record.report.tileCount));
  if (record.report.seamConfidence) {
    addReport("Seam confidence", `${Math.round(record.report.seamConfidence.minimum * 100)}% minimum`);
  }
  if (record.report.coverage) addReport("Coverage", `${(record.report.coverage.ratio * 100).toFixed(2)}%`);
  addReport("Page restored", record.report.restoration);
  if (record.report.fallbackUsed) addReport("Fallback", record.report.fallbackUsed);

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
    buttons.forEach((button) => { button.disabled = true; });
    preview.textContent = "No verified pixels are available for export.";
    preview.style.padding = "30px";
    return;
  }

  preview.style.width = `${px.width}px`;
  preview.style.height = `${px.height}px`;
  for (const tile of tiles.sort((a, b) => a.index - b.index)) {
    const image = document.createElement("img");
    const url = URL.createObjectURL(tile.blob);
    objectUrls.push(url);
    image.src = url;
    image.alt = "";
    image.style.left = `${tile.destXPx}px`;
    image.style.top = `${tile.destYPx}px`;
    image.style.width = `${tile.widthPx}px`;
    image.style.height = `${tile.heightPx}px`;
    preview.append(image);
  }

  const available = Math.max(320, innerWidth - 360);
  if (px.width > available) preview.style.transform = `scale(${available / px.width})`;
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
  buttons.forEach((item) => { item.disabled = true; });
  const original = button.textContent;
  try {
    button.textContent = `${label} 0%`;
    const blob = await exporter(record, tiles, (progress) => { button.textContent = `${label} ${Math.round(progress * 100)}%`; });
    download(blob, extension);
    showToast(`${label} ready (${(blob.size / 1024 / 1024).toFixed(1)} MB).`);
  } catch (error) {
    showToast(error.message);
  } finally {
    button.textContent = original;
    buttons.forEach((item) => { item.disabled = false; });
  }
}

document.querySelector("#save-png").addEventListener("click", (event) => runExport(event.currentTarget, "PNG", "png", exportPng));
document.querySelector("#save-jpeg").addEventListener("click", (event) => runExport(event.currentTarget, "JPEG", "jpg", exportJpeg));
document.querySelector("#save-webp").addEventListener("click", (event) => runExport(event.currentTarget, "WebP", "webp", exportWebp));

document.querySelector("#copy-clipboard").addEventListener("click", async (event) => {
  const button = event.currentTarget;
  const original = button.textContent;
  try {
    button.textContent = "Copying…";
    // ClipboardItem accepts a Promise<Blob>, which also keeps the document "focused enough"
    // for the clipboard permission while the PNG is still being encoded.
    const item = new ClipboardItem({ "image/png": exportPng(record, tiles) });
    await navigator.clipboard.write([item]);
    showToast("Copied to clipboard.");
  } catch (error) {
    showToast(`Copy failed: ${error.message}. Very large captures can exceed the clipboard limit — use Save PNG instead.`);
  } finally {
    button.textContent = original;
  }
});

document.querySelector("#print-capture").addEventListener("click", async (event) => {
  const button = event.currentTarget;
  const original = button.textContent;
  try {
    // Print the assembled image, not the positioned tiles — raw tiles overlap by
    // design and would print duplicated bands. Print CSS shows only #print-image.
    button.textContent = "Preparing…";
    const blob = await exportPng(record, tiles, (progress) => { button.textContent = `Preparing ${Math.round(progress * 100)}%`; });
    const url = URL.createObjectURL(blob);
    objectUrls.push(url);
    let image = document.querySelector("#print-image");
    if (!image) {
      image = document.createElement("img");
      image.id = "print-image";
      document.body.append(image);
    }
    image.src = url;
    await new Promise((resolve, reject) => { image.onload = resolve; image.onerror = () => reject(new Error("The print image failed to load.")); });
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
  watermark: document.querySelector("#pdf-watermark")
};

function showPdfSettings() {
  for (const [key, node] of Object.entries(pdfNodes)) node.value = settings.pdf[key];
}

function readPdfSettings() {
  return {
    layout: pdfNodes.layout.value,
    pageSize: pdfNodes.pageSize.value,
    customWidthIn: Number(pdfNodes.customWidthIn.value),
    customHeightIn: Number(pdfNodes.customHeightIn.value),
    header: pdfNodes.header.value,
    footer: pdfNodes.footer.value,
    watermark: pdfNodes.watermark.value
  };
}

showPdfSettings();
for (const node of Object.values(pdfNodes)) {
  node.addEventListener("change", async () => {
    settings = await saveSettings({ ...settings, pdf: readPdfSettings() });
    showPdfSettings();
  });
}

document.querySelector("#save-pdf").addEventListener("click", async (event) => {
  const button = event.currentTarget;
  settings = await saveSettings({ ...settings, pdf: readPdfSettings() });
  await runExport(button, "PDF", "pdf", (capture, captureTiles, onProgress) => exportPdf(capture, captureTiles, onProgress, settings.pdf));
});

addEventListener("unload", () => objectUrls.forEach((url) => URL.revokeObjectURL(url)));

try {
  if (!id) throw new Error("Missing capture ID.");
  record = await getRecord(id);
  if (!record) throw new Error("Capture not found. It may have been removed from browser storage.");
  tiles = await getTiles(id);
  renderRecord();
} catch (error) {
  title.textContent = "Could not load capture";
  state.textContent = "failed";
  state.className = "pill failed";
  addReport("Error", error.message);
  buttons.forEach((button) => { button.disabled = true; });
}
