// Store already-compressed image/PDF blobs directly in one portable ZIP. CRC is
// streamed so a large batch does not need a second full copy of each raster.
const table = Uint32Array.from({ length: 256 }, (_, value) => {
  for (let bit = 0; bit < 8; bit++) value = (value >>> 1) ^ ((value & 1) ? 0xedb88320 : 0);
  return value >>> 0;
});
export async function archiveFiles(files) {
  const local = [], directory = []; let offset = 0;
  if (files.length > 20) throw new Error("A batch archive supports at most 20 captures.");
  for (const file of files) {
    if (typeof file.name !== "string" || !file.name || /[\\/\x00-\x1f]/.test(file.name) || file.name === "." || file.name === ".." || new TextEncoder().encode(file.name).length > 65535 || !(file.blob instanceof Blob)) throw new Error("The batch contains an invalid file name or image.");
    if (offset + file.blob.size > 512 * 1024 * 1024) throw new Error("This batch exceeds the 512 MB archive limit. Save its captures individually from history.");
    const name = new TextEncoder().encode(file.name), size = file.blob.size;
    let crc = 0xffffffff;
    const reader = file.blob.stream().getReader();
    try { for (;;) { const { done, value } = await reader.read(); if (done) break; for (const byte of value) crc = (crc >>> 8) ^ table[(crc ^ byte) & 255]; } }
    finally { reader.releaseLock(); }
    crc = (crc ^ 0xffffffff) >>> 0;
    const header = new Uint8Array(30), h = new DataView(header.buffer);
    h.setUint32(0, 0x04034b50, true); h.setUint16(4, 20, true); h.setUint16(6, 0x800, true); h.setUint16(12, 0x21, true);
    h.setUint32(14, crc, true); h.setUint32(18, size, true); h.setUint32(22, size, true); h.setUint16(26, name.length, true);
    local.push(header, name, file.blob);
    const entry = new Uint8Array(46), e = new DataView(entry.buffer);
    e.setUint32(0, 0x02014b50, true); e.setUint16(4, 20, true); e.setUint16(6, 20, true); e.setUint16(8, 0x800, true); e.setUint16(14, 0x21, true);
    e.setUint32(16, crc, true); e.setUint32(20, size, true); e.setUint32(24, size, true); e.setUint16(28, name.length, true); e.setUint32(42, offset, true);
    directory.push(entry, name); offset += header.length + name.length + size;
  }
  const directorySize = directory.reduce((n, item) => n + item.length, 0), end = new Uint8Array(22), e = new DataView(end.buffer);
  e.setUint32(0, 0x06054b50, true); e.setUint16(8, files.length, true); e.setUint16(10, files.length, true); e.setUint32(12, directorySize, true); e.setUint32(16, offset, true);
  return new Blob([...local, ...directory, end], { type: "application/zip" });
}
