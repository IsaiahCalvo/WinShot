// Annotation model, drawing, and flattening shared by the editor and every export.
//
// Marks live in the capture's own pixel space (record.report.dimensionsPx), so they
// survive zoom, crop, and resize unchanged and a capture of any height can be edited:
// the editor draws only the visible window, and exports flatten the marks into fresh
// tiles that the existing PNG/JPEG/WebP/PDF writers consume as if they were captured.
//
// The tool set follows WinShot desktop's editor (Draw, Shapes, Text, Effects, Image).

export const TOOL_TYPES = Object.freeze([
  "pen", "highlighter", "rect", "ellipse", "line", "arrow", "step", "text", "redact", "spotlight"
]);

export const DEFAULT_STYLE = Object.freeze({
  color: "#ff3b30",
  width: 4,
  fill: "none",        // none | quarter | solid (rect, ellipse)
  dash: "solid",       // solid | dashed | dotted
  fontSize: 28,
  textStyle: "plain",  // plain | bold | outline | pill
  redactStyle: "pixelate", // pixelate | blur | blackout
  spotlightShape: "rect"
});

export const PALETTE = Object.freeze([
  "#ff3b30", "#ff9500", "#ffcc00", "#34c759", "#0a84ff", "#5e5ce6", "#ff2d55", "#ffffff", "#8e8e93", "#000000"
]);

const TEXT_FONT = "Segoe UI, system-ui, Arial, sans-serif";

