import { deleteCapture, listRecords } from "./store.js";

const PAGE_SIZE = 50;
const list = document.querySelector("#list");
const filterInput = document.querySelector("#filter");
const selectAll = document.querySelector("#select-all");
const deleteButton = document.querySelector("#delete-selected");
const loadMore = document.querySelector("#load-more");
const empty = document.querySelector("#empty");

let records = [];
let shown = 0;
let objectUrls = [];
const rows = new Map(); // id -> { row, checkbox }

function releaseObjectUrls() {
  for (const url of objectUrls) URL.revokeObjectURL(url);
  objectUrls = [];
}

function matches(record, query) {
  if (!query) return true;
  return `${record.sourceTitle || ""} ${record.sourceUrl || ""}`.toLowerCase().includes(query);
}

function filtered() {
  const query = filterInput.value.trim().toLowerCase();
  return records.filter((record) => matches(record, query));
}

function updateDeleteButton() {
  const count = [...rows.values()].filter((entry) => entry.checkbox.checked).length;
  deleteButton.textContent = `Delete selected [${count}]`;
  deleteButton.disabled = count === 0;
}

function renderRow(record) {
  const row = document.createElement("div");
  row.className = "row";

  const checkbox = document.createElement("input");
  checkbox.type = "checkbox";
  checkbox.addEventListener("click", (event) => { event.stopPropagation(); updateDeleteButton(); });

  let visual;
  if (record.thumbnail instanceof Blob) {
    visual = document.createElement("img");
    const url = URL.createObjectURL(record.thumbnail);
    objectUrls.push(url);
    visual.src = url;
    visual.loading = "lazy";
    visual.alt = "";
  } else {
    visual = document.createElement("div");
    visual.className = "no-thumb";
    visual.textContent = record.state;
  }

  const info = document.createElement("div");
  info.className = "info";
  const title = document.createElement("div");
  title.className = "title";
  title.textContent = record.sourceTitle || "(untitled)";
  const url = document.createElement("div");
  url.className = "url";
  url.textContent = record.sourceUrl || "";
  info.append(title, url);

  const side = document.createElement("div");
  side.className = "side";
  const pill = document.createElement("span");
  pill.className = `pill ${record.state}`;
  pill.textContent = record.state;
  const when = document.createElement("div");
  when.textContent = new Date(record.finishedAt || record.startedAt).toLocaleString();
  const size = document.createElement("div");
  const px = record.report?.dimensionsPx;
  size.textContent = px ? `${px.width}×${px.height}px · ${record.report.tileCount} tile(s)` : record.mode;
  side.append(pill, when, size);

  row.append(checkbox, visual, info, side);
  row.addEventListener("click", () => { location.href = `editor.html?id=${encodeURIComponent(record.id)}`; });
  rows.set(record.id, { row, checkbox });
  return row;
}

function render(reset = false) {
  if (reset) {
    releaseObjectUrls();
    list.textContent = "";
    rows.clear();
    shown = 0;
    selectAll.checked = false;
  }
  const visible = filtered();
  const next = visible.slice(shown, shown + PAGE_SIZE);
  for (const record of next) list.append(renderRow(record));
  shown += next.length;
  loadMore.hidden = shown >= visible.length;
  empty.hidden = visible.length > 0;
  updateDeleteButton();
}

async function reload() {
  records = (await listRecords()).sort((a, b) => (b.startedAt || "").localeCompare(a.startedAt || ""));
  render(true);
}

selectAll.addEventListener("change", () => {
  for (const entry of rows.values()) entry.checkbox.checked = selectAll.checked;
  updateDeleteButton();
});

deleteButton.addEventListener("click", async () => {
  const ids = [...rows.entries()].filter(([, entry]) => entry.checkbox.checked).map(([id]) => id);
  deleteButton.disabled = true;
  for (const id of ids) await deleteCapture(id);
  await reload();
});

filterInput.addEventListener("input", () => render(true));
loadMore.addEventListener("click", () => render(false));
addEventListener("pagehide", releaseObjectUrls);

reload();
