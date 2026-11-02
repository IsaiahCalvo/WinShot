import { getRecord, getTiles, updateRecord } from "./store.js";
import { exportJpeg, exportPdf, exportPng, exportWebp } from "./exporters.js";
import { loadSettings, saveSettings } from "./settings.js";
import { saveBlob } from "./downloads.js";
import { ICONS } from "./icons.js";
import {
  DEFAULT_STYLE, PALETTE, TileSource, annotationBounds, drawAnnotations, drawMark, effectKey, buildEffect,
  flattenEdits, hasEdits, hitTest, newId, normalizeEdits, normalizeRect, outputSize, stepRadius, textMetrics,
  translateAnnotation
} from "./annotations.js";

const params = new URLSearchParams(location.search);
const id = params.get("id");
const thenAction = params.get("then");
const title = document.querySelector("#title");
const state = document.querySelector("#state");
const report = document.querySelector("#report");
const warnings = document.querySelector("#warnings");
const preview = document.querySelector("#preview");
const stage = document.querySelector("#stage");
const canvas = document.querySelector("#view");
const ctx = canvas.getContext("2d");
const toast = document.querySelector("#toast");
const textEditor = document.querySelector("#text-editor");
const contextBar = document.querySelector("#context");
const subbar = document.querySelector("#subbar");
const details = document.querySelector("#details");
const headerButtons = () => [...document.querySelectorAll("header button")];
const exportButtons = [...document.querySelectorAll(".actions button")];

let record;
let tiles = [];
let source = null;
let settings = await loadSettings();
const objectUrls = [];

// ---- icons and tooltips ----------------------------------------------------

function paintIcons(root = document) {
  for (const node of root.querySelectorAll("[data-icon]")) {
    if (node.dataset.painted) continue;
    const svg = ICONS[node.dataset.icon];
    if (!svg) continue;
    const label = node.textContent.trim();
    node.innerHTML = svg + (label ? `<span>${label}</span>` : "");
    node.dataset.painted = "1";
  }
}

const tooltip = document.querySelector("#tooltip");
let tooltipTimer = 0;
let tooltipWarmUntil = 0;
document.addEventListener("pointerover", (event) => {
  const target = event.target.closest?.("[data-tip]");
  clearTimeout(tooltipTimer);
  if (!target || event.pointerType === "touch") { tooltip.classList.remove("show"); return; }
  const show = () => {
    tooltip.textContent = target.dataset.tip;
    const rect = target.getBoundingClientRect();
    tooltip.classList.add("show");
    const width = tooltip.offsetWidth;
    tooltip.style.left = `${Math.max(6, Math.min(innerWidth - width - 6, rect.left + rect.width / 2 - width / 2))}px`;
    tooltip.style.top = `${rect.bottom + 6}px`;
    tooltipWarmUntil = Date.now() + 600;
  };
  // Wait once, then show neighbours at once — the desktop app's tooltip timing.
  if (Date.now() < tooltipWarmUntil) show(); else tooltipTimer = setTimeout(show, 450);
});
document.addEventListener("pointerout", (event) => {
  if (event.target.closest?.("[data-tip]")) {
    clearTimeout(tooltipTimer);
    tooltip.classList.remove("show");
    tooltipWarmUntil = Date.now() + 600;
  }
});
document.addEventListener("pointerdown", () => tooltip.classList.remove("show"), true);

function showToast(message) {
  toast.textContent = message;
  toast.classList.add("show");
  clearTimeout(showToast.timer);
  showToast.timer = setTimeout(() => toast.classList.remove("show"), 3500);
}

// ---- report panel ----------------------------------------------------------

function addReport(label, value) {
  const dt = document.createElement("dt");
  const dd = document.createElement("dd");
  dt.textContent = label;
  dd.textContent = value;
  report.append(dt, dd);
  return dd;
}

let outputSizeNode = null;
function renderReport() {
  title.textContent = record.sourceTitle || "WinShot capture";
  document.title = `${record.sourceTitle || "Capture"} — WinShot`;
  state.textContent = record.state;
  state.className = `pill ${record.state}`;
  const css = record.report.dimensionsCss;
  const px = record.report.dimensionsPx;
  addReport("Mode", record.report.mode);
  addReport("Target", record.report.target);
  if (css) addReport("CSS size", `${Math.round(css.width)} × ${Math.round(css.height)}`);
  if (px) addReport("Pixel size", `${px.width} × ${px.height}`);
  if (px) outputSizeNode = addReport("Output size", "");
  addReport("Tiles", String(record.report.tileCount));
  if (record.report.seamConfidence) addReport("Seam confidence", `${Math.round(record.report.seamConfidence.minimum * 100)}% minimum`);
  if (record.report.coverage) addReport("Coverage", `${(record.report.coverage.ratio * 100).toFixed(2)}%`);
  addReport("Page restored", record.report.restoration);
  if (record.report.fallbackUsed) addReport("Fallback", record.report.fallbackUsed);
  if (record.sourceUrl) {
    const dd = addReport("Page", "");
    const link = document.createElement("a");
    link.href = record.sourceUrl;
    link.textContent = record.sourceUrl;
    link.target = "_blank";
    link.rel = "noreferrer";
    link.style.color = "#7ab8ff";
    dd.append(link);
  }

  const items = [...(record.report.warnings || [])];
  if (record.error && !items.includes(record.error)) items.push(record.error);
  if (items.length) {
    warnings.hidden = false;
    const list = warnings.querySelector("ul");
    for (const item of [...new Set(items)]) {
      const li = document.createElement("li");
      li.textContent = item;
      list.append(li);
    }
    // Problems are worth seeing; a clean capture keeps the canvas wide.
    details.classList.remove("collapsed");
  }
}

function refreshOutputSize() {
  if (!outputSizeNode) return;
  const size = outputSize(record, edits);
  outputSizeNode.textContent = `${size.width} × ${size.height}${hasEdits(edits) ? " (edited)" : ""}`;
}

// ---- edit state ------------------------------------------------------------

let edits = { annotations: [], crop: null, scale: 1 };
const undoStack = [];
const redoStack = [];
let selectedId = null;
let draft = null;          // mark being drawn
let gesture = null;        // pointer interaction in progress
let cropDraft = null;      // crop rectangle while the Crop tool is armed
let tool = "select";
let category = null;
let spaceHeld = false;
let style = { ...DEFAULT_STYLE, ...(settings.editorStyle || {}) };
const effects = new Map();
const effectsPending = new Set();
let saveTimer = 0;

function snapshot() {
  return JSON.stringify(edits);
}

function commit(mutator) {
  const before = snapshot();
  mutator();
  edits = normalizeEdits(edits, record.report.dimensionsPx);
  if (snapshot() === before) return;
  undoStack.push(before);
  if (undoStack.length > 200) undoStack.shift();
  redoStack.length = 0;
  changed();
}

function changed() {
  if (fitted && record?.report?.dimensionsPx) setZoom(fitZoom());
  else layout();
  refreshOutputSize();
  syncUndoButtons();
  scheduleSave();
  requestRender();
}

function undo() {
  if (!undoStack.length) return;
  redoStack.push(snapshot());
  edits = JSON.parse(undoStack.pop());
  if (!edits.annotations.some((a) => a.id === selectedId)) selectedId = null;
  changed();
  syncContext();
}

function redo() {
  if (!redoStack.length) return;
  undoStack.push(snapshot());
  edits = JSON.parse(redoStack.pop());
  changed();
  syncContext();
}

