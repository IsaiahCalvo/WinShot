import { CaptureMode, DEFAULT_LIMITS } from "./contract.js";

export const SETTINGS_KEY = "winshot:capture-settings";

export const DEFAULT_SETTINGS = Object.freeze({
  delayMs: 0,
  captureInfiniteGrowth: true,
  maxDurationMs: DEFAULT_LIMITS.maxDurationMs,
  maxTiles: DEFAULT_LIMITS.maxTiles,
  maxHeightCss: DEFAULT_LIMITS.maxHeightCss,
  batchMode: CaptureMode.FULL_PAGE,
  lastMode: CaptureMode.FULL_PAGE,
  contextMenu: true,
  historyRetentionDays: 30, // 0 = keep forever
  fileNameCounter: 1,
  fileNameCounterPad: 3,
  fileNameTemplate: "{title}-{date}-{time}",
  pdf: Object.freeze({
    layout: "multipage",
    pageSize: "auto",
    customWidthIn: 8.5,
    customHeightIn: 11,
    header: "",
    footer: "Page {page} of {pages}",
    watermark: ""
  })
});

function finiteNumber(value, fallback, minimum, maximum) {
  const number = Number(value);
  return Number.isFinite(number) ? Math.min(maximum, Math.max(minimum, number)) : fallback;
}

export function normalizeSettings(value = {}) {
  const pdf = value.pdf || {};
  return {
    delayMs: finiteNumber(value.delayMs, DEFAULT_SETTINGS.delayMs, 0, 30_000),
    captureInfiniteGrowth: value.captureInfiniteGrowth !== false,
    maxDurationMs: finiteNumber(value.maxDurationMs, DEFAULT_SETTINGS.maxDurationMs, 30_000, 60 * 60 * 1000),
    maxTiles: Math.round(finiteNumber(value.maxTiles, DEFAULT_SETTINGS.maxTiles, 1, 2000)),
    maxHeightCss: Math.round(finiteNumber(value.maxHeightCss, DEFAULT_SETTINGS.maxHeightCss, 1000, 2_000_000)),
    batchMode: value.batchMode === CaptureMode.VISIBLE ? CaptureMode.VISIBLE : CaptureMode.FULL_PAGE,
    lastMode: [CaptureMode.VISIBLE, CaptureMode.FULL_PAGE, CaptureMode.REGION, CaptureMode.ELEMENT].includes(value.lastMode)
      ? value.lastMode : DEFAULT_SETTINGS.lastMode,
    contextMenu: value.contextMenu !== false,
    historyRetentionDays: Math.round(finiteNumber(value.historyRetentionDays, DEFAULT_SETTINGS.historyRetentionDays, 0, 3650)),
    fileNameCounter: Math.round(finiteNumber(value.fileNameCounter, DEFAULT_SETTINGS.fileNameCounter, 0, 1e9)),
    fileNameCounterPad: Math.round(finiteNumber(value.fileNameCounterPad, DEFAULT_SETTINGS.fileNameCounterPad, 0, 10)),
    fileNameTemplate: String(value.fileNameTemplate || DEFAULT_SETTINGS.fileNameTemplate).slice(0, 160),
    pdf: {
      layout: pdf.layout === "single" ? "single" : "multipage",
      pageSize: ["auto", "a4", "letter", "legal", "custom"].includes(pdf.pageSize) ? pdf.pageSize : "auto",
      customWidthIn: finiteNumber(pdf.customWidthIn, DEFAULT_SETTINGS.pdf.customWidthIn, 1, 200),
      customHeightIn: finiteNumber(pdf.customHeightIn, DEFAULT_SETTINGS.pdf.customHeightIn, 1, 200),
      header: String(pdf.header ?? DEFAULT_SETTINGS.pdf.header).slice(0, 500),
      footer: String(pdf.footer ?? DEFAULT_SETTINGS.pdf.footer).slice(0, 500),
      watermark: String(pdf.watermark ?? DEFAULT_SETTINGS.pdf.watermark).slice(0, 500)
    }
  };
}

export async function loadSettings() {
  const stored = await chrome.storage.local.get(SETTINGS_KEY);
  return normalizeSettings(stored[SETTINGS_KEY]);
}

export async function saveSettings(value) {
  const settings = normalizeSettings(value);
  await chrome.storage.local.set({ [SETTINGS_KEY]: settings });
  return settings;
}

function safePart(value, fallback) {
  const cleaned = String(value || fallback)
    .replace(/[<>:"/\\|?*\x00-\x1f]/g, "-")
    .replace(/[. ]+$/g, "")
    .replace(/\s+/g, " ")
    .trim();
  return cleaned || fallback;
}

export function captureLimits(settings) {
  return {
    maxDurationMs: settings.maxDurationMs,
    maxTiles: settings.maxTiles,
    maxHeightCss: settings.maxHeightCss,
    maxGrowthPasses: settings.captureInfiniteGrowth ? settings.maxTiles : 0
  };
}

// Supports both {title}-style tokens and FireShot-style %-tokens (%t %e %u %n %y %m %d %H %M %S %B %A).
// `counter` (with `counterPad`) is reserved by the caller — see reserveFileNameCounter.
export function renderFileName(template, record, now = new Date(), counter = null, counterPad = 0) {
  let host = "page";
  try { host = new URL(record.sourceUrl || "").hostname || host; } catch { /* Keep the fallback. */ }
  const p2 = (value) => String(value).padStart(2, "0");
  const months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
  const days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
  const counterText = counter === null ? "" : String(counter).padStart(counterPad, "0");
  const values = {
    title: record.sourceTitle || "capture",
    host,
    date: now.toISOString().slice(0, 10),
    time: now.toTimeString().slice(0, 8).replaceAll(":", "-"),
    mode: record.mode || record.report?.mode || "capture",
    counter: counterText
  };
  const rendered = String(template || DEFAULT_SETTINGS.fileNameTemplate)
    .replace(/\{(title|host|date|time|mode|counter)\}/gi, (_, key) => values[key.toLowerCase()])
    .replaceAll("%t", values.title).replaceAll("%e", host).replaceAll("%u", record.sourceUrl || "")
    .replaceAll("%n", counterText)
    .replaceAll("%y", String(now.getFullYear())).replaceAll("%m", p2(now.getMonth() + 1)).replaceAll("%d", p2(now.getDate()))
    .replaceAll("%H", p2(now.getHours())).replaceAll("%M", p2(now.getMinutes())).replaceAll("%S", p2(now.getSeconds()))
    .replaceAll("%B", months[now.getMonth()]).replaceAll("%A", days[now.getDay()]);
  return safePart(rendered, "winshot-capture").slice(0, 180);
}

// Counter reservation is serialized through this queue so concurrent captures cannot
// receive the same %n or clobber unrelated settings fields.
let counterQueue = Promise.resolve();
export function reserveFileNameCounter() {
  const next = counterQueue.then(async () => {
    const settings = await loadSettings();
    await saveSettings({ ...settings, fileNameCounter: settings.fileNameCounter + 1 });
    return { counter: settings.fileNameCounter, pad: settings.fileNameCounterPad };
  });
  counterQueue = next.catch(() => {});
  return next;
}

export function renderPdfTemplate(template, context) {
  return String(template || "").replace(/\{(title|url|date|time|page|pages)\}/gi, (_, key) => {
    const value = context[key.toLowerCase()];
    return value == null ? "" : String(value);
  });
}
