// Private, unsafe-to-copy PNG ancillary chunk. Ordinary PNG exports never use it.
// Editing a PNG in another app may discard this chunk, leaving a regular image.
const SIGNATURE = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]);
const TYPE = "wsPJ";
const MAX_BYTES = 100 * 1024 * 1024;
const table = Uint32Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ c >>> 1 : c >>> 1;
  return c >>> 0;
});
function crc(bytes) {
  let value = 0xffffffff;
  for (const byte of bytes) value = table[(value ^ byte) & 255] ^ value >>> 8;
  return (value ^ 0xffffffff) >>> 0;
}
export { crc as crc32 };
function parse(bytes) {
  if (bytes.length > MAX_BYTES) throw new Error("This PNG is over 100 MB. Open a smaller file.");
  if (bytes.length < 45 || !SIGNATURE.every((b, i) => bytes[i] === b)) throw new Error("This is not a valid PNG image.");
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength), chunks = [];
  let p = 8, seenData = false, seenProject = false;
  while (p + 12 <= bytes.length) {
    if (chunks.length >= 10_000) throw new Error("This PNG has too many chunks to open safely.");
    const length = view.getUint32(p);
    if (length > bytes.length - p - 12) throw new Error("The PNG contains a truncated chunk.");
    const typeBytes = bytes.subarray(p + 4, p + 8), type = String.fromCharCode(...typeBytes);
    if (!/^[A-Za-z]{4}$/.test(type) || type[2] !== type[2].toUpperCase()) throw new Error("The PNG has an invalid chunk type.");
    if (view.getUint32(p + 8 + length) !== crc(bytes.subarray(p + 4, p + 8 + length))) throw new Error("The PNG has a damaged chunk checksum.");
    if (!chunks.length && (type !== "IHDR" || length !== 13) || chunks.length && type === "IHDR") throw new Error("The PNG has an invalid image header.");
    if (type === "IDAT") seenData = true;
    if (type === TYPE) {
      if (seenProject || !length) throw new Error("The PNG has duplicate or empty editable project data.");
      seenProject = true;
    }
    chunks.push({ type, start: p, end: p + length + 12, data: bytes.subarray(p + 8, p + 8 + length) });
    p += length + 12;
    if (type === "IEND") {
      if (length || !seenData || p !== bytes.length) throw new Error("The PNG has an invalid end chunk.");
      return chunks;
    }
  }
  throw new Error("The PNG is incomplete.");
}
export async function readPngProject(blob) {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  const chunk = parse(bytes).find(c => c.type === TYPE);
  if (!chunk) return null;
  try { return new TextDecoder("utf-8", { fatal: true }).decode(chunk.data); }
  catch { throw new Error("The PNG contains invalid editable project text."); }
}
export async function embedPngProject(png, project) {
  if (png.size + project.size + 12 > MAX_BYTES) throw new Error("This editable PNG would exceed 100 MB. Save a regular PNG or a separate project instead.");
  const bytes = new Uint8Array(await png.arrayBuffer()), chunks = parse(bytes);
  if (chunks.some(c => c.type === TYPE)) throw new Error("This PNG already contains editable project data.");
  const data = new Uint8Array(await project.arrayBuffer()), chunk = new Uint8Array(data.length + 12), view = new DataView(chunk.buffer);
  view.setUint32(0, data.length); chunk.set(new TextEncoder().encode(TYPE), 4); chunk.set(data, 8);
  view.setUint32(data.length + 8, crc(chunk.subarray(4, data.length + 8)));
  const end = chunks.at(-1).start;
  return new Blob([bytes.subarray(0, end), chunk, bytes.subarray(end)], { type: "image/png" });
}