function syncUndoButtons() {
  document.querySelector("#undo").disabled = !undoStack.length;
  document.querySelector("#redo").disabled = !redoStack.length;
}

function scheduleSave() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(persistEdits, 350);
}

async function persistEdits() {
  clearTimeout(saveTimer);
  if (!record) return;
  const fresh = await getRecord(record.id);
  if (!fresh) return;
  fresh.edits = edits;
  record.edits = edits;
  await updateRecord(fresh);
}

function selected() {
  return edits.annotations.find((a) => a.id === selectedId) || null;
}

function replaceAnnotation(next) {
  edits.annotations = edits.annotations.map((a) => (a.id === next.id ? next : a));
}

// ---- view (zoom, scroll, coordinates) -------------------------------------

const PAD = 32;
let zoom = 1;
let fitted = true;
const spacer = document.createElement("div");
spacer.className = "spacer";

function viewOrigin() {
  // While cropping, show the whole capture so the crop can grow back out.
  if (tool === "crop" || !edits.crop) return { x: 0, y: 0, w: record.report.dimensionsPx.width, h: record.report.dimensionsPx.height };
  return edits.crop;
}

function contentSize() {
  const o = viewOrigin();
  return { width: o.w * zoom + PAD * 2, height: o.h * zoom + PAD * 2 };
}

function offsets() {
  const size = contentSize();
  return {
    x: Math.max(PAD, (stage.clientWidth - size.width) / 2 + PAD),
    y: Math.max(PAD, (stage.clientHeight - size.height) / 2 + PAD)
  };
}

function layout() {
  const size = contentSize();
  spacer.style.width = `${Math.max(size.width, 1)}px`;
  spacer.style.height = `${Math.max(size.height, 1)}px`;
}

function fitZoom() {
  const o = viewOrigin();
  const available = { w: Math.max(80, stage.clientWidth - PAD * 2), h: Math.max(80, stage.clientHeight - PAD * 2) };
  // Fit the whole picture; a long page (more than twice as tall as the window can show
  // at full width) fits its width and scrolls instead of shrinking to a sliver.
  // Never upscale a small capture past 100%.
  const byWidth = available.w / o.w;
  const byBoth = Math.min(byWidth, available.h / o.h);
  return Math.min(1, o.h * byWidth > available.h * 2 ? byWidth : byBoth);
}

function setZoom(next, anchor = null) {
  const before = anchor ? screenToImage(anchor.clientX, anchor.clientY) : null;
  zoom = Math.min(8, Math.max(0.02, next));
  layout();
  if (before) {
    const rect = stage.getBoundingClientRect();
    const o = viewOrigin();
    const off = offsets();
    stage.scrollLeft = (before.x - o.x) * zoom + off.x - (anchor.clientX - rect.left);
    stage.scrollTop = (before.y - o.y) * zoom + off.y - (anchor.clientY - rect.top);
  }
  document.querySelector("#zoom-label").textContent = `${Math.round(zoom * 100)}%`;
  requestRender();
}

function fit() {
  fitted = true;
  setZoom(fitZoom());
  stage.scrollTop = 0;
  stage.scrollLeft = 0;
}

function screenToImage(clientX, clientY) {
  const rect = stage.getBoundingClientRect();
  const o = viewOrigin();
  const off = offsets();
  return {
    x: o.x + (clientX - rect.left + stage.scrollLeft - off.x) / zoom,
    y: o.y + (clientY - rect.top + stage.scrollTop - off.y) / zoom
  };
}

function imageToScreen(x, y) {
  const o = viewOrigin();
  const off = offsets();
  return { x: (x - o.x) * zoom + off.x - stage.scrollLeft, y: (y - o.y) * zoom + off.y - stage.scrollTop };
}

// ---- rendering -------------------------------------------------------------

let renderQueued = false;
function requestRender() {
  if (renderQueued) return;
  renderQueued = true;
  requestAnimationFrame(() => { renderQueued = false; render(); });
}

function ensureEffects() {
  for (const a of edits.annotations) {
    if (a.type !== "redact" || a.redactStyle === "blackout") continue;
    const key = effectKey(a);
    if (effects.has(key) || effectsPending.has(key)) continue;
    effectsPending.add(key);
    buildEffect(a, source).then((effect) => {
      effects.set(key, effect);
      effectsPending.delete(key);
      requestRender();
    }, () => effectsPending.delete(key));
  }
  if (draft?.type === "redact" && draft.redactStyle !== "blackout") {
    // Preview a redaction while it is drawn without rebuilding it on every mouse move.
    const key = effectKey(draft);
    if (!effects.has(key) && !effectsPending.has(key) && !gesture?.redactTimer) {
      gesture.redactTimer = setTimeout(() => {
        if (gesture) gesture.redactTimer = 0;
        const snapshotDraft = { ...draft };
        const k = effectKey(snapshotDraft);
        effectsPending.add(k);
        buildEffect(snapshotDraft, source).then((effect) => { effects.set(k, effect); effectsPending.delete(k); requestRender(); }, () => effectsPending.delete(k));
      }, 120);
    }
  }
}

function render() {
  if (!record || !source) return;
  const dpr = devicePixelRatio || 1;
  const width = stage.clientWidth;
  const height = stage.clientHeight;
  if (canvas.width !== Math.round(width * dpr) || canvas.height !== Math.round(height * dpr)) {
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;
  }
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  const o = viewOrigin();
  const off = offsets();
  const tx = (off.x - stage.scrollLeft) * dpr - o.x * zoom * dpr;
  const ty = (off.y - stage.scrollTop) * dpr - o.y * zoom * dpr;
  ctx.setTransform(zoom * dpr, 0, 0, zoom * dpr, tx, ty);

  // Visible image rectangle.
  const visible = {
    x: Math.max(o.x, o.x + (stage.scrollLeft - off.x) / zoom),
    y: Math.max(o.y, o.y + (stage.scrollTop - off.y) / zoom)
  };
  visible.w = Math.min(o.x + o.w, visible.x + width / zoom) - visible.x;
  visible.h = Math.min(o.y + o.h, visible.y + height / zoom) - visible.y;

  ctx.save();
  ctx.shadowColor = "rgba(0,0,0,0.55)";
  ctx.shadowBlur = 24;
  ctx.fillStyle = "#ffffff";
  ctx.fillRect(o.x, o.y, o.w, o.h);
  ctx.restore();

  ctx.save();
  ctx.beginPath();
  ctx.rect(o.x, o.y, o.w, o.h);
  ctx.clip();
  ctx.imageSmoothingEnabled = zoom < 2;
  ctx.imageSmoothingQuality = "high";
  if (visible.w > 0 && visible.h > 0) {
    for (const tile of source.intersecting(visible.x, visible.y, visible.w, visible.h)) {
      const bitmap = source.cached(tile);
      if (bitmap) ctx.drawImage(bitmap, tile.destXPx, tile.destYPx, tile.widthPx, tile.heightPx);
      else source.bitmap(tile).then(requestRender, () => {});
    }
  }
  ensureEffects();
  const marks = draft ? [...edits.annotations, draft] : edits.annotations;
  const visibleMarks = gesture?.editingText ? marks.filter((a) => a.id !== gesture.editingText) : marks;
  drawAnnotations(ctx, visibleMarks, effects);
  ctx.restore();

  if (tool === "crop") drawCropOverlay(dpr);
  drawSelection(dpr);
}

function screenRect(r) {
  const a = imageToScreen(r.x, r.y);
  return { x: a.x, y: a.y, w: r.w * zoom, h: r.h * zoom };
}

