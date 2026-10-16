import { putImportedCapture } from "./store.js";
import { validateCoverage } from "./contract.js";

const MAX_FILE_BYTES = 100 * 1024 * 1024;
const MAX_PIXELS = 100_000_000;
const TYPES = new Set(["arrow", "rectangle", "ellipse", "pen", "text", "redact"]);

function number(value, min, max) {
  if (!Number.isFinite(value) || value < min || value > max) throw new Error("The project contains invalid image or edit coordinates.");
  return value;
}

function dimension(value) {
  if (!Number.isInteger(value)) throw new Error("The project has invalid image dimensions.");
  return number(value, 1, 200_000);
}

function imageExtent(width, height) {
  dimension(width); dimension(height);
  if (width * height > MAX_PIXELS) throw new Error("This image is too large for an editable project. Save PNG or PDF instead.");
}

function rasterGeometry(tiles, width, height) {
  if (!Array.isArray(tiles) || !tiles.length || tiles.length > 1000) throw new Error("The project has an invalid image tile list.");
  const indexes = new Set(); let pixels = 0;
  for (const t of tiles) {
    if (!Number.isInteger(t.index) || t.index < 0 || indexes.has(t.index)) throw new Error("The project has duplicate or invalid image tiles.");
    indexes.add(t.index);
    const w = dimension(t.widthPx), h = dimension(t.heightPx);
    const x = number(t.destXPx, 0, width - 1), y = number(t.destYPx, 0, height - 1);
    if (!Number.isInteger(x) || !Number.isInteger(y) || x + w > width || y + h > height || (pixels += w * h) > MAX_PIXELS) throw new Error("The project image is too large or has tiles outside its bounds.");
  }
  return validateCoverage(width, height, tiles.map((t) => ({ destX: t.destXPx, destY: t.destYPx, widthCss: t.widthPx, heightCss: t.heightPx })), 0);
}

function pngDimensions(bytes, width, height) {
  if (bytes.length < 24 || ![137,80,78,71,13,10,26,10].every((v,i) => bytes[i] === v)) throw new Error("A project tile is not a PNG image.");
  const head = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (head.getUint32(16) !== width || head.getUint32(20) !== height) throw new Error("The project tile dimensions do not match its image.");
}

