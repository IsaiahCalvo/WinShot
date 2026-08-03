const id = new URL(location.href).searchParams.get("id");
const summaryNode = document.querySelector("#summary");
const resultsNode = document.querySelector("#results");
const skippedNode = document.querySelector("#skipped");

const stored = id ? await chrome.storage.local.get(`batch:${id}`) : {};
const batch = stored[`batch:${id}`];
if (!batch) {
  summaryNode.textContent = "This batch report is missing or expired.";
} else {
  const complete = batch.results.filter((item) => item.state === "complete").length;
  summaryNode.textContent = `${complete} of ${batch.results.length} captures completed; ${batch.skipped.length} tabs or URLs skipped.`;
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
}
