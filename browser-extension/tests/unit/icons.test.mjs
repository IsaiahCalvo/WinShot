import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import { desktopIcons } from "../../tools/icons.mjs";

test("every extension icon is the exact corresponding Windows desktop frame", () => {
  for (const [size, png] of desktopIcons()) {
    assert.equal(png.readUInt32BE(16), size);
    assert.equal(png.readUInt32BE(20), size);
    assert.deepEqual(fs.readFileSync(new URL(`../../icons/${size}.png`, import.meta.url)), png);
  }
});