function handlesFor(a) {
  if (!a) return [];
  if (a.type === "line" || a.type === "arrow") return [{ key: "p1", x: a.x1, y: a.y1 }, { key: "p2", x: a.x2, y: a.y2 }];
  if (["rect", "ellipse", "redact", "spotlight"].includes(a.type)) return rectHandles(normalizeRect(a));
  return [];
}

function rectHandles(r) {
  return [
    { key: "nw", x: r.x, y: r.y }, { key: "n", x: r.x + r.w / 2, y: r.y }, { key: "ne", x: r.x + r.w, y: r.y },
    { key: "e", x: r.x + r.w, y: r.y + r.h / 2 }, { key: "se", x: r.x + r.w, y: r.y + r.h },
    { key: "s", x: r.x + r.w / 2, y: r.y + r.h }, { key: "sw", x: r.x, y: r.y + r.h }, { key: "w", x: r.x, y: r.y + r.h / 2 }
  ];
}

function drawHandle(x, y, dpr) {
  ctx.fillStyle = "#ffffff";
  ctx.strokeStyle = "#0a84ff";
  ctx.lineWidth = 1.5 * dpr;
  ctx.beginPath();
  ctx.rect(x * dpr - 4.5 * dpr, y * dpr - 4.5 * dpr, 9 * dpr, 9 * dpr);
  ctx.fill();
  ctx.stroke();
}

function drawSelection(dpr) {
  const a = selected();
  if (!a || tool === "crop") return;
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  const b = screenRect(annotationBounds(a));
  ctx.save();
  ctx.strokeStyle = "#0a84ff";
  ctx.lineWidth = 1 * dpr;
  ctx.setLineDash([4 * dpr, 3 * dpr]);
  ctx.strokeRect((b.x - 3) * dpr, (b.y - 3) * dpr, (b.w + 6) * dpr, (b.h + 6) * dpr);
  ctx.restore();
  for (const h of handlesFor(a)) {
    const s = imageToScreen(h.x, h.y);
    drawHandle(s.x, s.y, dpr);
  }
}

function drawCropOverlay(dpr) {
  const px = record.report.dimensionsPx;
  const crop = cropDraft || edits.crop || { x: 0, y: 0, w: px.width, h: px.height };
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  const all = screenRect({ x: 0, y: 0, w: px.width, h: px.height });
  const c = screenRect(normalizeRect(crop));
  ctx.save();
  ctx.fillStyle = "rgba(0,0,0,0.55)";
  ctx.beginPath();
  ctx.rect(all.x * dpr, all.y * dpr, all.w * dpr, all.h * dpr);
  ctx.rect(c.x * dpr, c.y * dpr, c.w * dpr, c.h * dpr);
  ctx.fill("evenodd");
  ctx.strokeStyle = "#ffffff";
  ctx.lineWidth = 1.5 * dpr;
  ctx.strokeRect(c.x * dpr, c.y * dpr, c.w * dpr, c.h * dpr);
  ctx.strokeStyle = "rgba(255,255,255,0.35)";
  ctx.lineWidth = 1 * dpr;
  for (let i = 1; i < 3; i++) {
    ctx.beginPath();
    ctx.moveTo((c.x + (c.w * i) / 3) * dpr, c.y * dpr);
    ctx.lineTo((c.x + (c.w * i) / 3) * dpr, (c.y + c.h) * dpr);
    ctx.moveTo(c.x * dpr, (c.y + (c.h * i) / 3) * dpr);
    ctx.lineTo((c.x + c.w) * dpr, (c.y + (c.h * i) / 3) * dpr);
    ctx.stroke();
  }
  ctx.restore();
  for (const h of rectHandles(normalizeRect(crop))) {
    const s = imageToScreen(h.x, h.y);
    drawHandle(s.x, s.y, dpr);
  }
  const label = cropSizeLabel();
  if (label) label.textContent = `${Math.round(normalizeRect(crop).w)} × ${Math.round(normalizeRect(crop).h)}`;
}

// ---- tools ---------------------------------------------------------------

const CATEGORIES = {
  draw: [
    { tool: "pen", icon: "Pen", label: "Pen", key: "P" },
    { tool: "highlighter", icon: "Highlighter", label: "Highlighter", key: "H" },
    { tool: "eraser", icon: "Eraser", label: "Eraser", key: "E" }
  ],
  shapes: [
    { tool: "rect", icon: "Rect", label: "Rectangle", key: "R" },
    { tool: "ellipse", icon: "Ellipse", label: "Ellipse", key: "O" },
    { tool: "line", icon: "Line", label: "Line", key: "L" },
    { tool: "arrow", icon: "Arrow", label: "Arrow", key: "A" },
    { tool: "step", icon: "Counter", label: "Number", key: "C" }
  ],
  text: [
    { tool: "text", icon: "TextBox", label: "Text", key: "T" }
  ],
  effects: [
    { tool: "redact", icon: "Pixelate", label: "Redact", key: "X" },
    { tool: "spotlight", icon: "Spotlight", label: "Spotlight", key: "" }
  ],
  image: [
    { tool: "crop", icon: "Crop", label: "Crop", key: "K" },
    { action: "resize", icon: "FitScreen", label: "Resize…" }
  ]
};

const TOOL_CATEGORY = Object.fromEntries(Object.entries(CATEGORIES).flatMap(([name, items]) => items.filter((i) => i.tool).map((i) => [i.tool, name])));
const KEY_TOOLS = Object.fromEntries(Object.values(CATEGORIES).flat().filter((i) => i.key).map((i) => [i.key.toLowerCase(), i.tool]));
KEY_TOOLS.v = "select";
const DRAW_TOOLS = new Set(["pen", "highlighter", "rect", "ellipse", "line", "arrow", "step", "redact", "spotlight"]);
const lastToolIn = { draw: "pen", shapes: "rect", text: "text", effects: "redact", image: "crop" };

function setTool(next) {
  if (tool === "crop" && next !== "crop") cancelCrop();
  commitTextEditor();
  tool = next;
  const cat = TOOL_CATEGORY[next] || null;
  if (cat) { category = cat; lastToolIn[cat] = next; }
  else category = null;
  if (next !== "select") selectedId = next === "pan" ? selectedId : null;
  if (next === "crop") cropDraft = edits.crop ? { ...edits.crop } : null;
  for (const button of document.querySelectorAll("[data-tool]")) button.classList.toggle("active", button.dataset.tool === tool);
  for (const button of document.querySelectorAll("[data-category]")) button.classList.toggle("active", button.dataset.category === category);
  stage.className = `stage tool-${DRAW_TOOLS.has(next) || next === "crop" ? "draw" : next}`;
  buildSubbar();
  syncContext();
  layout();
  if (next === "crop" || (edits.crop && fitted)) fit();
  requestRender();
}

