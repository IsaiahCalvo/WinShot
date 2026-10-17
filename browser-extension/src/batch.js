import { renderEditedCapture } from "./image-editor.js";
import { exportBatchPdf, exportPng, exportJpeg, exportWebp, exportPdf, exportGif, exportBmp } from "./exporters.js";
import { loadSettings } from "./settings.js";
import { getRecord, getTiles } from "./store.js";

const id = new URL(location.href).searchParams.get("id");
const summaryNode = document.querySelector("#summary");
const resultsNode = document.querySelector("#results");
const skippedNode = document.querySelector("#skipped");
const pdfButton = document.querySelector("#save-batch-pdf");
const pdfStatus = document.querySelector("#pdf-status");
const filesButton = document.querySelector("#save-batch-files"), fileFormat = document.querySelector("#batch-file-format");
let exporting = false;
const formats = { png: [exportPng, "png"], jpeg: [exportJpeg, "jpg"], webp: [exportWebp, "webp"], pdf: [exportPdf, "pdf"], gif: [exportGif, "gif"], bmp: [exportBmp, "bmp"] };
function download(blob, name) {
  const url = URL.createObjectURL(blob), anchor = document.createElement("a");
  anchor.href = url; anchor.download = name; anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}
async function editedEntry(item) {
  const record = await getRecord(item.captureId), tiles = await getTiles(item.captureId);
  if (!record || !tiles.length) throw new Error("A capture was removed from history. Open the remaining captures individually.");
  return record.editorDocument ? renderEditedCapture(record, tiles, record.editorDocument) : { record, tiles };
}
async function saveIndividualFiles() {
  if (exporting) return;
  exporting = true;
  filesButton.disabled = pdfButton.disabled = fileFormat.disabled = true;
  try {
    const selected = fileFormat.value, [exporter, extension] = formats[selected], settings = await loadSettings(), files = [];
    const items = batch.results.filter(item => item.state === "complete");
    let totalBytes = 0;
    if (!items.length) throw new Error("No completed captures are available. Review partial captures individually.");
    for (const [index, item] of items.entries()) {
      pdfStatus.textContent = `Preparing ${index + 1} of ${items.length} files…`;
      const input = await editedEntry(item), blob = await exporter(input.record, input.tiles, () => {}, selected === "pdf" ? settings.pdf : undefined);
      totalBytes += blob.size;
      if (totalBytes > 512 * 1024 * 1024) throw new Error("This batch exceeds the 512 MB archive limit. Save its captures individually from history.");
      files.push({ name: `${String(index + 1).padStart(2, "0")}-${(input.record.fileName || "capture").replace(/[<>:"/\\|?*\x00-\x1f]/g, "-")}.${extension}`, blob });
    }
    const { archiveFiles } = await import("./file-archive.js");
    const zip = await archiveFiles(files);
    download(zip, `winshot-${batch.kind}-${selected}.zip`);
    pdfStatus.textContent = `${files.length} ${selected.toUpperCase()} files ready in one ZIP. Partial captures remain available for review.`;
  } catch (error) { pdfStatus.textContent = error.message; }
  finally { exporting = false; filesButton.disabled = pdfButton.disabled = fileFormat.disabled = false; }
}

const stored = id ? await chrome.storage.local.get(`batch:${id}`) : {};
const batch = stored[`batch:${id}`];

async function saveCombinedPdf() {
  if (exporting) return;
  exporting = true;
  pdfButton.disabled = filesButton.disabled = fileFormat.disabled = true;
  try {
    const complete = batch.results.filter((item) => item.state === "complete" || item.state === "partial");
    const entries = [];
    for (const item of complete) {
      entries.push(await editedEntry(item));
    }
    if (!entries.length) throw new Error("No completed captures are available for the PDF.");
    const settings = await loadSettings();
    const blob = await exportBatchPdf(entries, (progress) => {
      pdfStatus.textContent = `Building combined PDF: ${Math.round(progress * 100)}%`;
    }, settings.pdf);
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = `winshot-${batch.kind}-${new Date().toISOString().slice(0, 10)}.pdf`;
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 30_000);
    pdfStatus.textContent = `Combined PDF ready (${(blob.size / 1024 / 1024).toFixed(1)} MB).`;
  } catch (error) {
    pdfStatus.textContent = error.message;
  } finally {
    exporting = false;
    pdfButton.disabled = filesButton.disabled = fileFormat.disabled = false;
  }
}

if (!batch) {
  summaryNode.textContent = "This batch report is missing or expired.";
} else {
  const complete = batch.results.filter((item) => item.state === "complete").length;
  summaryNode.textContent = `${batch.cancelled ? "Batch stopped. " : ""}${complete} of ${batch.results.length} captures completed in ${batch.mode === "visible" ? "visible" : "entire-page"} mode; ${batch.skipped.length} tabs or URLs skipped.`;
  pdfButton.disabled = filesButton.disabled = complete === 0;
  fileFormat.value = Object.hasOwn(formats, batch.output) ? batch.output : "png";
  filesButton.addEventListener("click", saveIndividualFiles);
  pdfButton.addEventListener("click", saveCombinedPdf);
  for (const item of batch.results) {
    const row = document.createElement("article");
    row.className = `item ${item.state}`;
    const title = document.createElement("strong");
    title.textContent = item.title || item.url || "Capture";
    row.append(title);
    if (item.state === "complete" || item.state === "partial") {
      const link = document.createElement("a");
      link.href = chrome.runtime.getURL(`src/editor.html?id=${encodeURIComponent(item.captureId)}`);
      link.textContent = item.state === "complete" ? "Open capture" : "Open partial capture";
      row.append(link);
    } else {
      const error = document.createElement("small");
      error.textContent = item.error || "Capture failed.";
      row.append(error);
    }
    resultsNode.append(row);
  }
  if (batch.skipped.length) {
    const heading = document.createElement("h2");
    heading.textContent = "Skipped";
    skippedNode.append(heading);
    for (const item of batch.skipped) {
      const row = document.createElement("div");
      row.className = "item";
      row.textContent = `${item.title}: ${item.reason}`;
      skippedNode.append(row);
    }
  }
  if (!batch.cancelled && batch.combinePdf && complete) await saveCombinedPdf();
  else if (!batch.cancelled && Object.hasOwn(formats, batch.output) && complete) await saveIndividualFiles();
}
