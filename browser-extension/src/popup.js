import { loadSettings, normalizeSettings, saveSettings } from "./settings.js";

const status = document.querySelector("#status");
let settings = await loadSettings().catch(error => {
  status.className = "error";
  status.textContent = "Capture settings could not load. Reload WinShot and try again.";
  throw error;
});
let busy = false;
let statusRevision = 0;
const popupWindowId = (await chrome.windows.getCurrent()).id;
const startButtons = [...document.querySelectorAll("[data-mode], [data-picker], #repeat-last, #all-tabs, #all-tabs-pdf, #url-batch, #browser-window, #open-library, #open-video-studio")];

function setBusy(value) {
  busy = value;
  document.body.dataset.busy = String(value);
  document.body.setAttribute("aria-busy", String(value));
  for (const button of startButtons) button.disabled = value;
}

const delayNode = document.querySelector("#capture-delay");
const infiniteNode = document.querySelector("#capture-infinite");
const timeoutNode = document.querySelector("#capture-timeout");
const heightNode = document.querySelector("#capture-height");
const tilesNode = document.querySelector("#capture-tiles");
const templateNode = document.querySelector("#filename-template");
const batchModeNode = document.querySelector("#batch-mode");
const retentionNode = document.querySelector("#history-retention");
const contextMenuNode = document.querySelector("#context-menu");

function showSettings() {
  delayNode.value = String(settings.delayMs);
  infiniteNode.checked = settings.captureInfiniteGrowth;
  timeoutNode.value = String(settings.maxDurationMs / 60_000);
  heightNode.value = String(settings.maxHeightCss);
  tilesNode.value = String(settings.maxTiles);
  templateNode.value = settings.fileNameTemplate;
  batchModeNode.value = settings.batchMode;
  retentionNode.value = String(settings.historyRetentionDays);
  contextMenuNode.checked = settings.contextMenu;
}

async function persistSettings() {
  settings = await saveSettings(normalizeSettings({
    ...settings,
    delayMs: Number(delayNode.value),
    captureInfiniteGrowth: infiniteNode.checked,
    maxDurationMs: Number(timeoutNode.value) * 60_000,
    maxHeightCss: Number(heightNode.value),
    maxTiles: Number(tilesNode.value),
    fileNameTemplate: templateNode.value,
    batchMode: batchModeNode.value,
    historyRetentionDays: Number(retentionNode.value),
    contextMenu: contextMenuNode.checked
  }));
}

showSettings();
for (const node of [delayNode, infiniteNode, timeoutNode, heightNode, tilesNode, templateNode, batchModeNode, retentionNode, contextMenuNode]) {
  node.addEventListener("change", () => persistSettings().catch((error) => {
    status.className = "error";
    status.textContent = error.message;
  }));
}

// Callers set their own status text BEFORE calling (a delayed capture shows its
// countdown) — request must not stomp it with a generic message.
async function request(message) {
  status.className = "";
  const response = await chrome.runtime.sendMessage(message);
  if (response?.error) throw new Error(response.error);
  return response;
}

// Hold Alt while clicking any capture button for a one-off delayed capture (FireShot parity).
function launchSettings(event) {
  const delayMs = event?.altKey ? Math.max(settings.delayMs, 5000) : settings.delayMs;
  return { ...settings, delayMs };
}

async function startMode(mode, event) {
  if (busy) return;
  setBusy(true);
  try {
    await persistSettings();
    const launch = launchSettings(event);
    status.textContent = launch.delayMs ? `Capture starts in ${launch.delayMs / 1000} seconds...` : "Capture running. Keep this tab active...";
    const result = await request({ type: "WINSHOT_START_CAPTURE", mode, settings: launch });
    if (result?.error) throw new Error(result.error);
    status.textContent = result?.state === "complete" ? "Capture complete." : `Capture ${result?.state || "finished"}.`;
  } catch (error) {
    status.className = /cancelled/i.test(error.message) ? "" : "error";
    status.textContent = error.message;
  } finally {
    setBusy(false);
  }
}

async function startPicker(mode, event) {
  if (busy) return;
  setBusy(true);
  try {
    await persistSettings();
    const launch = launchSettings(event);
    status.textContent = launch.delayMs ? `Capture starts in ${launch.delayMs / 1000} seconds...` : "Starting...";
    await request({ type: "WINSHOT_START_PICKER", mode, settings: launch });
    window.close();
  } catch (error) {
    status.className = /cancelled/i.test(error.message) ? "" : "error";
    status.textContent = error.message;
  } finally {
    setBusy(false);
  }
}

for (const button of document.querySelectorAll("[data-mode]")) {
  button.addEventListener("click", (event) => startMode(button.dataset.mode, event));
}

for (const button of document.querySelectorAll("[data-picker]")) {
  button.addEventListener("click", (event) => startPicker(button.dataset.picker, event));
}

