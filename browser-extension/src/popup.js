import { loadSettings, normalizeSettings, saveSettings } from "./settings.js";

const status = document.querySelector("#status");
let settings = await loadSettings();

const delayNode = document.querySelector("#capture-delay");
const infiniteNode = document.querySelector("#capture-infinite");
const timeoutNode = document.querySelector("#capture-timeout");
const heightNode = document.querySelector("#capture-height");
const tilesNode = document.querySelector("#capture-tiles");
const templateNode = document.querySelector("#filename-template");
const batchModeNode = document.querySelector("#batch-mode");

function showSettings() {
  delayNode.value = String(settings.delayMs);
  infiniteNode.checked = settings.captureInfiniteGrowth;
  timeoutNode.value = String(settings.maxDurationMs / 60_000);
  heightNode.value = String(settings.maxHeightCss);
  tilesNode.value = String(settings.maxTiles);
  templateNode.value = settings.fileNameTemplate;
  batchModeNode.value = settings.batchMode;
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
    batchMode: batchModeNode.value
  }));
}

showSettings();
for (const node of [delayNode, infiniteNode, timeoutNode, heightNode, tilesNode, templateNode, batchModeNode]) {
  node.addEventListener("change", () => persistSettings().catch((error) => {
    status.className = "error";
    status.textContent = error.message;
  }));
}

async function request(message) {
  status.className = "";
  status.textContent = "Starting...";
  const response = await chrome.runtime.sendMessage(message);
  if (response?.error) throw new Error(response.error);
  return response;
}

for (const button of document.querySelectorAll("[data-mode]")) {
  button.addEventListener("click", async () => {
    try {
      await persistSettings();
      status.textContent = settings.delayMs ? `Capture starts in ${settings.delayMs / 1000} seconds...` : "Capture running. Keep this tab active...";
      const result = await request({ type: "WINSHOT_START_CAPTURE", mode: button.dataset.mode, settings });
      if (result?.error) throw new Error(result.error);
      status.textContent = result?.state === "complete" ? "Capture complete." : `Capture ${result?.state || "finished"}.`;
    } catch (error) {
      status.className = "error";
      status.textContent = error.message;
    }
  });
}

for (const button of document.querySelectorAll("[data-picker]")) {
  button.addEventListener("click", async () => {
    try {
      await persistSettings();
      await request({ type: "WINSHOT_START_PICKER", mode: button.dataset.picker, settings });
      window.close();
    } catch (error) {
      status.className = "error";
      status.textContent = error.message;
    }
  });
}

document.querySelector("#browser-window").addEventListener("click", async () => {
  try {
    await chrome.tabs.create({ url: "winshot://capture-window" });
    window.close();
  } catch {
    status.className = "error";
    status.textContent = "WinShot desktop is required for browser-window capture. You can also press Ctrl+Shift+8 in WinShot.";
  }
});

async function launchBatch(kind, urls = [], combinePdf = false) {
  await persistSettings();
  return await request({ type: "WINSHOT_START_BATCH", kind, urls, combinePdf, settings });
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

chrome.runtime.onMessage.addListener((message) => {
  if (message.type !== "WINSHOT_PROGRESS") return;
  status.textContent = message.message;
});
