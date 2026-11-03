// Import the deliberately supported subset of ProjectSerializer.cs schema v1.
// No archive paths are written to disk and no annotations are silently omitted.
import { Inflate } from "./vendor/fflate.js";
import { crc32 } from "./png-project.js";
import { importProject } from "./project-file.js";

const LIMIT = 100 * 1024 * 1024;
const unsupported = () => new Error("This Windows project uses annotations or styles that the browser cannot preserve. Export a flattened image from WinShot, or simplify the project first.");
function finite(value, min = -1_000_000, max = 1_000_000) {
  if (!Number.isFinite(value) || value < min || value > max) throw new Error("The Windows project has invalid coordinates.");
  return value;
}
function extras(bytes, start, length) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength), end = start + length;
  for (let p = start; p < end;) {
    if (p + 4 > end) throw new Error("The project ZIP has a damaged extra field.");
    const type = view.getUint16(p, true), size = view.getUint16(p + 2, true); p += 4;
    if (type === 1) throw new Error("ZIP64 Windows projects are not supported.");
    if (p + size > end) throw new Error("The project ZIP has a damaged extra field.");
    p += size;
  }
}

export async function readNativeArchive(file) {
  if (!(file instanceof Blob) || !file.size || file.size > LIMIT) throw new Error("Open a Windows project under 100 MB.");
  const bytes = new Uint8Array(await file.arrayBuffer()), view = new DataView(bytes.buffer);
  let end = -1;
  for (let p = bytes.length - 22; p >= Math.max(0, bytes.length - 65557); p--) {
    if (view.getUint32(p, true) === 0x06054b50 && p + 22 + view.getUint16(p + 20, true) === bytes.length) { end = p; break; }
  }
  if (end < 0) throw new Error("This is not a complete Windows project ZIP.");
  const count = view.getUint16(end + 10, true), centralSize = view.getUint32(end + 12, true), central = view.getUint32(end + 16, true);
  if (count === 65535 || centralSize === 0xffffffff || central === 0xffffffff) throw new Error("ZIP64 Windows projects are not supported.");
  if (view.getUint16(end + 4, true) || view.getUint16(end + 6, true) || view.getUint16(end + 8, true) !== count || central + centralSize !== end) throw new Error("The project ZIP has invalid directory bounds.");
  if (count !== 2) throw unsupported(); // Embedded native image annotations are not supported.
  const entries = new Map(), ranges = []; let p = central, declared = 0;
  for (let i = 0; i < count; i++) {
    if (p + 46 > end || view.getUint32(p, true) !== 0x02014b50) throw new Error("The project ZIP has a damaged directory.");
    const flags = view.getUint16(p + 8, true), method = view.getUint16(p + 10, true), checksum = view.getUint32(p + 16, true), packed = view.getUint32(p + 20, true), size = view.getUint32(p + 24, true);
    const n = view.getUint16(p + 28, true), x = view.getUint16(p + 30, true), c = view.getUint16(p + 32, true), local = view.getUint32(p + 42, true);
    if ([packed, size, local].includes(0xffffffff)) throw new Error("ZIP64 Windows projects are not supported.");
    if (p + 46 + n + x + c > end || view.getUint16(p + 34, true) || flags & ~0x080e || ![0, 8].includes(method)) throw new Error("Encrypted or unsupported project ZIP entries cannot be opened.");
    const nameBytes = bytes.subarray(p + 46, p + 46 + n), name = new TextDecoder("utf-8", { fatal: true }).decode(nameBytes);
    if (!["source.png", "annotations.json"].includes(name) || entries.has(name)) throw new Error("The Windows project has duplicate or unsupported archive paths.");
    extras(bytes, p + 46 + n, x);
    if (size > (name === "annotations.json" ? 8 * 1024 * 1024 : LIMIT) || (declared += size) > LIMIT) throw new Error("The unpacked Windows project is too large.");
    if (local + 30 > central || view.getUint32(local, true) !== 0x04034b50 || view.getUint16(local + 6, true) !== flags || view.getUint16(local + 8, true) !== method) throw new Error("The project ZIP has an invalid local header.");
    const ln = view.getUint16(local + 26, true), lx = view.getUint16(local + 28, true), dataStart = local + 30 + ln + lx;
    if (dataStart + packed > central || ln !== n || !nameBytes.every((b, j) => bytes[local + 30 + j] === b)) throw new Error("The project ZIP has invalid entry bounds.");
    extras(bytes, local + 30 + ln, lx);
    let rangeEnd = dataStart + packed;
    if (!(flags & 8)) {
      if (view.getUint32(local + 14, true) !== checksum || view.getUint32(local + 18, true) !== packed || view.getUint32(local + 22, true) !== size) throw new Error("The project ZIP has inconsistent entry sizes.");
    } else {
      let descriptor = rangeEnd;
      if (descriptor + 4 <= central && view.getUint32(descriptor, true) === 0x08074b50) descriptor += 4;
      if (descriptor + 12 > central || view.getUint32(descriptor, true) !== checksum || view.getUint32(descriptor + 4, true) !== packed || view.getUint32(descriptor + 8, true) !== size) throw new Error("The project ZIP has an invalid data descriptor.");
      rangeEnd = descriptor + 12;
    }
    if (ranges.some(([start, stop]) => local < stop && rangeEnd > start)) throw new Error("The project ZIP contains overlapping entries.");
    ranges.push([local, rangeEnd]);
    entries.set(name, { method, checksum, size, packed: bytes.subarray(dataStart, dataStart + packed) });
    p += 46 + n + x + c;
  }
  if (p !== end || !entries.has("source.png") || !entries.has("annotations.json")) throw new Error("The Windows project is missing its source or annotations.");
  const result = {};
  for (const [name, entry] of entries) {
    let output;
    if (entry.method === 0) output = entry.packed;
    else {
      const parts = []; let total = 0;
      const inflate = new Inflate(chunk => {
        total += chunk.length;
        if (total > entry.size || total > LIMIT) throw new Error("The project ZIP expands beyond its declared size.");
        parts.push(chunk);
      });
      for (let at = 0; at < entry.packed.length; at += 8192) inflate.push(entry.packed.subarray(at, at + 8192), at + 8192 >= entry.packed.length);
      output = new Uint8Array(total); let at = 0; for (const part of parts) { output.set(part, at); at += part.length; }
    }
    if (output.length !== entry.size || crc32(output) !== entry.checksum) throw new Error("The Windows project has damaged image or annotation checksums.");
    result[name] = output;
  }
  return result;
}