const MODE_LABELS = {
  "visible": "Visible viewport",
  "full-page": "Entire page",
  "region": "Drag a selection",
  "element": "Select an element"
};
const repeatLabel = document.querySelector("#repeat-label");
repeatLabel.textContent = MODE_LABELS[settings.lastMode] || MODE_LABELS["full-page"];
document.querySelector("#repeat-last").addEventListener("click", (event) => {
  const mode = MODE_LABELS[settings.lastMode] ? settings.lastMode : "full-page";
  if (mode === "region" || mode === "element") return startPicker(mode, event);
  return startMode(mode, event);
});

document.querySelector("#browser-window").addEventListener("click", async () => {
  try {
    // Navigating the current tab to an external-protocol URL launches the handler
    // without committing a navigation, so the page is untouched either way.
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (!tab?.id) throw new Error("No active browser tab was found.");
    await chrome.tabs.update(tab.id, { url: "winshot://capture-window" });
    status.textContent = "Asked WinShot desktop to capture this window. Nothing happening? Install WinShot desktop, or press Ctrl+Shift+8 in WinShot.";
  } catch {
    status.className = "error";
    status.textContent = "WinShot desktop is required for browser-window capture. You can also press Ctrl+Shift+8 in WinShot.";
  }
});

async function launchBatch(kind, urls = [], combinePdf = false) {
  setBusy(true);
  try {
    await persistSettings();
    return await request({ type: "WINSHOT_START_BATCH", kind, urls, combinePdf, settings });
  } finally {
    setBusy(false);
  }
}

document.querySelector("#all-tabs").addEventListener("click", async () => {
  try {
    status.textContent = "Preparing batch capture...";
    await launchBatch("all-tabs");
    window.close();
  } catch (error) {
    status.className = "error";
    status.textContent = error.message;
  }
});

document.querySelector("#all-tabs-pdf").addEventListener("click", async () => {
  try {
    status.textContent = "Capturing tabs for one PDF...";
    await launchBatch("all-tabs", [], true);
    window.close();
  } catch (error) {
    status.className = "error";
    status.textContent = error.message;
  }
});

document.querySelector("#url-batch").addEventListener("click", async () => {
  try {
    const urls = document.querySelector("#batch-urls").value.split(/\r?\n/).map((value) => value.trim()).filter(Boolean);
    if (!urls.length) throw new Error("Enter at least one website URL.");
    status.textContent = "Preparing URL batch...";
    await launchBatch("urls", urls);
    window.close();
  } catch (error) {
    status.className = "error";
    status.textContent = error.message;
  }
});

document.querySelector("#cancel").addEventListener("click", async () => {
  try {
    const result = await request({ type: "WINSHOT_CANCEL_CAPTURE" });
    status.textContent = result.cancelled ? "Cancelling and restoring the page..." : "No active capture in this tab.";
  } catch (error) {
    status.className = "error";
    status.textContent = error.message;
  }
});

document.querySelector("#open-history").addEventListener("click", async (event) => {
  event.preventDefault();
  await chrome.tabs.create({ url: chrome.runtime.getURL("src/history.html") });
  window.close();
});

document.querySelector("#open-library").addEventListener("click", async () => {
  await chrome.tabs.create({ url: chrome.runtime.getURL("src/library.html") });
  window.close();
});

document.querySelector("#open-video-studio").addEventListener("click", async () => {
  await chrome.tabs.create({ url: chrome.runtime.getURL("src/video-editor.html") });
  window.close();
});

chrome.runtime.onMessage.addListener((message) => {
  if ((message.type === "WINSHOT_CAPTURE_STATUS" || message.type === "WINSHOT_PROGRESS") &&
      message.windowId !== popupWindowId && !message.global) return;
  if (message.type === "WINSHOT_CAPTURE_STATUS") {
    statusRevision++;
    setBusy(message.busy);
    if (message.message) status.textContent = message.message;
    return;
  }
  if (message.type !== "WINSHOT_PROGRESS") return;
  statusRevision++;
  setBusy(message.busy ?? (message.state === "preparing" || message.state === "capturing"));
  status.textContent = message.message;
});

const initialRevision = statusRevision;
chrome.runtime.sendMessage({ type: "WINSHOT_GET_CAPTURE_STATUS" }).then(result => {
  if (statusRevision !== initialRevision) return;
  setBusy(Boolean(result?.busy));
  status.textContent = result?.message || "Captures stay on this device.";
}).catch(() => {
  if (statusRevision !== initialRevision) return;
  setBusy(false);
  status.textContent = "Captures stay on this device.";
}).finally(() => {
  document.querySelector("main").inert = false;
  document.querySelector("#cancel").disabled = false;
  document.body.dataset.ready = "true";
});
