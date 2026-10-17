import fs from "node:fs";
import path from "node:path";

const desktopIcon = new URL("../../src/WinShot/Assets/winshot.ico", import.meta.url);
const sizes = [16, 32, 48, 128];
const pngSignature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);

// The Windows ICO already contains hand-sized PNGs. Preserve those pixels instead
// of redrawing the brand or resampling the largest icon for small toolbar sizes.
export function desktopIcons() {
  const ico = fs.readFileSync(desktopIcon);
  if (ico.readUInt16LE(0) !== 0 || ico.readUInt16LE(2) !== 1) throw new Error("Invalid WinShot desktop ICO.");
  const frames = new Map();
  for (let i = 0; i < ico.readUInt16LE(4); i++) {
    const entry = 6 + i * 16;
    const width = ico[entry] || 256;
    const height = ico[entry + 1] || 256;
    const length = ico.readUInt32LE(entry + 8);
    const offset = ico.readUInt32LE(entry + 12);
    if (offset + length > ico.length) throw new Error("Truncated WinShot desktop icon.");
    const png = ico.subarray(offset, offset + length);
    if (width === height && sizes.includes(width) && png.subarray(0, 8).equals(pngSignature)) frames.set(width, png);
  }
  if (sizes.some((size) => !frames.has(size))) throw new Error("WinShot desktop icon is missing a Chrome icon size.");
  return frames;
}

export function writeDesktopIcons(destination) {
  fs.mkdirSync(destination, { recursive: true });
  for (const [size, png] of desktopIcons()) fs.writeFileSync(path.join(destination, `${size}.png`), png);
}
