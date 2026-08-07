import { exportBatchPdf } from "./exporters.js";
import { loadSettings } from "./settings.js";
import { getRecord, getTiles } from "./store.js";

const id = new URL(location.href).searchParams.get("id");
const summaryNode = document.querySelector("#summary");
const resultsNode = document.querySelector("#results");
const skippedNode = document.querySelector("#skipped");
const pdfButton = document.querySelector("#save-batch-pdf");
const pdfStatus = document.querySelector("#pdf-status");

const stored = id ? await chrome.storage.local.get(`batch:${id}`) : {};
const batch = stored[`batch:${id}`];

async function saveCombinedPdf() {
  pdfButton.disabled = true;
  try {
    const complete = batch.results.filter((item) => item.state === "complete" || item.state === "partial");
    const entries = [];
    for (const item of complete) {
      const record = await getRecord(item.captureId);
      const tiles = await getTiles(item.captureId);
      if (record && tiles.length) entries.push({ record, tiles });
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
    pdfButton.disabled = false;
  }
}

if (!batch) {
  summaryNode.textContent = "This batch report is missing or expired.";
} else {
  const complete = batch.results.filter((item) => item.state === "complete").length;
  summaryNode.textContent = `${complete} of ${batch.results.length} captures completed in ${batch.mode === "visible" ? "visible" : "entire-page"} mode; ${batch.skipped.length} tabs or URLs skipped.`;
  pdfButton.disabled = complete === 0;
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
  if (batch.combinePdf && complete) await saveCombinedPdf();
}