function buildSubbar() {
  subbar.textContent = "";
  if (!category) return;
  for (const item of CATEGORIES[category]) {
    const button = document.createElement("button");
    button.className = "subtool";
    button.dataset.icon = item.icon;
    button.textContent = item.label;
    if (item.tool) {
      button.dataset.tool = item.tool;
      button.dataset.tip = item.key ? `${item.label} (${item.key})` : item.label;
      button.classList.toggle("active", item.tool === tool);
      button.addEventListener("click", () => setTool(item.tool));
    } else {
      button.dataset.tip = item.label.replace("…", "");
      button.addEventListener("click", () => runAction(item.action));
    }
    subbar.append(button);
  }
  if (tool === "crop") {
    const hint = document.createElement("span");
    hint.className = "hint";
    hint.id = "crop-size";
    subbar.append(hint);
    const grow = document.createElement("span");
    grow.className = "grow";
    subbar.append(grow);
    const reset = document.createElement("button");
    reset.textContent = "Reset crop";
    reset.addEventListener("click", () => { cropDraft = null; commit(() => { edits.crop = null; }); setTool("select"); });
    const cancel = document.createElement("button");
    cancel.textContent = "Cancel";
    cancel.addEventListener("click", () => setTool("select"));
    const apply = document.createElement("button");
    apply.className = "primary";
    apply.textContent = "Apply crop";
    apply.addEventListener("click", applyCrop);
    subbar.append(reset, cancel, apply);
  }
  paintIcons(subbar);
}

function cropSizeLabel() {
  return document.querySelector("#crop-size");
}

function applyCrop() {
  const next = cropDraft ? normalizeRect(cropDraft) : null;
  cropDraft = null;
  commit(() => { edits.crop = next && next.w >= 2 && next.h >= 2 ? next : edits.crop; });
  tool = "select";
  setTool("select");
  fit();
}

function cancelCrop() {
  cropDraft = null;
}

// ---- context controls (style for the armed tool or the selected mark) ------

function activeType() {
  const a = selected();
  if (a && tool === "select") return a.type;
  return DRAW_TOOLS.has(tool) || tool === "text" ? tool : null;
}

function currentValue(key) {
  const a = selected();
  if (a && tool === "select" && key in a) return a[key];
  return style[key];
}

function applyStyle(key, value) {
  style[key] = value;
  settings.editorStyle = { ...style };
  saveSettings(settings).catch(() => {});
  const a = selected();
  if (a && key in a) {
    commit(() => replaceAnnotation({ ...a, [key]: value }));
  }
  if (gesture?.editingText) styleTextEditor();
  syncContext();
  requestRender();
}

function chipGroup(key, options) {
  const wrap = document.createElement("span");
  wrap.className = "swatches";
  for (const option of options) {
    const button = document.createElement("button");
    button.className = "chip";
    button.dataset.tip = option.tip || option.label;
    if (option.icon) button.dataset.icon = option.icon;
    else button.textContent = option.label;
    button.classList.toggle("active", currentValue(key) === option.value);
    button.addEventListener("click", () => applyStyle(key, option.value));
    wrap.append(button);
  }
  return wrap;
}

function labeled(text, node) {
  const label = document.createElement("label");
  label.textContent = text;
  const wrap = document.createElement("span");
  wrap.style.display = "inline-flex";
  wrap.style.alignItems = "center";
  wrap.style.gap = "5px";
  wrap.append(label, node);
  return wrap;
}

function syncContext() {
  contextBar.textContent = "";
  const type = activeType();
  if (!type) {
    if (tool === "select" && !selected()) {
      const hint = document.createElement("span");
      hint.className = "hint";
      hint.style.color = "var(--muted)";
      hint.textContent = edits.annotations.length ? "Click a mark to edit it. Delete removes it." : "Pick a tool to mark up the capture.";
      contextBar.append(hint);
    }
    return;
  }
  const usesColor = !["redact", "spotlight"].includes(type);
  if (usesColor) {
    const swatches = document.createElement("span");
    swatches.className = "swatches";
    for (const color of PALETTE) {
      const button = document.createElement("button");
      button.className = "swatch";
      button.style.background = color;
      button.dataset.tip = color;
      button.setAttribute("aria-label", `Colour ${color}`);
      button.classList.toggle("active", currentValue("color")?.toLowerCase() === color);
      button.addEventListener("click", () => applyStyle("color", color));
      swatches.append(button);
    }
    const custom = document.createElement("input");
    custom.type = "color";
    custom.value = currentValue("color") || "#ff3b30";
    custom.dataset.tip = "Custom colour";
    custom.addEventListener("change", () => applyStyle("color", custom.value));
    swatches.append(custom);
    contextBar.append(swatches);
  }
  if (["pen", "highlighter", "rect", "ellipse", "line", "arrow"].includes(type)) {
    const width = document.createElement("select");
    for (const value of [2, 3, 4, 6, 8, 12, 16, 24]) {
      const option = document.createElement("option");
      option.value = String(value);
      option.textContent = `${value} px`;
      width.append(option);
    }
    const current = currentValue("width") || 4;
    if (![...width.options].some((o) => Number(o.value) === current)) {
      const option = document.createElement("option");
      option.value = String(current);
      option.textContent = `${current} px`;
      width.append(option);
    }
    width.value = String(current);
    width.addEventListener("change", () => applyStyle("width", Number(width.value)));
    contextBar.append(labeled("Width", width));
  }
  if (["rect", "ellipse"].includes(type)) {
    contextBar.append(chipGroup("fill", [
      { value: "none", icon: "FillNone", tip: "No fill" },
      { value: "quarter", icon: "FillQuarter", tip: "Light fill" },
      { value: "solid", icon: "FillSolid", tip: "Solid fill" }
    ]));
  }
  if (["rect", "ellipse", "line", "arrow"].includes(type)) {
    const dash = document.createElement("select");
    for (const [value, label] of [["solid", "Solid"], ["dashed", "Dashed"], ["dotted", "Dotted"]]) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = label;
      dash.append(option);
    }
    dash.value = currentValue("dash") || "solid";
    dash.addEventListener("change", () => applyStyle("dash", dash.value));
    contextBar.append(labeled("Line", dash));
  }
  if (["text", "step"].includes(type)) {
    const size = document.createElement("select");
    for (const value of [14, 18, 22, 28, 36, 48, 64, 96]) {
      const option = document.createElement("option");
      option.value = String(value);
      option.textContent = `${value}`;
      size.append(option);
    }
    const current = currentValue("fontSize") || 28;
    if (![...size.options].some((o) => Number(o.value) === current)) {
      const option = document.createElement("option");
      option.value = String(current);
      option.textContent = String(current);
      size.append(option);
    }
    size.value = String(current);
    size.addEventListener("change", () => applyStyle("fontSize", Number(size.value)));
    contextBar.append(labeled("Size", size));
  }
  if (type === "text") {
    contextBar.append(chipGroup("textStyle", [
      { value: "plain", label: "Aa", tip: "Plain" },
      { value: "bold", label: "B", tip: "Bold" },
      { value: "outline", label: "O", tip: "Outlined" },
      { value: "pill", label: "▬", tip: "Label" }
    ]));
  }
  if (type === "step") {
    const next = document.createElement("input");
    next.type = "number";
    next.min = "0";
    next.max = "999";
    const a = selected();
    next.value = String(a && tool === "select" && a.type === "step" ? a.n : nextStepNumber());
    next.addEventListener("change", () => {
      const value = Math.max(0, Math.round(Number(next.value) || 0));
      const sel = selected();
      if (sel?.type === "step" && tool === "select") commit(() => replaceAnnotation({ ...sel, n: value }));
      else style.nextStep = value;
    });
    contextBar.append(labeled(a && tool === "select" ? "Number" : "Next", next));
  }
  if (type === "redact") {
    contextBar.append(chipGroup("redactStyle", [
      { value: "pixelate", label: "Pixelate" },
      { value: "blur", label: "Blur" },
      { value: "blackout", label: "Black out" }
    ]));
  }
  if (type === "spotlight") {
    contextBar.append(chipGroup("spotlightShape", [
      { value: "rect", icon: "Rect", tip: "Rectangle" },
      { value: "ellipse", icon: "Ellipse", tip: "Ellipse" }
    ]));
  }
  if (selected() && tool === "select") {
    const remove = document.createElement("button");
    remove.className = "icon";
    remove.dataset.icon = "Trash";
    remove.dataset.tip = "Delete (Del)";
    remove.setAttribute("aria-label", "Delete mark");
    remove.addEventListener("click", deleteSelected);
    contextBar.append(remove);
  }
  paintIcons(contextBar);
}

