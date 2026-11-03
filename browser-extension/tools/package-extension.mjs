// Builds the unpacked extension and zips it for the Chrome Web Store.
//   node browser-extension/tools/package-extension.mjs
// Writes browser-extension/dist/winshot-capture-<version>.zip. Dependency-free (zlib only),
// so it runs the same on Windows, CI, and the Linux container.
import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const project = path.resolve(here, "..");
const unpacked = path.join(project, "dist", "winshot-capture");

execFileSync(process.execPath, [path.join(here, "build-extension.mjs")], { stdio: "inherit" });

// The store package is what Chrome runs: no test docs, no source maps.
const SKIP = new Set(["docs", "README.md"]);

function walk(directory, prefix = "") {
  const entries = [];
  for (const name of fs.readdirSync(directory).sort()) {
    if (!prefix && SKIP.has(name)) continue;
    // The build stamp only drives developer auto-reload and would make every zip differ.
    if (prefix === "src" && name === "build-stamp.json") continue;
    const full = path.join(directory, name);
    const relative = prefix ? `${prefix}/${name}` : name;
    if (fs.statSync(full).isDirectory()) entries.push(...walk(full, relative));
    else entries.push({ name: relative, data: fs.readFileSync(full) });
  }
  return entries;
}

const CRC_TABLE = new Uint32Array(256).map((_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = (c & 1) ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) crc = CRC_TABLE[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

// Fixed timestamp (1980-01-01) so the same sources always give the same zip bytes.
const DOS_TIME = 0;
const DOS_DATE = (0 << 9) | (1 << 5) | 1;

export function zip(entries) {
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const entry of entries) {
    const name = Buffer.from(entry.name, "utf8");
    const compressed = zlib.deflateRawSync(entry.data, { level: 9 });
    const crc = crc32(entry.data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6); // UTF-8 names
    local.writeUInt16LE(8, 8);      // deflate
    local.writeUInt16LE(DOS_TIME, 10);
    local.writeUInt16LE(DOS_DATE, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(compressed.length, 18);
    local.writeUInt32LE(entry.data.length, 22);
    local.writeUInt16LE(name.length, 26);
    locals.push(local, name, compressed);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(8, 10);
    central.writeUInt16LE(DOS_TIME, 12);
    central.writeUInt16LE(DOS_DATE, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(compressed.length, 20);
    central.writeUInt32LE(entry.data.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, name);
    offset += local.length + name.length + compressed.length;
  }
  const centralSize = centrals.reduce((sum, part) => sum + part.length, 0);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralSize, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, ...centrals, end]);
}

const manifest = JSON.parse(fs.readFileSync(path.join(unpacked, "manifest.json"), "utf8"));
if (!/^\d+(\.\d+){0,3}$/.test(manifest.version)) throw new Error(`Invalid manifest version ${manifest.version}`);
for (const size of ["16", "32", "48", "128"]) {
  if (!fs.existsSync(path.join(unpacked, manifest.icons[size]))) throw new Error(`Missing ${size}px icon`);
}
const entries = walk(unpacked);
const output = path.join(project, "dist", `winshot-capture-${manifest.version}.zip`);
fs.writeFileSync(output, zip(entries));
console.log(`Packaged ${entries.length} files into ${path.relative(process.cwd(), output)} (${(fs.statSync(output).size / 1024).toFixed(0)} KB)`);
if (process.env.GITHUB_OUTPUT) {
  fs.appendFileSync(process.env.GITHUB_OUTPUT, `zip=${output}\nversion=${manifest.version}\n`);
}