export function newId() {
  return `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
}

// ---- geometry -------------------------------------------------------------

export function normalizeRect(a) {
  const x = Math.min(a.x, a.x + a.w);
  const y = Math.min(a.y, a.y + a.h);
  return { x, y, w: Math.abs(a.w), h: Math.abs(a.h) };
}

export function textMetrics(a, context = null) {
  const lines = String(a.text || "").split("\n");
  const size = a.fontSize || DEFAULT_STYLE.fontSize;
  const ctx = context || measureContext();
  ctx.font = textFont(a);
  const width = Math.max(size * 0.6, ...lines.map((line) => ctx.measureText(line || " ").width));
  const lineHeight = size * 1.25;
  const pad = a.textStyle === "pill" ? size * 0.45 : 0;
  return { lines, lineHeight, width, height: lineHeight * lines.length, pad };
}

let measureCanvas = null;
function measureContext() {
  measureCanvas ||= new OffscreenCanvas(4, 4);
  return measureCanvas.getContext("2d");
}

function textFont(a) {
  const size = a.fontSize || DEFAULT_STYLE.fontSize;
  const weight = a.textStyle === "bold" || a.textStyle === "outline" || a.textStyle === "pill" ? 700 : 500;
  return `${weight} ${size}px ${TEXT_FONT}`;
}

export function annotationBounds(a) {
  switch (a.type) {
    case "pen":
    case "highlighter": {
      const xs = a.points.map((p) => p[0]);
      const ys = a.points.map((p) => p[1]);
      const pad = (a.width || 1) / 2;
      return { x: Math.min(...xs) - pad, y: Math.min(...ys) - pad, w: Math.max(...xs) - Math.min(...xs) + pad * 2, h: Math.max(...ys) - Math.min(...ys) + pad * 2 };
    }
    case "line":
    case "arrow": {
      const pad = (a.width || 1) * 2;
      return { x: Math.min(a.x1, a.x2) - pad, y: Math.min(a.y1, a.y2) - pad, w: Math.abs(a.x2 - a.x1) + pad * 2, h: Math.abs(a.y2 - a.y1) + pad * 2 };
    }
    case "step": {
      const r = stepRadius(a);
      return { x: a.x - r, y: a.y - r, w: r * 2, h: r * 2 };
    }
    case "text": {
      const m = textMetrics(a);
      return { x: a.x - m.pad, y: a.y - m.pad, w: m.width + m.pad * 2, h: m.height + m.pad * 2 };
    }
    default:
      return normalizeRect(a);
  }
}

export function stepRadius(a) {
  return Math.max(10, (a.fontSize || DEFAULT_STYLE.fontSize) * 0.75);
}

function distanceToSegment(px, py, x1, y1, x2, y2) {
  const dx = x2 - x1;
  const dy = y2 - y1;
  const length = dx * dx + dy * dy;
  const t = length ? Math.max(0, Math.min(1, ((px - x1) * dx + (py - y1) * dy) / length)) : 0;
  return Math.hypot(px - (x1 + t * dx), py - (y1 + t * dy));
}

// True when (x, y) in image pixels lands on the mark. Filled and effect marks are hit
// anywhere inside; outlines only near their stroke, so a box never blocks what it frames.
export function hitTest(a, x, y, tolerance = 6) {
  const reach = tolerance + (a.width || 1) / 2;
  switch (a.type) {
    case "pen":
    case "highlighter":
      for (let i = 1; i < a.points.length; i++) {
        if (distanceToSegment(x, y, ...a.points[i - 1], ...a.points[i]) <= reach) return true;
      }
      return a.points.length === 1 && Math.hypot(x - a.points[0][0], y - a.points[0][1]) <= reach;
    case "line":
    case "arrow":
      return distanceToSegment(x, y, a.x1, a.y1, a.x2, a.y2) <= reach;
    case "rect": {
      const r = normalizeRect(a);
      const inside = x >= r.x - reach && x <= r.x + r.w + reach && y >= r.y - reach && y <= r.y + r.h + reach;
      if (!inside) return false;
      if (a.fill !== "none") return true;
      return x <= r.x + reach || x >= r.x + r.w - reach || y <= r.y + reach || y >= r.y + r.h - reach;
    }
    case "ellipse": {
      const r = normalizeRect(a);
      const rx = r.w / 2;
      const ry = r.h / 2;
      if (rx < 1 || ry < 1) return false;
      const nx = (x - (r.x + rx)) / rx;
      const ny = (y - (r.y + ry)) / ry;
      const d = Math.sqrt(nx * nx + ny * ny);
      if (a.fill !== "none") return d <= 1 + reach / Math.min(rx, ry);
      return Math.abs(d - 1) * Math.min(rx, ry) <= reach;
    }
    default: {
      const b = annotationBounds(a);
      return x >= b.x - tolerance && x <= b.x + b.w + tolerance && y >= b.y - tolerance && y <= b.y + b.h + tolerance;
    }
  }
}

export function translateAnnotation(a, dx, dy) {
  const moved = structuredClone(a);
  if (moved.points) moved.points = moved.points.map(([x, y]) => [x + dx, y + dy]);
  if ("x1" in moved) {
    moved.x1 += dx; moved.x2 += dx; moved.y1 += dy; moved.y2 += dy;
  } else {
    moved.x += dx; moved.y += dy;
  }
  return moved;
}

// ---- drawing --------------------------------------------------------------

function dashFor(a, ctx) {
  const w = a.width || 1;
  if (a.dash === "dashed") ctx.setLineDash([w * 3, w * 2]);
  else if (a.dash === "dotted") ctx.setLineDash([0.01, w * 2]);
  else ctx.setLineDash([]);
}

function hexToRgba(hex, alpha) {
  const value = /^#([0-9a-f]{6})$/i.exec(hex || "")?.[1] || "000000";
  const n = parseInt(value, 16);
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
}

function contrastText(hex) {
  const value = /^#([0-9a-f]{6})$/i.exec(hex || "")?.[1] || "000000";
  const n = parseInt(value, 16);
  const luma = ((n >> 16) & 255) * 0.299 + ((n >> 8) & 255) * 0.587 + (n & 255) * 0.114;
  return luma > 170 ? "#111111" : "#ffffff";
}

function strokePath(ctx, points) {
  ctx.beginPath();
  ctx.moveTo(points[0][0], points[0][1]);
  if (points.length === 1) {
    ctx.lineTo(points[0][0] + 0.01, points[0][1]);
  } else if (points.length === 2) {
    ctx.lineTo(points[1][0], points[1][1]);
  } else {
    // Midpoint quadratic smoothing keeps fast mouse strokes from looking polygonal.
    for (let i = 1; i < points.length - 1; i++) {
      const mx = (points[i][0] + points[i + 1][0]) / 2;
      const my = (points[i][1] + points[i + 1][1]) / 2;
      ctx.quadraticCurveTo(points[i][0], points[i][1], mx, my);
    }
    const last = points.at(-1);
    ctx.lineTo(last[0], last[1]);
  }
  ctx.stroke();
}

function drawArrowHead(ctx, a) {
  const angle = Math.atan2(a.y2 - a.y1, a.x2 - a.x1);
  const length = Math.max(12, (a.width || 4) * 4.2);
  const spread = Math.PI / 7;
  ctx.setLineDash([]);
  ctx.beginPath();
  ctx.moveTo(a.x2, a.y2);
  ctx.lineTo(a.x2 - length * Math.cos(angle - spread), a.y2 - length * Math.sin(angle - spread));
  ctx.lineTo(a.x2 - length * Math.cos(angle + spread), a.y2 - length * Math.sin(angle + spread));
  ctx.closePath();
  ctx.fill();
}

export function drawMark(ctx, a) {
  ctx.save();
  ctx.lineCap = "round";
  ctx.lineJoin = "round";
  ctx.strokeStyle = a.color;
  ctx.fillStyle = a.color;
  ctx.lineWidth = a.width || 1;
  switch (a.type) {
    case "pen":
      ctx.setLineDash([]);
      strokePath(ctx, a.points);
      break;
    case "highlighter":
      // Multiply keeps text under the highlight readable, like a real marker.
      ctx.globalCompositeOperation = "multiply";
      ctx.globalAlpha = 0.45;
      ctx.lineCap = "butt";
      ctx.setLineDash([]);
      strokePath(ctx, a.points);
      break;
    case "rect":
    case "ellipse": {
      const r = normalizeRect(a);
      ctx.beginPath();
      if (a.type === "rect") ctx.rect(r.x, r.y, r.w, r.h);
      else ctx.ellipse(r.x + r.w / 2, r.y + r.h / 2, r.w / 2, r.h / 2, 0, 0, Math.PI * 2);
      if (a.fill === "solid") ctx.fill();
      else if (a.fill === "quarter") {
        ctx.fillStyle = hexToRgba(a.color, 0.25);
        ctx.fill();
      }
      if (a.fill !== "solid") {
        dashFor(a, ctx);
        ctx.stroke();
      }
      break;
    }
    case "line":
    case "arrow": {
      dashFor(a, ctx);
      ctx.beginPath();
      let endX = a.x2;
      let endY = a.y2;
      if (a.type === "arrow") {
        // Stop the shaft inside the head so a thick line never pokes through its tip.
        const angle = Math.atan2(a.y2 - a.y1, a.x2 - a.x1);
        const inset = Math.max(12, (a.width || 4) * 4.2) * 0.6;
        endX -= inset * Math.cos(angle);
        endY -= inset * Math.sin(angle);
      }
      ctx.moveTo(a.x1, a.y1);
      ctx.lineTo(endX, endY);
      ctx.stroke();
      if (a.type === "arrow") drawArrowHead(ctx, a);
      break;
    }
    case "step": {
      const r = stepRadius(a);
      ctx.beginPath();
      ctx.arc(a.x, a.y, r, 0, Math.PI * 2);
      ctx.fill();
      ctx.lineWidth = Math.max(1.5, r * 0.12);
      ctx.strokeStyle = "#ffffff";
      ctx.stroke();
      ctx.fillStyle = contrastText(a.color);
      ctx.font = `700 ${Math.round(r * 1.1)}px ${TEXT_FONT}`;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(String(a.n ?? 1), a.x, a.y + r * 0.04);
      break;
    }
    case "text": {
      const m = textMetrics(a, ctx);
      ctx.font = textFont(a);
      ctx.textBaseline = "top";
      if (a.textStyle === "pill") {
        const radius = Math.min(m.height / 2 + m.pad, (a.fontSize || 28) * 0.7);
        ctx.fillStyle = "rgba(20,22,28,0.86)";
        ctx.beginPath();
        ctx.roundRect(a.x - m.pad, a.y - m.pad, m.width + m.pad * 2, m.height + m.pad * 2, radius);
        ctx.fill();
        ctx.fillStyle = a.color;
      }
      m.lines.forEach((line, index) => {
        const y = a.y + index * m.lineHeight + (m.lineHeight - (a.fontSize || 28)) / 2;
        if (a.textStyle === "outline") {
          ctx.lineWidth = Math.max(2, (a.fontSize || 28) * 0.16);
          ctx.strokeStyle = contrastText(a.color) === "#ffffff" ? "#ffffff" : "#111111";
          ctx.setLineDash([]);
          ctx.strokeText(line, a.x, y);
        }
        ctx.fillText(line, a.x, y);
      });
      break;
    }
    default:
      break;
  }
  ctx.restore();
}

// ---- effects (redaction and spotlight) ------------------------------------
//
// A redaction is computed ONCE from the source tiles into a small canvas, then scaled
// up wherever it is drawn. Every band of an export and every frame of the editor draws
// the same small canvas, so a mark spanning tiles has no seam and the preview matches
// the exported file exactly.

export function effectKey(a) {
  const r = normalizeRect(a);
  return `${a.redactStyle}:${Math.round(r.x)}:${Math.round(r.y)}:${Math.round(r.w)}:${Math.round(r.h)}:${a.strength || ""}`;
}

export async function buildEffect(a, source) {
  const r = normalizeRect(a);
  if (r.w < 1 || r.h < 1 || a.redactStyle === "blackout") return null;
  if (a.redactStyle === "pixelate") {
    const block = Math.max(4, a.strength || 14);
    const cols = Math.max(1, Math.ceil(r.w / block));
    const rows = Math.max(1, Math.ceil(r.h / block));
    const canvas = new OffscreenCanvas(cols, rows);
    const ctx = canvas.getContext("2d");
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = "high";
    await source.drawRegion(ctx, r.x, r.y, cols * block, rows * block, cols, rows);
    return { canvas, x: r.x, y: r.y, w: cols * block, h: rows * block, smooth: false };
  }
  // Blur: work at a reduced resolution so a page-tall redaction stays cheap, and pull
  // in a margin so the blur does not fade to transparent at the mark's edges.
  const radius = Math.max(4, a.strength || 18);
  const factor = Math.max(1, Math.ceil(Math.sqrt((r.w * r.h) / 1_500_000)), Math.floor(radius / 6));
  const margin = radius * 2;
  const sw = Math.max(1, Math.ceil((r.w + margin * 2) / factor));
  const sh = Math.max(1, Math.ceil((r.h + margin * 2) / factor));
  const raw = new OffscreenCanvas(sw, sh);
  const rawCtx = raw.getContext("2d");
  rawCtx.imageSmoothingQuality = "high";
  await source.drawRegion(rawCtx, r.x - margin, r.y - margin, sw * factor, sh * factor, sw, sh);
  const blurred = new OffscreenCanvas(sw, sh);
  const blurCtx = blurred.getContext("2d");
  blurCtx.filter = `blur(${radius / factor}px)`;
  blurCtx.drawImage(raw, 0, 0);
  // A second pass removes the faint shapes a single Gaussian can leave on hard text.
  const twice = new OffscreenCanvas(sw, sh);
  const twiceCtx = twice.getContext("2d");
  twiceCtx.filter = `blur(${radius / factor / 2}px)`;
  twiceCtx.drawImage(blurred, 0, 0);
  return {
    canvas: twice,
    x: r.x - margin,
    y: r.y - margin,
    w: sw * factor,
    h: sh * factor,
    clip: r,
    smooth: true
  };
}

function drawRedaction(ctx, a, effect) {
  const r = normalizeRect(a);
  ctx.save();
  ctx.beginPath();
  ctx.rect(r.x, r.y, r.w, r.h);
  ctx.clip();
  if (a.redactStyle === "blackout" || !effect) {
    ctx.fillStyle = a.redactStyle === "blackout" ? "#000000" : "#7f7f7f";
    ctx.fillRect(r.x, r.y, r.w, r.h);
  } else {
    ctx.imageSmoothingEnabled = effect.smooth;
    ctx.drawImage(effect.canvas, effect.x, effect.y, effect.w, effect.h);
  }
  ctx.restore();
}

function drawSpotlights(ctx, spots) {
  if (!spots.length) return;
  // Dim on a separate layer and punch every hole out of it, so overlapping spotlights
  // stay clear where they overlap (a single even-odd or winding path would re-dim it).
  const layer = new OffscreenCanvas(ctx.canvas.width, ctx.canvas.height);
  const lctx = layer.getContext("2d");
  lctx.fillStyle = "rgba(0,0,0,0.55)";
  lctx.fillRect(0, 0, layer.width, layer.height);
  lctx.setTransform(ctx.getTransform());
  lctx.globalCompositeOperation = "destination-out";
  lctx.fillStyle = "#000";
  for (const s of spots) {
    const r = normalizeRect(s);
    lctx.beginPath();
    if (s.spotlightShape === "ellipse") lctx.ellipse(r.x + r.w / 2, r.y + r.h / 2, Math.max(0.5, r.w / 2), Math.max(0.5, r.h / 2), 0, 0, Math.PI * 2);
    else lctx.rect(r.x, r.y, r.w, r.h);
    lctx.fill();
  }
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.drawImage(layer, 0, 0);
  ctx.restore();
}

// Draws every mark over pixels already on `ctx`, whose transform maps image pixels.
// Order: redactions hide pixels first, the spotlight dims, then marks stay readable on top.
export function drawAnnotations(ctx, annotations, effects) {
  for (const a of annotations) if (a.type === "redact") drawRedaction(ctx, a, effects?.get(effectKey(a)));
  drawSpotlights(ctx, annotations.filter((a) => a.type === "spotlight"));
  for (const a of annotations) if (a.type !== "redact" && a.type !== "spotlight") drawMark(ctx, a);
}

// ---- tile source ----------------------------------------------------------

export class TileSource {
  constructor(tiles, { cacheSize = 48 } = {}) {
    this.tiles = [...tiles].sort((a, b) => a.index - b.index);
    this.cache = new Map();
    this.pending = new Map();
    this.cacheSize = cacheSize;
  }

  intersecting(left, top, width, height) {
    return this.tiles.filter((tile) =>
      tile.destXPx < left + width && tile.destXPx + tile.widthPx > left &&
      tile.destYPx < top + height && tile.destYPx + tile.heightPx > top);
  }

  cached(tile) {
    const bitmap = this.cache.get(tile.index);
    if (bitmap) {
      this.cache.delete(tile.index);
      this.cache.set(tile.index, bitmap);
    }
    return bitmap || null;
  }

  async bitmap(tile) {
    const ready = this.cached(tile);
    if (ready) return ready;
    if (!this.pending.has(tile.index)) {
      this.pending.set(tile.index, createImageBitmap(tile.blob).then((bitmap) => {
        this.pending.delete(tile.index);
        this.cache.set(tile.index, bitmap);
        while (this.cache.size > this.cacheSize) {
          const [oldest, stale] = this.cache.entries().next().value;
          this.cache.delete(oldest);
          stale.close();
        }
        return bitmap;
      }));
    }
    return await this.pending.get(tile.index);
  }

  // Draw image-pixel region (left, top, width, height) into ctx at (0, 0, outW, outH).
  async drawRegion(ctx, left, top, width, height, outW, outH) {
    const sx = outW / width;
    const sy = outH / height;
    ctx.fillStyle = "#ffffff";
    ctx.fillRect(0, 0, outW, outH);
    for (const tile of this.intersecting(left, top, width, height)) {
      const bitmap = await this.bitmap(tile);
      ctx.drawImage(bitmap, (tile.destXPx - left) * sx, (tile.destYPx - top) * sy, tile.widthPx * sx, tile.heightPx * sy);
    }
  }

  close() {
    for (const bitmap of this.cache.values()) bitmap.close();
    this.cache.clear();
  }
}

// ---- edits and flattening -------------------------------------------------

export function emptyEdits() {
  return { annotations: [], crop: null, scale: 1 };
}

export function normalizeEdits(edits, dimensionsPx) {
  const value = edits || {};
  const width = dimensionsPx?.width || 1;
  const height = dimensionsPx?.height || 1;
  let crop = null;
  if (value.crop) {
    const r = normalizeRect(value.crop);
    const x = Math.max(0, Math.min(width - 1, Math.round(r.x)));
    const y = Math.max(0, Math.min(height - 1, Math.round(r.y)));
    const w = Math.max(1, Math.min(width - x, Math.round(r.w)));
    const h = Math.max(1, Math.min(height - y, Math.round(r.h)));
    if (!(x === 0 && y === 0 && w === width && h === height)) crop = { x, y, w, h };
  }
  const scale = Number.isFinite(value.scale) ? Math.min(4, Math.max(0.05, value.scale)) : 1;
  const annotations = Array.isArray(value.annotations)
    ? value.annotations.filter((a) => a && TOOL_TYPES.includes(a.type))
    : [];
  return { annotations, crop, scale };
}

export function hasEdits(edits) {
  return Boolean(edits && (edits.annotations?.length || edits.crop || (edits.scale && Math.abs(edits.scale - 1) > 1e-6)));
}

export function outputSize(record, edits) {
  const px = record.report.dimensionsPx;
  const crop = edits.crop || { x: 0, y: 0, w: px.width, h: px.height };
  return { width: Math.max(1, Math.round(crop.w * edits.scale)), height: Math.max(1, Math.round(crop.h * edits.scale)), crop };
}

export async function prepareEffects(annotations, source, cache = new Map()) {
  for (const a of annotations) {
    if (a.type !== "redact" || a.redactStyle === "blackout") continue;
    const key = effectKey(a);
    if (!cache.has(key)) cache.set(key, await buildEffect(a, source));
  }
  return cache;
}

const OUTPUT_TILE = 4096;

// Returns { record, tiles } where the edits are burned into fresh tiles. Without edits
// the original capture is returned untouched, so plain exports stay byte-for-byte the same.
export async function flattenEdits(record, tiles, onProgress = () => {}) {
  const px = record.report?.dimensionsPx;
  const edits = normalizeEdits(record.edits, px);
  if (!px || !hasEdits(edits)) return { record, tiles };
  const { width, height, crop } = outputSize(record, edits);
  const source = new TileSource(tiles, { cacheSize: 24 });
  try {
    const effects = await prepareEffects(edits.annotations, source);
    const output = [];
    const columns = Math.ceil(width / OUTPUT_TILE);
    const rows = Math.ceil(height / OUTPUT_TILE);
    const scale = edits.scale;
    let index = 0;
    for (let row = 0; row < rows; row++) {
      for (let column = 0; column < columns; column++) {
        const ox = column * OUTPUT_TILE;
        const oy = row * OUTPUT_TILE;
        const tw = Math.min(OUTPUT_TILE, width - ox);
        const th = Math.min(OUTPUT_TILE, height - oy);
        const canvas = new OffscreenCanvas(tw, th);
        const ctx = canvas.getContext("2d", { alpha: false });
        ctx.fillStyle = "#ffffff";
        ctx.fillRect(0, 0, tw, th);
        // Image-pixel area this output tile shows, with one source pixel of slack so
        // resampling at the edges has real neighbours.
        const area = { x: crop.x + ox / scale, y: crop.y + oy / scale, w: tw / scale, h: th / scale };
        ctx.setTransform(scale, 0, 0, scale, -area.x * scale, -area.y * scale);
        ctx.imageSmoothingQuality = "high";
        for (const tile of source.intersecting(area.x - 1, area.y - 1, area.w + 2, area.h + 2)) {
          const bitmap = await source.bitmap(tile);
          ctx.drawImage(bitmap, tile.destXPx, tile.destYPx, tile.widthPx, tile.heightPx);
        }
        drawAnnotations(ctx, edits.annotations, effects);
        const blob = await canvas.convertToBlob({ type: "image/png" });
        output.push({
          captureId: record.id,
          index: index++,
          blob,
          destXPx: ox,
          destYPx: oy,
          widthPx: tw,
          heightPx: th
        });
        onProgress(index / (rows * columns));
      }
    }
    return { record: editedRecord(record, edits, width, height, crop), tiles: output };
  } finally {
    source.close();
  }
}

// The record the exporters see after edits: new pixel size, and the searchable text and
// links moved by the crop (in CSS pixels) and dropped where the crop cut them away.
function editedRecord(record, edits, width, height, crop) {
  const px = record.report.dimensionsPx;
  const css = record.report.dimensionsCss || { width: px.width, height: px.height };
  const pxPerCss = px.width / Math.max(1, css.width);
  const left = crop.x / pxPerCss;
  const top = crop.y / pxPerCss;
  const cssWidth = crop.w / pxPerCss;
  const cssHeight = crop.h / pxPerCss;
  const keep = (entry) => entry.x + entry.width > left && entry.x < left + cssWidth && entry.y + entry.height > top && entry.y < top + cssHeight;
  const shift = (entry) => ({ ...entry, x: entry.x - left, y: entry.y - top });
  return {
    ...record,
    report: {
      ...record.report,
      dimensionsPx: { width, height },
      dimensionsCss: { width: cssWidth, height: cssHeight }
    },
    semantics: {
      text: (record.semantics?.text || []).filter(keep).map(shift),
      links: (record.semantics?.links || []).filter(keep).map(shift)
    }
  };
}
