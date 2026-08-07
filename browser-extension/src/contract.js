export const PROTOCOL_VERSION = 1;

export const CaptureMode = Object.freeze({
  VISIBLE: "visible",
  FULL_PAGE: "full-page",
  REGION: "region",
  ELEMENT: "element"
});

export const SessionState = Object.freeze({
  PREPARING: "preparing",
  CAPTURING: "capturing",
  COMPLETE: "complete",
  PARTIAL: "partial",
  FAILED: "failed",
  CANCELLED: "cancelled"
});

export const DEFAULT_LIMITS = Object.freeze({
  maxTiles: 400,
  maxWidthCss: 100000,
  maxHeightCss: 200000,
  maxDurationMs: 5 * 60 * 1000,
  maxGrowthPasses: 8,
  overlapCss: 64,
  settleTimeoutMs: 1800,
  minCaptureIntervalMs: 550
});

export function createSession({ id, mode, tab, target = null, limits = {} }) {
  return {
    protocolVersion: PROTOCOL_VERSION,
    id,
    mode,
    tabId: tab.id,
    windowId: tab.windowId,
    sourceUrl: tab.url || "",
    sourceTitle: tab.title || "Untitled page",
    target,
    limits: { ...DEFAULT_LIMITS, ...limits },
    startedAt: new Date().toISOString(),
    state: SessionState.PREPARING,
    report: {
      target: mode,
      mode,
      dimensionsCss: null,
      dimensionsPx: null,
      tileCount: 0,
      seamConfidence: null,
      warnings: [],
      fallbackUsed: null,
      restoration: "pending",
      coverage: null
    }
  };
}

export function axisPositions(extent, viewport, overlap = 0) {
  if (!Number.isFinite(extent) || !Number.isFinite(viewport) || extent <= 0 || viewport <= 0) {
    throw new Error("Capture dimensions must be positive finite numbers.");
  }
  if (extent <= viewport) return [0];

  const safeOverlap = Math.max(0, Math.min(overlap, viewport / 2));
  const step = Math.max(1, viewport - safeOverlap);
  const positions = [];
  for (let value = 0; value < extent - viewport; value += step) positions.push(value);
  const last = Math.max(0, extent - viewport);
  if (positions.length === 0 || Math.abs(positions.at(-1) - last) > 0.25) positions.push(last);
  return positions;
}

export function buildTilePlan({ width, height, viewportWidth, viewportHeight, overlap = 0 }) {
  const xs = axisPositions(width, viewportWidth, overlap);
  const ys = axisPositions(height, viewportHeight, overlap);
  const result = [];
  for (const y of ys) {
    for (const x of xs) result.push({ x, y });
  }
  return result;
}

export function validateCoverage(width, height, tiles, tolerance = 1.5) {
  if (!tiles.length) return { complete: false, ratio: 0, gaps: ["No tiles were captured."] };

  const yBreaks = new Set([0, height]);
  for (const tile of tiles) {
    yBreaks.add(Math.max(0, Math.min(height, tile.destY)));
    yBreaks.add(Math.max(0, Math.min(height, tile.destY + tile.heightCss)));
  }
  const rows = [...yBreaks].sort((a, b) => a - b);
  let coveredArea = 0;
  const gaps = [];

  for (let i = 0; i < rows.length - 1; i++) {
    const top = rows[i];
    const bottom = rows[i + 1];
    if (bottom - top <= tolerance) continue;
    const spans = tiles
      .filter((tile) => tile.destY <= top + tolerance && tile.destY + tile.heightCss >= bottom - tolerance)
      .map((tile) => [Math.max(0, tile.destX), Math.min(width, tile.destX + tile.widthCss)])
      .sort((a, b) => a[0] - b[0]);
    let cursor = 0;
    let rowCovered = 0;
    for (const [left, right] of spans) {
      if (left > cursor + tolerance) gaps.push(`Gap near x=${Math.round(cursor)}, y=${Math.round(top)}`);
      const start = Math.max(cursor, left);
      if (right > start) rowCovered += right - start;
      cursor = Math.max(cursor, right);
    }
    if (cursor < width - tolerance) gaps.push(`Gap near x=${Math.round(cursor)}, y=${Math.round(top)}`);
    coveredArea += Math.min(width, rowCovered) * (bottom - top);
  }

  const total = width * height;
  const ratio = total > 0 ? Math.min(1, coveredArea / total) : 0;
  return { complete: gaps.length === 0 && ratio >= 0.999, ratio, gaps: [...new Set(gaps)].slice(0, 20) };
}

export function restrictedPageReason(url = "") {
  const value = url.toLowerCase();
  if (value.startsWith("chrome://") || value.startsWith("edge://")) {
    return "The browser can capture this visible page, but it does not allow extensions to inspect or scroll its internal pages.";
  }
  if (value.startsWith("chrome-extension://") || value.startsWith("edge-extension://")) {
    return "Another extension page cannot be inspected. Visible capture may still work after an explicit click.";
  }
  if (value.startsWith("file://")) {
    return "Enable ‘Allow access to file URLs’ for WinShot to capture beyond the visible viewport.";
  }
  if (value.startsWith("view-source:") || value.startsWith("devtools://")) {
    return "This browser-owned page cannot be scrolled by an extension. Use visible capture.";
  }
  return "The browser did not expose this page to the extension. Use visible capture or capture the window with WinShot desktop.";
}

export function friendlyError(error) {
  const message = error instanceof Error ? error.message : String(error || "Unknown capture error");
  if (/cannot access|cannot be scripted|missing host permission|receiving end does not exist/i.test(message)) {
    return "This page is restricted by the browser. Visible capture may work, but scrolling and element capture are unavailable.";
  }
  if (/navigation|tab was closed|no tab with id/i.test(message)) {
    return "The page changed or closed during capture. WinShot stopped without claiming a complete result.";
  }
  if (/quota|max.*tile|limit/i.test(message)) {
    return "The capture reached its safety limit. Increase the limit and retry, or export the verified partial capture.";
  }
  return message;
}