function snapshot(value, width, height) {
  if (!value || !Array.isArray(value.objects) || value.objects.length > 10_000) throw new Error("The project has invalid annotations.");
  const c = value.crop;
  if (!c) throw new Error("The project has no crop information.");
  if (![c.x, c.y, c.width, c.height].every(Number.isInteger)) throw new Error("The project crop must use whole image pixels.");
  const crop = { x: number(c.x, 0, width - 1), y: number(c.y, 0, height - 1), width: number(c.width, 1, width), height: number(c.height, 1, height) };
  if (crop.x + crop.width > width + .01 || crop.y + crop.height > height + .01) throw new Error("The crop extends outside the source image.");
  let pointCount = 0;
  const ids = new Set();
  const objects = value.objects.map((o) => {
    if (!o || !TYPES.has(o.type) || !/^#[0-9a-f]{6}$/i.test(o.color)) throw new Error("The project contains an unsupported annotation.");
    const id = typeof o.id === "string" && o.id.length > 0 ? o.id.slice(0, 100) : crypto.randomUUID();
    if (ids.has(id)) throw new Error("The project contains duplicate annotation identities.");
    ids.add(id);
    const result = { id, type: o.type, color: o.color,
      x: number(o.x, -1_000_000, 1_000_000), y: number(o.y, -1_000_000, 1_000_000),
      width: number(o.width, -1_000_000, 1_000_000), height: number(o.height, -1_000_000, 1_000_000), stroke: number(o.stroke, .1, 200) };
    if (o.type === "pen") {
      if (!Array.isArray(o.points) || o.points.length < 2 || (pointCount += o.points.length) > 100_000) throw new Error("The project has invalid pen points.");
      result.points = o.points.map((p) => ({ x: number(p.x, -1_000_000, 1_000_000), y: number(p.y, -1_000_000, 1_000_000) }));
    }
    if (o.type === "text") {
      if (typeof o.text !== "string" || o.text.length > 10_000) throw new Error("The project has invalid text.");
      result.text = o.text; result.fontSize = number(o.fontSize, 1, 1000);
    }
    return result;
  });
  return { crop, objects };
}

export function validateEditorDocument(value, width, height) {
  if (!value) return { version: 1, crop: { x: 0, y: 0, width, height }, objects: [], past: [], future: [] };
  if (value.version !== 1) throw new Error("This project was created by a newer editor. Update WinShot to open it.");
  const current = snapshot(value, width, height);
  // Undo history is session-local. Imported projects preserve editable objects,
  // without retaining old redactions/crops or allocating unbounded old snapshots.
  return { version: 1, ...current, past: [], future: [] };
}

function checkFile(file) {
  if (!(file instanceof Blob) || file.size === 0) throw new Error("Choose a non-empty image or WinShot project.");
  if (file.size > MAX_FILE_BYTES) throw new Error("This file is over 100 MB. Open a smaller file.");
}

function base64(bytes) {
  let text = "";
  for (let i = 0; i < bytes.length; i += 32_768) text += String.fromCharCode(...bytes.subarray(i, i + 32_768));
  return btoa(text);
}

function decodePng(encoded) {
  if (typeof encoded !== "string" || encoded.length > MAX_FILE_BYTES || !/^[A-Za-z0-9+/]*={0,2}$/.test(encoded)) throw new Error("The project contains invalid image data.");
  let bytes;
  try { bytes = Uint8Array.from(atob(encoded), (c) => c.charCodeAt(0)); }
  catch { throw new Error("The project contains invalid image data."); }
  if (bytes.length < 24 || ![137,80,78,71,13,10,26,10].every((v,i) => bytes[i] === v)) throw new Error("A project tile is not a PNG image.");
  return new Blob([bytes], { type: "image/png" });
}

export async function saveProject(record, tiles) {
  const { width, height } = record.report.dimensionsPx;
  imageExtent(width, height);
  const coverage = rasterGeometry(tiles, width, height);
  const document = validateEditorDocument(record.editorDocument, width, height);
  const encoded = [];
  let bytes = 0;
  for (const tile of tiles) {
    bytes += tile.blob.size;
    if (bytes * 4 / 3 > MAX_FILE_BYTES - 1_000_000) throw new Error("This capture is too large for a 100 MB editable project. Save PNG or PDF instead.");
    const data = new Uint8Array(await tile.blob.arrayBuffer());
    pngDimensions(data, tile.widthPx, tile.heightPx);
    encoded.push({ index: tile.index, destXPx: tile.destXPx, destYPx: tile.destYPx, widthPx: tile.widthPx, heightPx: tile.heightPx, png: base64(data) });
  }
  const sourceState = record.state === "cancelled" ? "cancelled" : record.state !== "complete" || !coverage.complete ? "partial" : "complete";
  const warnings = (record.report.warnings || []).filter(x => typeof x === "string").slice(0, 29);
  if (record.state === "failed") warnings.push(record.error || "The original capture reported a failure. Review its retained pixels before using it.");
  const blob = new Blob([JSON.stringify({ format: "winshot-browser-project", version: 1, title: record.sourceTitle, fileName: record.fileName, width, height, state: sourceState, warnings, editorDocument: document, tiles: encoded })], { type: "application/json" });
  if (blob.size > MAX_FILE_BYTES) throw new Error("This editable project is over 100 MB. Save PNG or PDF instead.");
  return blob;
}

async function storeImported(title, fileName, width, height, tiles, editorDocument, { state = "complete", warnings = [], coverage = { ratio: 1, complete: true } } = {}) {
  const id = crypto.randomUUID(), stamp = new Date().toISOString();
  const record = { id, sourceTitle: String(title || "Imported image").slice(0, 500), sourceUrl: "", fileName: String(fileName || "winshot-image").replace(/[<>:"/\\|?*\x00-\x1f]/g, "-").slice(0, 160), mode: "import", state, startedAt: stamp, finishedAt: stamp,
    report: { mode: "import", target: "image", dimensionsCss: { width, height }, dimensionsPx: { width, height }, tileCount: tiles.length, coverage, restoration: "not applicable", warnings }, editorDocument };
  await putImportedCapture(record, tiles);
  return record;
}

export async function importProject(file) {
  checkFile(file);
  let data;
  try { data = JSON.parse(await file.text()); } catch { throw new Error("This is not a valid WinShot project file."); }
  if (data?.format !== "winshot-browser-project" || data.version !== 1) throw new Error("This is not a supported WinShot browser project.");
  const width = dimension(data.width), height = dimension(data.height);
  imageExtent(width, height);
  const editorDocument = validateEditorDocument(data.editorDocument, width, height);
  const coverage = rasterGeometry(data.tiles, width, height), tiles = [];
  const state = data.state === undefined ? "complete" : data.state;
  if (!["complete", "partial", "cancelled"].includes(state)) throw new Error("The project has an invalid capture state.");
  if (!coverage.complete && state === "complete") throw new Error("The project image has missing tile coverage.");
  for (const t of data.tiles) {
    const blob = decodePng(t.png);
    pngDimensions(new Uint8Array(await blob.slice(0, 24).arrayBuffer()), t.widthPx, t.heightPx);
    let bitmap;
    try { bitmap = await createImageBitmap(blob); }
    catch { throw new Error("The project contains a damaged PNG image."); }
    bitmap.close();
    tiles.push({ index: t.index, blob, destXPx: t.destXPx, destYPx: t.destYPx, widthPx: t.widthPx, heightPx: t.heightPx });
  }
  const warnings = Array.isArray(data.warnings) ? data.warnings.filter(x => typeof x === "string").slice(0, 30).map(x => x.slice(0, 1000)) : [];
  if (!coverage.complete) warnings.push("This project preserves a partial capture. Missing image areas are not verified pixels.");
  return storeImported(data.title, data.fileName, width, height, tiles, editorDocument, { state, warnings, coverage });
}

export async function importImage(file) {
  checkFile(file);
  if (!/^image\/(png|jpeg|webp|gif|bmp)$/.test(file.type)) throw new Error("Open a PNG, JPEG, WebP, GIF or BMP image. Use Video studio for recordings.");
  await preflightImage(file);
  let bitmap;
  try { bitmap = await createImageBitmap(file); } catch { throw new Error("This image could not be decoded. Try saving it as PNG."); }
  try {
    const { width, height } = bitmap;
    if (width > 200_000 || height > 200_000 || width * height > MAX_PIXELS) throw new Error("This image is too large to edit in the browser. Open a smaller image.");
    const tiles = [];
    for (let y = 0; y < height; y += 2048) for (let x = 0; x < width; x += 2048) {
      const w = Math.min(2048, width - x), h = Math.min(2048, height - y), canvas = new OffscreenCanvas(w, h);
      canvas.getContext("2d").drawImage(bitmap, -x, -y);
      tiles.push({ index: tiles.length, destXPx: x, destYPx: y, widthPx: w, heightPx: h, blob: await canvas.convertToBlob({ type: "image/png" }) });
    }
    const name = file.name || "Clipboard image";
    return await storeImported(name, name.replace(/\.[^.]+$/, ""), width, height, tiles, validateEditorDocument(null, width, height));
  } finally { bitmap.close(); }
}

// Check dimensions from the file header before asking Chrome to allocate a full
// bitmap. A tiny, highly compressed image can still describe billions of pixels.
async function preflightImage(file) {
  const data = new Uint8Array(await file.slice(0, 2 * 1024 * 1024).arrayBuffer()), view = new DataView(data.buffer);
  const tag = (offset, text) => data.length >= offset + text.length && [...text].every((c, i) => data[offset + i] === c.charCodeAt(0));
  let size;
  if (data.length >= 24 && data[0] === 137 && tag(1, "PNG\r\n\x1a\n")) size = [view.getUint32(16), view.getUint32(20)];
  else if (data.length >= 10 && (tag(0,"GIF87a") || tag(0,"GIF89a"))) size = [view.getUint16(6,true), view.getUint16(8,true)];
  else if (data.length >= 26 && tag(0,"BM")) size = view.getUint32(14,true) === 12 ? [view.getUint16(18,true), view.getUint16(20,true)] : [view.getInt32(18,true), Math.abs(view.getInt32(22,true))];
  else if (data.length >= 4 && data[0] === 255 && data[1] === 216) {
    const frame = new Set([0xc0,0xc1,0xc2,0xc3,0xc5,0xc6,0xc7,0xc9,0xca,0xcb,0xcd,0xce,0xcf]);
    for (let p = 2; p + 4 <= data.length;) {
      if (data[p++] !== 255) break;
      while (p < data.length && data[p] === 255) p++;
      const marker = data[p++];
      if (marker === 0xd9 || marker === 0xda) break;
      if (marker === 0x01 || marker >= 0xd0 && marker <= 0xd7) continue;
      if (p + 2 > data.length) break;
      const length = view.getUint16(p);
      if (length < 2 || p + length > data.length) break;
      if (frame.has(marker) && length >= 7) { size = [view.getUint16(p+5),view.getUint16(p+3)]; break; }
      p += length;
    }
  } else if (data.length >= 30 && tag(0,"RIFF") && tag(8,"WEBP")) {
    const u24 = p => data[p] | data[p+1] << 8 | data[p+2] << 16;
    for (let p = 12; p + 8 <= data.length;) {
      const length = view.getUint32(p+4,true), start = p+8;
      if (tag(p,"VP8X") && length >= 10 && start+10<=data.length) { size = [1+u24(start+4),1+u24(start+7)]; break; }
      if (tag(p,"VP8L") && length >= 5 && start+5<=data.length && data[start]===0x2f) { const bits = view.getUint32(start+1,true); size = [1+(bits&0x3fff),1+((bits>>>14)&0x3fff)]; break; }
      if (tag(p,"VP8 ") && length >= 10 && start+10<=data.length && data[start+3]===0x9d && data[start+4]===1 && data[start+5]===0x2a) { size = [view.getUint16(start+6,true)&0x3fff,view.getUint16(start+8,true)&0x3fff]; break; }
      p = start + length + (length & 1);
    }
  }
  if (!size) throw new Error("The image header could not be read. Save it as a standard PNG or JPEG, then open it again.");
  imageExtent(...size);
}
