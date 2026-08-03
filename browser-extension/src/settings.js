import { CaptureMode, DEFAULT_LIMITS } from "./contract.js";

export const SETTINGS_KEY = "winshot:capture-settings";

export const DEFAULT_SETTINGS = Object.freeze({
  delayMs: 0,
  captureInfiniteGrowth: true,
  maxDurationMs: DEFAULT_LIMITS.maxDurationMs,
  maxTiles: DEFAULT_LIMITS.maxTiles,
  maxHeightCss: DEFAULT_LIMITS.maxHeightCss,
  batchMode: CaptureMode.FULL_PAGE,
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
    maxGrowthPasses: settings.captureInfiniteGrowth ? DEFAULT_LIMITS.maxGrowthPasses : 0
  };
}

export function renderFileName(template, record, now = new Date()) {
  let host = "page";
  try { host = new URL(record.sourceUrl || "").hostname || host; } catch { /* Keep the fallback. */ }
  const values = {
    title: record.sourceTitle || "capture",
    host,
    date: now.toISOString().slice(0, 10),
    time: now.toTimeString().slice(0, 8).replaceAll(":", "-"),
    mode: record.mode || record.report?.mode || "capture"
  };
  const rendered = String(template || DEFAULT_SETTINGS.fileNameTemplate)
    .replace(/\{(title|host|date|time|mode)\}/gi, (_, key) => values[key.toLowerCase()]);
  return safePart(rendered, "winshot-capture").slice(0, 180);
}

export function renderPdfTemplate(template, context) {
  return String(template || "").replace(/\{(title|url|date|time|page|pages)\}/gi, (_, key) => {
    const value = context[key.toLowerCase()];
    return value == null ? "" : String(value);
  });
}