export function convertNativeAnnotations(document) {
  if (document?.version !== 1 || !Array.isArray(document.annotations) || document.annotations.length > 10_000) throw new Error("This Windows project version is not supported.");
  let pointCount = 0;
  const items = document.annotations.map((a, index) => {
    if (!a || !["rectangle", "ellipse", "arrow", "freehand", "redact"].includes(a.type)) throw unsupported();
    if ((a.angle ?? 0) !== 0 || a.alpha != null && a.alpha !== 1 || a.sketch && a.sketch !== "Clean" || a.lineStyle && a.lineStyle !== "solid" || a.dash || a.mid || a.startHead && a.startHead !== "none" || a.penAspect != null && a.penAspect !== 1) throw unsupported();
    const tx = finite(a.tx ?? 0), ty = finite(a.ty ?? 0), z = finite(a.z ?? index, 0, 1_000_000);
    const color = a.type === "redact" ? "#000000" : /^#FF[0-9a-f]{6}$/i.test(a.color || "") ? `#${a.color.slice(3)}` : null;
    if (!color) throw unsupported();
    const o = { id: crypto.randomUUID(), type: a.type === "freehand" ? "pen" : a.type, color, stroke: finite(a.thickness ?? (a.type === "redact" ? 1 : NaN), .1, 200) };
    if (["rectangle", "ellipse", "redact"].includes(a.type)) {
      if (!Array.isArray(a.rect) || a.rect.length !== 4) throw unsupported();
      if (a.type === "redact") {
        if (a.style !== "BlackOut") throw unsupported();
        o.redactionColor = "#000000";
      } else if (a.fill && a.fill !== "None" || a.fillColor && !/^#00[0-9a-f]{6}$/i.test(a.fillColor) || a.style) throw unsupported();
      Object.assign(o, { x: finite(a.rect[0] + tx), y: finite(a.rect[1] + ty), width: finite(a.rect[2], 1), height: finite(a.rect[3], 1) });
    } else {
      if (!Array.isArray(a.points) || a.points.length < 2 || a.type === "arrow" && a.points.length !== 2 || (pointCount += a.points.length) > 100_000) throw unsupported();
      if (a.type === "arrow" && (a.head !== "vShape" || a.style && a.style !== "Thin")) throw unsupported();
      if (a.type === "freehand" && (a.head && a.head !== "none" || a.style)) throw unsupported();
      const points = a.points.map(point => {
        if (!Array.isArray(point) || point.length !== 2) throw unsupported();
        return { x: finite(point[0] + tx), y: finite(point[1] + ty) };
      });
      Object.assign(o, { x: points[0].x, y: points[0].y, width: finite(points.at(-1).x - points[0].x), height: finite(points.at(-1).y - points[0].y) });
      if (a.type === "freehand") o.points = points;
    }
    return { o, z, index };
  });
  return items.sort((a, b) => a.z - b.z || a.index - b.index).map(({ o }) => o);
}

export async function importNativeProject(file) {
  const entries = await readNativeArchive(file), png = entries["source.png"];
  if (png.length < 24 || ![137,80,78,71,13,10,26,10].every((b, i) => png[i] === b)) throw new Error("The Windows project source is not a PNG.");
  const view = new DataView(png.buffer, png.byteOffset, png.byteLength), width = view.getUint32(16), height = view.getUint32(20);
  let document;
  try { document = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(entries["annotations.json"])); }
  catch { throw new Error("The Windows project has damaged annotation text."); }
  const objects = convertNativeAnnotations(document);
  let encoded = "";
  for (let at = 0; at < png.length; at += 32768) encoded += String.fromCharCode(...png.subarray(at, at + 32768));
  const project = { format: "winshot-browser-project", version: 1, title: file.name || "Windows project", fileName: (file.name || "winshot-project").replace(/\.winshot$/i, ""), width, height, editorDocument: { version: 1, crop: { x: 0, y: 0, width, height }, objects }, tiles: [{ index: 0, destXPx: 0, destYPx: 0, widthPx: width, heightPx: height, png: btoa(encoded) }] };
  if (objects.some(o => o.type === "arrow")) project.warnings = ["Native arrow endpoints and stroke are editable; arrowheads use the browser editor's rendering."];
  return await importProject(new Blob([JSON.stringify(project)], { type: "application/json" }));
}
