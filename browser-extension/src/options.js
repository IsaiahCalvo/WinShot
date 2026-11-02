import { loadSettings, normalizeSettings, renderFileName, saveSettings } from "./settings.js";

const status = document.querySelector("#status");
const $ = (selector) => document.querySelector(selector);
let settings = await loadSettings();

const fields = {
  afterCapture: [$("#after-capture"), "value"],
  imageFormat: [$("#image-format"), "value"],
  downloadFolder: [$("#download-folder"), "value"],
  saveAs: [$("#save-as"), "checked"],
  fileNameTemplate: [$("#filename-template"), "value"],
  delayMs: [$("#capture-delay"), "number"],
  captureInfiniteGrowth: [$("#capture-infinite"), "checked"],
  maxDurationMs: [$("#capture-timeout"), "minutes"],
  maxHeightCss: [$("#capture-height"), "number"],
  maxTiles: [$("#capture-tiles"), "number"],
  batchMode: [$("#batch-mode"), "value"],
  contextMenu: [$("#context-menu"), "checked"],
  historyRetentionDays: [$("#history-retention"), "number"]
};

function show() {
  for (const [key, [node, kind]] of Object.entries(fields)) {
    if (kind === "checked") node.checked = Boolean(settings[key]);
    else if (kind === "minutes") node.value = String(settings[key] / 60_000);
    else node.value = String(settings[key]);
  }
  $("#filename-example").textContent = `${renderFileName(settings.fileNameTemplate, {
    sourceTitle: "Example page",
    sourceUrl: "https://example.com/article",
    mode: "full-page"
  }, new Date(), settings.fileNameCounter, settings.fileNameCounterPad)}.png`;
}

let statusTimer = 0;
function flash(text) {
  status.textContent = text;
  status.classList.add("show");
  clearTimeout(statusTimer);
  statusTimer = setTimeout(() => status.classList.remove("show"), 1600);
}

async function persist() {
  const next = { ...(await loadSettings()) };
  for (const [key, [node, kind]] of Object.entries(fields)) {
    if (kind === "checked") next[key] = node.checked;
    else if (kind === "number") next[key] = Number(node.value);
    else if (kind === "minutes") next[key] = Number(node.value) * 60_000;
    else next[key] = node.value;
  }
  settings = await saveSettings(normalizeSettings(next));
  show();
  flash("Saved");
}

for (const [node] of Object.values(fields)) node.addEventListener("change", () => persist().catch((error) => flash(error.message)));

async function showShortcuts() {
  const list = $("#shortcuts");
  list.textContent = "";
  const commands = await chrome.commands.getAll();
  for (const command of commands) {
    if (!command.description) continue;
    const name = document.createElement("span");
    name.textContent = command.description;
    const keys = document.createElement("kbd");
    keys.textContent = command.shortcut || "Not set";
    list.append(name, keys);
  }
}

$("#edit-shortcuts").addEventListener("click", () => chrome.tabs.create({ url: "chrome://extensions/shortcuts" }));
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes["winshot:capture-settings"]) {
    settings = normalizeSettings(changes["winshot:capture-settings"].newValue);
    show();
  }
});

show();
showShortcuts();