function nextStepNumber() {
  if (Number.isFinite(style.nextStep)) return style.nextStep;
  const numbers = edits.annotations.filter((a) => a.type === "step").map((a) => a.n);
  return numbers.length ? Math.max(...numbers) + 1 : 1;
}

function deleteSelected() {
  if (!selectedId) return;
  const gone = selectedId;
  selectedId = null;
  commit(() => { edits.annotations = edits.annotations.filter((a) => a.id !== gone); });
  syncContext();
}

// ---- pointer interaction ----------------------------------------------------

function topmostAt(point) {
  const tolerance = 6 / zoom;
  for (let i = edits.annotations.length - 1; i >= 0; i--) {
    if (hitTest(edits.annotations[i], point.x, point.y, tolerance)) return edits.annotations[i];
  }
  return null;
}

function handleAt(clientX, clientY, handles) {
  const rect = stage.getBoundingClientRect();
  for (const h of handles) {
    const s = imageToScreen(h.x, h.y);
    if (Math.abs(s.x - (clientX - rect.left)) <= 7 && Math.abs(s.y - (clientY - rect.top)) <= 7) return h;
  }
  return null;
}

function constrainPoint(start, point, shift, kind) {
  if (!shift) return point;
  const dx = point.x - start.x;
  const dy = point.y - start.y;
  if (kind === "angle") {
    const angle = Math.round(Math.atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
    const length = Math.hypot(dx, dy);
    return { x: start.x + Math.cos(angle) * length, y: start.y + Math.sin(angle) * length };
  }
  const side = Math.max(Math.abs(dx), Math.abs(dy));
  return { x: start.x + Math.sign(dx || 1) * side, y: start.y + Math.sign(dy || 1) * side };
}

function resizeRect(r, key, point) {
  let { x, y, w, h } = r;
  const right = x + w;
  const bottom = y + h;
  if (key.includes("w")) { x = Math.min(point.x, right - 1); w = right - x; }
  if (key.includes("e")) { w = Math.max(1, point.x - x); }
  if (key.includes("n")) { y = Math.min(point.y, bottom - 1); h = bottom - y; }
  if (key.includes("s")) { h = Math.max(1, point.y - y); }
  return { x, y, w, h };
}

function clampToImage(point) {
  const px = record.report.dimensionsPx;
  return { x: Math.max(0, Math.min(px.width, point.x)), y: Math.max(0, Math.min(px.height, point.y)) };
}

stage.addEventListener("pointerdown", (event) => {
  if (!record || !source) return;
  if (event.target === textEditor) return;
  if (event.button === 1 || (event.button === 0 && (tool === "pan" || spaceHeld))) {
    gesture = { kind: "pan", startX: event.clientX, startY: event.clientY, left: stage.scrollLeft, top: stage.scrollTop };
    stage.classList.add("panning");
    stage.setPointerCapture(event.pointerId);
    event.preventDefault();
    return;
  }
  if (event.button !== 0) return;
  commitTextEditor();
  // A press on the canvas leaves any toolbar field, so keys go back to the editor shortcuts.
  if (document.activeElement && document.activeElement !== document.body && document.activeElement !== textEditor) document.activeElement.blur();
  const point = screenToImage(event.clientX, event.clientY);
  stage.setPointerCapture(event.pointerId);
  event.preventDefault();

  if (tool === "crop") {
    const px = record.report.dimensionsPx;
    const current = normalizeRect(cropDraft || edits.crop || { x: 0, y: 0, w: px.width, h: px.height });
    const handle = handleAt(event.clientX, event.clientY, rectHandles(current));
    const inside = point.x > current.x && point.x < current.x + current.w && point.y > current.y && point.y < current.y + current.h;
    if (handle) gesture = { kind: "crop-resize", key: handle.key, start: current };
    else if (inside && (cropDraft || edits.crop)) gesture = { kind: "crop-move", origin: point, start: current };
    else gesture = { kind: "crop-new", origin: clampToImage(point) };
    return;
  }

  if (tool === "select") {
    const a = selected();
    const handle = a ? handleAt(event.clientX, event.clientY, handlesFor(a)) : null;
    if (handle) {
      gesture = { kind: "resize", key: handle.key, start: structuredClone(a), before: snapshot() };
      return;
    }
    const hit = topmostAt(point);
    selectedId = hit?.id || null;
    syncContext();
    if (hit) {
      if (event.detail >= 2 && hit.type === "text") { openTextEditor(hit); return; }
      gesture = { kind: "move", origin: point, start: structuredClone(hit), before: snapshot(), moved: false };
    }
    requestRender();
    return;
  }

  if (tool === "eraser") {
    gesture = { kind: "erase", before: snapshot(), erased: false };
    eraseAt(point);
    return;
  }

  if (tool === "text") {
    const hit = topmostAt(point);
    if (hit?.type === "text") openTextEditor(hit);
    else openTextEditor(null, point);
    return;
  }

  if (tool === "step") {
    const n = nextStepNumber();
    const mark = { id: newId(), type: "step", x: point.x, y: point.y, n, color: style.color, fontSize: style.fontSize };
    commit(() => { edits.annotations.push(mark); });
    if (Number.isFinite(style.nextStep)) style.nextStep = n + 1;
    syncContext();
    return;
  }

  const base = { id: newId(), type: tool, color: style.color, width: style.width, dash: style.dash };
  if (tool === "pen" || tool === "highlighter") {
    draft = { ...base, width: tool === "highlighter" ? Math.max(12, style.width * 4) : style.width, points: [[point.x, point.y]] };
  } else if (tool === "line" || tool === "arrow") {
    draft = { ...base, x1: point.x, y1: point.y, x2: point.x, y2: point.y };
  } else if (tool === "rect" || tool === "ellipse") {
    draft = { ...base, x: point.x, y: point.y, w: 0, h: 0, fill: style.fill };
  } else if (tool === "redact") {
    draft = { id: base.id, type: "redact", x: point.x, y: point.y, w: 0, h: 0, redactStyle: style.redactStyle };
  } else if (tool === "spotlight") {
    draft = { id: base.id, type: "spotlight", x: point.x, y: point.y, w: 0, h: 0, spotlightShape: style.spotlightShape };
  }
  gesture = { kind: "draw", origin: point };
  requestRender();
});

stage.addEventListener("pointermove", (event) => {
  if (!gesture) {
    if (tool === "select" && record && source) {
      const over = topmostAt(screenToImage(event.clientX, event.clientY));
      stage.classList.toggle("over-mark", Boolean(over));
    }
    return;
  }
  if (gesture.kind === "pan") {
    stage.scrollLeft = gesture.left - (event.clientX - gesture.startX);
    stage.scrollTop = gesture.top - (event.clientY - gesture.startY);
    return;
  }
  const point = screenToImage(event.clientX, event.clientY);
  switch (gesture.kind) {
    case "draw": {
      if (!draft) break;
      if (draft.points) {
        const last = draft.points.at(-1);
        if (Math.hypot(point.x - last[0], point.y - last[1]) * zoom >= 1.5) draft.points.push([point.x, point.y]);
      } else if ("x1" in draft) {
        const p = constrainPoint(gesture.origin, point, event.shiftKey, "angle");
        draft.x2 = p.x; draft.y2 = p.y;
      } else {
        const p = constrainPoint(gesture.origin, point, event.shiftKey, "square");
        draft.w = p.x - gesture.origin.x;
        draft.h = p.y - gesture.origin.y;
      }
      break;
    }
    case "move": {
      const dx = point.x - gesture.origin.x;
      const dy = point.y - gesture.origin.y;
      if (Math.hypot(dx, dy) * zoom > 2) gesture.moved = true;
      if (gesture.moved) replaceAnnotation(translateAnnotation(gesture.start, dx, dy));
      break;
    }
    case "resize": {
      const a = gesture.start;
      if (gesture.key === "p1") replaceAnnotation({ ...a, x1: point.x, y1: point.y });
      else if (gesture.key === "p2") replaceAnnotation({ ...a, x2: point.x, y2: point.y });
      else replaceAnnotation({ ...a, ...resizeRect(normalizeRect(a), gesture.key, point) });
      break;
    }
    case "erase":
      eraseAt(point);
      break;
    case "crop-new": {
      const p = clampToImage(point);
      cropDraft = { x: Math.min(gesture.origin.x, p.x), y: Math.min(gesture.origin.y, p.y), w: Math.abs(p.x - gesture.origin.x), h: Math.abs(p.y - gesture.origin.y) };
      break;
    }
    case "crop-move": {
      const px = record.report.dimensionsPx;
      const s = gesture.start;
      const x = Math.max(0, Math.min(px.width - s.w, s.x + point.x - gesture.origin.x));
      const y = Math.max(0, Math.min(px.height - s.h, s.y + point.y - gesture.origin.y));
      cropDraft = { ...s, x, y };
      break;
    }
    case "crop-resize":
      cropDraft = resizeRect(gesture.start, gesture.key, clampToImage(point));
      break;
    default:
      break;
  }
  requestRender();
});

function endGesture(event) {
  if (!gesture) return;
  // The text box stays open after the click that placed it; it ends on commit.
  if (gesture.kind === "text") {
    try { stage.releasePointerCapture(event.pointerId); } catch { /* already released */ }
    return;
  }
  const ended = gesture;
  gesture = null;
  stage.classList.remove("panning");
  try { stage.releasePointerCapture(event.pointerId); } catch { /* already released */ }
  if (ended.redactTimer) clearTimeout(ended.redactTimer);
  if (ended.kind === "draw" && draft) {
    const mark = draft;
    draft = null;
    const b = annotationBounds(mark);
    const tiny = mark.points ? false : (b.w * zoom < 3 && b.h * zoom < 3);
    if (!tiny) {
      commit(() => { edits.annotations.push(mark); });
      selectedId = mark.type === "pen" || mark.type === "highlighter" ? null : mark.id;
    }
    syncContext();
  } else if ((ended.kind === "move" && ended.moved) || ended.kind === "resize" || (ended.kind === "erase" && ended.erased)) {
    // The live edits already moved; record one undo step for the whole drag.
    const after = snapshot();
    if (after !== ended.before) {
      undoStack.push(ended.before);
      redoStack.length = 0;
      edits = normalizeEdits(edits, record.report.dimensionsPx);
      changed();
    }
    syncContext();
  } else if (ended.kind === "crop-new" && cropDraft && (cropDraft.w * zoom < 4 || cropDraft.h * zoom < 4)) {
    cropDraft = null;
  }
  requestRender();
}

// Pressing the canvas must not move focus: it would blur (and close) a text box opened by
// that same press, and send the typed text to the page shortcuts instead.
stage.addEventListener("mousedown", (event) => { if (event.target !== textEditor) event.preventDefault(); });
stage.addEventListener("pointerup", endGesture);
stage.addEventListener("pointercancel", endGesture);

function eraseAt(point) {
  const hit = topmostAt(point);
  if (!hit) return;
  edits.annotations = edits.annotations.filter((a) => a.id !== hit.id);
  if (selectedId === hit.id) selectedId = null;
  gesture.erased = true;
  requestRender();
}

stage.addEventListener("wheel", (event) => {
  if (!event.ctrlKey) return;
  event.preventDefault();
  fitted = false;
  setZoom(zoom * Math.exp(-event.deltaY * 0.0015), event);
}, { passive: false });

stage.addEventListener("scroll", () => {
  positionTextEditor();
  requestRender();
});

new ResizeObserver(() => {
  if (!record || !source) return;
  if (fitted) setZoom(fitZoom());
  else layout();
  requestRender();
}).observe(stage);

// ---- text editing -----------------------------------------------------------

function styleTextEditor() {
  const a = gesture?.textDraft;
  if (!a) return;
  const size = (a.fontSize || 28) * zoom;
  const weight = a.textStyle === "plain" ? 500 : 700;
  textEditor.style.font = `${weight} ${size}px "Segoe UI", system-ui, Arial, sans-serif`;
  textEditor.style.color = a.color;
  textEditor.style.lineHeight = "1.25";
}

function positionTextEditor() {
  const a = gesture?.textDraft;
  if (!a || textEditor.hidden) return;
  const s = imageToScreen(a.x, a.y);
  textEditor.style.left = `${s.x + stage.scrollLeft}px`;
  textEditor.style.top = `${s.y + stage.scrollTop}px`;
  const lines = textEditor.value.split("\n");
  textEditor.rows = Math.max(1, lines.length);
  const m = textMetrics({ ...a, text: textEditor.value || " " });
  textEditor.style.width = `${Math.max(60, m.width * zoom + 24)}px`;
  textEditor.style.height = `${m.height * zoom + 4}px`;
}

function openTextEditor(existing, point = null) {
  const a = existing
    ? { ...existing, textStyle: existing.textStyle || style.textStyle }
    : { id: newId(), type: "text", x: point.x, y: point.y - (style.fontSize * 0.6), text: "", color: style.color, fontSize: style.fontSize, textStyle: style.textStyle };
  if (existing) selectedId = existing.id;
  gesture = { kind: "text", textDraft: a, editingText: existing?.id || null, isNew: !existing };
  textEditor.hidden = false;
  textEditor.value = a.text;
  styleTextEditor();
  positionTextEditor();
  syncContext();
  requestRender();
  // Focus now so the first keystroke lands in the box; the stage's mousedown no longer
  // moves focus (see below), and the deferred call covers a double-click's second press.
  textEditor.focus();
  setTimeout(() => { if (!textEditor.hidden) textEditor.focus(); }, 0);
}

function commitTextEditor() {
  if (!gesture || gesture.kind !== "text") return;
  const { textDraft, isNew } = gesture;
  const text = textEditor.value.replace(/\s+$/, "");
  gesture = null;
  textEditor.hidden = true;
  textEditor.blur();
  const latest = { ...textDraft, color: style.color && isNew ? textDraft.color : textDraft.color, text };
  commit(() => {
    if (!text) edits.annotations = edits.annotations.filter((a) => a.id !== latest.id);
    else if (isNew) edits.annotations.push(latest);
    else replaceAnnotation(latest);
  });
  if (text) selectedId = latest.id;
  syncContext();
  requestRender();
}

textEditor.addEventListener("input", positionTextEditor);
textEditor.addEventListener("keydown", (event) => {
  event.stopPropagation();
  if (event.key === "Escape") {
    textEditor.value = gesture?.isNew ? "" : gesture?.textDraft?.text || "";
    commitTextEditor();
  } else if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
    commitTextEditor();
  }
});
textEditor.addEventListener("blur", () => setTimeout(commitTextEditor, 0));

// When the selected text's style changes while editing, keep the box in step.
const originalApplyStyle = applyStyle;
void originalApplyStyle;

// ---- keyboard -------------------------------------------------------------

document.addEventListener("keydown", (event) => {
  if (!record || !source) return;
  const typing = event.target.closest?.("input, select, textarea, dialog");
  const ctrl = event.ctrlKey || event.metaKey;
  if (ctrl && !typing) {
    const key = event.key.toLowerCase();
    if (key === "z" && !event.shiftKey) { event.preventDefault(); undo(); return; }
    if ((key === "z" && event.shiftKey) || key === "y") { event.preventDefault(); redo(); return; }
    if (key === "s") { event.preventDefault(); document.querySelector("#save-png").click(); return; }
    if (key === "c" && !window.getSelection()?.toString()) { event.preventDefault(); document.querySelector("#copy-clipboard").click(); return; }
    if (key === "p") { event.preventDefault(); document.querySelector("#print-capture").click(); return; }
    if (key === "d" && selected()) {
      event.preventDefault();
      const copy = { ...translateAnnotation(selected(), 16 / zoom, 16 / zoom), id: newId() };
      commit(() => edits.annotations.push(copy));
      selectedId = copy.id;
      syncContext();
      return;
    }
    if (key === "0") { event.preventDefault(); fitted = false; setZoom(1); return; }
    if (key === "1") { event.preventDefault(); fit(); return; }
    if (key === "=" || key === "+") { event.preventDefault(); fitted = false; setZoom(zoom * 1.25); return; }
    if (key === "-") { event.preventDefault(); fitted = false; setZoom(zoom / 1.25); return; }
    return;
  }
  if (typing) return;
  if (event.key === " ") { spaceHeld = true; stage.classList.add("panning"); event.preventDefault(); return; }
  if (event.key === "Escape") {
    if (tool === "crop") { setTool("select"); return; }
    if (draft) { draft = null; gesture = null; requestRender(); return; }
    selectedId = null;
    syncContext();
    requestRender();
    return;
  }
  if (event.key === "Enter" && tool === "crop") { applyCrop(); return; }
  if ((event.key === "Delete" || event.key === "Backspace") && selected()) { event.preventDefault(); deleteSelected(); return; }
  if (event.key.startsWith("Arrow") && selected()) {
    event.preventDefault();
    const step = (event.shiftKey ? 10 : 1) / Math.min(1, zoom);
    const dx = event.key === "ArrowLeft" ? -step : event.key === "ArrowRight" ? step : 0;
    const dy = event.key === "ArrowUp" ? -step : event.key === "ArrowDown" ? step : 0;
    const a = selected();
    commit(() => replaceAnnotation(translateAnnotation(a, dx, dy)));
    return;
  }
  if (!event.altKey && !event.shiftKey) {
    const next = KEY_TOOLS[event.key.toLowerCase()];
    if (next) { setTool(next); event.preventDefault(); }
  }
});
document.addEventListener("keyup", (event) => {
  if (event.key === " ") { spaceHeld = false; stage.classList.remove("panning"); }
});

// ---- resize dialog ----------------------------------------------------------

const resizeDialog = document.querySelector("#resize-dialog");
const resizeWidth = document.querySelector("#resize-width");
const resizeHeight = document.querySelector("#resize-height");
const resizePercent = document.querySelector("#resize-percent");

function runAction(action) {
  if (action !== "resize") return;
  const size = outputSize(record, edits);
  const base = edits.crop || { w: record.report.dimensionsPx.width, h: record.report.dimensionsPx.height };
  resizeWidth.value = String(size.width);
  resizeHeight.value = String(size.height);
  resizePercent.value = String(Math.round(edits.scale * 100));
  const sync = (from) => {
    let scale = edits.scale;
    if (from === "w") scale = Number(resizeWidth.value) / base.w;
    if (from === "h") scale = Number(resizeHeight.value) / base.h;
    if (from === "p") scale = Number(resizePercent.value) / 100;
    if (!Number.isFinite(scale) || scale <= 0) return;
    scale = Math.min(4, Math.max(0.05, scale));
    if (from !== "w") resizeWidth.value = String(Math.round(base.w * scale));
    if (from !== "h") resizeHeight.value = String(Math.round(base.h * scale));
    if (from !== "p") resizePercent.value = String(Math.round(scale * 100));
    resizeDialog.dataset.scale = String(scale);
  };
  resizeDialog.dataset.scale = String(edits.scale);
  resizeWidth.oninput = () => sync("w");
  resizeHeight.oninput = () => sync("h");
  resizePercent.oninput = () => sync("p");
  resizeDialog.onclose = () => {
    if (resizeDialog.returnValue === "ok") commit(() => { edits.scale = Number(resizeDialog.dataset.scale) || 1; });
    if (resizeDialog.returnValue === "reset") commit(() => { edits.scale = 1; });
    showToast(`Saved images will be ${outputSize(record, edits).width} × ${outputSize(record, edits).height} px.`);
  };
  resizeDialog.showModal();
}

// ---- exports ---------------------------------------------------------------

async function effective(onProgress = () => {}) {
  await persistEdits();
  const withEdits = { ...record, edits };
  return await flattenEdits(withEdits, tiles, onProgress);
}

function fileName(extension) {
  return `${record.fileName || "winshot-capture"}.${extension}`;
}

async function runExport(button, label, extension, exporter, saveOptions = {}) {
  exportButtons.forEach((item) => { item.disabled = true; });
  const original = button.textContent;
  try {
    button.textContent = `${label} 0%`;
    const flat = await effective((progress) => { button.textContent = `${label} ${Math.round(progress * 30)}%`; });
    const blob = await exporter(flat.record, flat.tiles, (progress) => { button.textContent = `${label} ${Math.round(30 + progress * 70)}%`; });
    await saveBlob(blob, fileName(extension), settings, saveOptions);
    const size = blob.size >= 1024 * 1024 ? `${(blob.size / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(blob.size / 1024))} KB`;
    showToast(`${label} saved (${size}).`);
    return blob;
  } catch (error) {
    showToast(error.message);
    return null;
  } finally {
    button.textContent = original;
    exportButtons.forEach((item) => { item.disabled = false; });
  }
}

const pdfExporter = (capture, captureTiles, onProgress) => exportPdf(capture, captureTiles, onProgress, settings.pdf);

document.querySelector("#save-png").addEventListener("click", (event) => runExport(event.currentTarget, "PNG", "png", exportPng));
document.querySelector("#save-jpeg").addEventListener("click", (event) => runExport(event.currentTarget, "JPEG", "jpg", exportJpeg));
document.querySelector("#save-webp").addEventListener("click", (event) => runExport(event.currentTarget, "WebP", "webp", exportWebp));
document.querySelector("#save-pdf").addEventListener("click", async (event) => {
  // currentTarget is cleared once the handler awaits, so keep the button first.
  const button = event.currentTarget;
  settings = await saveSettings({ ...settings, pdf: readPdfSettings() });
  await runExport(button, "PDF", "pdf", pdfExporter);
});

async function copyToClipboard() {
  const button = document.querySelector("#copy-clipboard");
  const original = button.textContent;
  try {
    button.textContent = "Copying…";
    // ClipboardItem accepts a Promise<Blob>, which keeps the user activation alive
    // while the PNG is still being encoded.
    const blob = effective().then((flat) => exportPng(flat.record, flat.tiles));
    await navigator.clipboard.write([new ClipboardItem({ "image/png": blob })]);
    showToast("Copied to clipboard.");
    return true;
  } catch (error) {
    showToast(`Copy failed: ${error.message}. Very large captures can exceed the clipboard limit — use Save PNG instead.`);
    return false;
  } finally {
    button.textContent = original;
  }
}

document.querySelector("#copy-clipboard").addEventListener("click", copyToClipboard);

document.querySelector("#print-capture").addEventListener("click", async (event) => {
  const button = event.currentTarget;
  const original = button.textContent;
  try {
    // Print the assembled image, not the tiles — tiles overlap by design.
    button.textContent = "Preparing…";
    const flat = await effective();
    const blob = await exportPng(flat.record, flat.tiles, (progress) => { button.textContent = `Preparing ${Math.round(progress * 100)}%`; });
    const url = URL.createObjectURL(blob);
    objectUrls.push(url);
    let image = document.querySelector("#print-image");
    if (!image) {
      image = document.createElement("img");
      image.id = "print-image";
      document.body.append(image);
    }
    image.src = url;
    await new Promise((resolve, reject) => { image.onload = resolve; image.onerror = () => reject(new Error("The print image failed to load.")); });
    window.print();
  } catch (error) {
    showToast(`Print failed: ${error.message}`);
  } finally {
    button.textContent = original;
  }
});

// Desktop hand-off: save a PNG, then ask WinShot to open that file in its own editor.
document.querySelector("#open-desktop").addEventListener("click", async (event) => {
  const button = event.currentTarget;
  const original = button.textContent;
  exportButtons.forEach((item) => { item.disabled = true; });
  try {
    button.textContent = "Saving…";
    const flat = await effective();
    const blob = await exportPng(flat.record, flat.tiles);
    const item = await saveBlob(blob, fileName("png"), settings, { wait: true, saveAs: false });
    if (!item?.filename) throw new Error("The browser did not report where the file was saved.");
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    await chrome.tabs.update(tab.id, { url: `winshot://edit:${encodeURIComponent(item.filename)}` });
    showToast("Opening in WinShot… If nothing happens, install WinShot desktop or allow Chrome to open it.");
  } catch (error) {
    showToast(`Could not hand off to WinShot: ${error.message}`);
  } finally {
    button.textContent = original;
    exportButtons.forEach((item) => { item.disabled = false; });
  }
});

const pdfNodes = {
  layout: document.querySelector("#pdf-layout"),
  pageSize: document.querySelector("#pdf-page-size"),
  customWidthIn: document.querySelector("#pdf-width"),
  customHeightIn: document.querySelector("#pdf-height"),
  header: document.querySelector("#pdf-header"),
  footer: document.querySelector("#pdf-footer"),
  watermark: document.querySelector("#pdf-watermark")
};

function showPdfSettings() {
  for (const [key, node] of Object.entries(pdfNodes)) node.value = settings.pdf[key];
}

function readPdfSettings() {
  return {
    layout: pdfNodes.layout.value,
    pageSize: pdfNodes.pageSize.value,
    customWidthIn: Number(pdfNodes.customWidthIn.value),
    customHeightIn: Number(pdfNodes.customHeightIn.value),
    header: pdfNodes.header.value,
    footer: pdfNodes.footer.value,
    watermark: pdfNodes.watermark.value
  };
}

showPdfSettings();
for (const node of Object.values(pdfNodes)) {
  node.addEventListener("change", async () => {
    settings = await saveSettings({ ...settings, pdf: readPdfSettings() });
    showPdfSettings();
  });
}

// ---- wiring ------------------------------------------------------------------

paintIcons();
for (const button of document.querySelectorAll("#toolbar [data-tool]")) button.addEventListener("click", () => setTool(button.dataset.tool));
for (const button of document.querySelectorAll("[data-category]")) {
  button.addEventListener("click", () => setTool(lastToolIn[button.dataset.category]));
}
document.querySelector("#undo").addEventListener("click", undo);
document.querySelector("#redo").addEventListener("click", redo);
document.querySelector("#zoom-in").addEventListener("click", () => { fitted = false; setZoom(zoom * 1.25); });
document.querySelector("#zoom-out").addEventListener("click", () => { fitted = false; setZoom(zoom / 1.25); });
document.querySelector("#zoom-label").addEventListener("click", () => {
  if (fitted && Math.abs(zoom - 1) > 0.01) { fitted = false; setZoom(1); } else fit();
});
document.querySelector("#toggle-details").addEventListener("click", () => {
  details.classList.toggle("collapsed");
  requestRender();
});

addEventListener("pagehide", () => {
  persistEdits().catch(() => {});
  objectUrls.forEach((url) => URL.revokeObjectURL(url));
  source?.close();
});

// FireShot-style one-step actions: the background opens the editor with ?then=… when
// Settings says what to do after a capture.
async function runThenAction() {
  if (!thenAction) return;
  if (thenAction === "copy") {
    await copyToClipboard();
    return;
  }
  const button = thenAction === "save-pdf" ? document.querySelector("#save-pdf") : document.querySelector(`#save-${settings.imageFormat === "jpeg" ? "jpeg" : settings.imageFormat === "webp" ? "webp" : "png"}`);
  const exporter = thenAction === "save-pdf" ? pdfExporter : settings.imageFormat === "jpeg" ? exportJpeg : settings.imageFormat === "webp" ? exportWebp : exportPng;
  const extension = thenAction === "save-pdf" ? "pdf" : settings.imageFormat === "jpeg" ? "jpg" : settings.imageFormat;
  const label = extension.toUpperCase();
  const closing = params.get("close") === "1";
  // A tab that closes itself must wait for the download to finish: it owns the blob.
  const blob = await runExport(button, label, extension, exporter, closing ? { wait: true } : {});
  if (blob && closing) {
    const tab = await chrome.tabs.getCurrent();
    if (tab?.id) chrome.tabs.remove(tab.id);
  }
}

try {
  if (!id) throw new Error("Missing capture ID.");
  record = await getRecord(id);
  if (!record) throw new Error("Capture not found. It may have been removed from browser storage.");
  tiles = await getTiles(id);
  renderReport();
  const px = record.report.dimensionsPx;
  if (!px || !tiles.length) {
    headerButtons().forEach((button) => { button.disabled = true; });
    preview.textContent = "No verified pixels are available for export.";
    preview.classList.add("message");
    details.classList.remove("collapsed");
  } else {
    if (!record.report.warnings?.length && !record.error) details.classList.add("collapsed");
    edits = normalizeEdits(record.edits, px);
    source = new TileSource(tiles);
    preview.append(spacer);
    refreshOutputSize();
    syncUndoButtons();
    setTool("select");
    fit();
    runThenAction().catch((error) => showToast(error.message));
  }
} catch (error) {
  title.textContent = "Could not load capture";
  state.textContent = "failed";
  state.className = "pill failed";
  addReport("Error", error.message);
  headerButtons().forEach((button) => { button.disabled = true; });
  details.classList.remove("collapsed");
}

// Test hook: lets the end-to-end suite drive the editor model without synthetic pointer
// choreography. Not reachable from web pages.
globalThis.__winshotEditor = Object.freeze({
  get edits() { return structuredClone(edits); },
  setEdits(next) { commit(() => { edits = normalizeEdits(next, record.report.dimensionsPx); }); },
  setTool,
  get tool() { return tool; },
  get zoom() { return zoom; },
  screenToImage,
  imageToScreen,
  persist: persistEdits
});
