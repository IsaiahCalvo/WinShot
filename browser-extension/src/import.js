import { putRecord, putTile } from "./store.js";

// Open a picture from disk or the clipboard as a capture, so the editor and every
// export work on it (FireShot's "import from clipboard / file").
function safeName(value) {
  const cleaned = String(value || "image").replace(/\.[a-z0-9]{2,5}$/i, "").replace(/[<>:"/\\|?*\x00-\x1f]/g, "-").replace(/[. ]+$/g, "").trim();
  return (cleaned || "image").slice(0, 120);
}

async function thumbnailOf(bitmap) {
  const scale = Math.min(1, 240 / bitmap.width);
  const side = Math.min(bitmap.height, bitmap.width);
  const canvas = new OffscreenCanvas(Math.max(1, Math.round(bitmap.width * scale)), Math.max(1, Math.round(side * scale)));
  const context = canvas.getContext("2d", { alpha: false });
  context.fillStyle = "#fff";
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.drawImage(bitmap, 0, 0, bitmap.width, side, 0, 0, canvas.width, canvas.height);
  return await canvas.convertToBlob({ type: "image/jpeg", quality: 0.7 });
}

export async function importImage(blob, name = "Imported image") {
  if (!blob || !/^image\//.test(blob.type || "image/png")) throw new Error("That file is not an image.");
  let bitmap;
  try {
    bitmap = await createImageBitmap(blob);
  } catch {
    throw new Error("The browser could not read that image.");
  }
  const { width, height } = bitmap;
  try {
    if (width > 32_000 || height > 200_000) throw new Error("That image is too large to edit here.");
    const id = `${Date.now().toString(36)}-${crypto.randomUUID()}`;
    const now = new Date().toISOString();
    const title = safeName(name);
    await putTile({
      captureId: id, index: 0, blob,
      destX: 0, destY: 0, destXPx: 0, destYPx: 0,
      widthCss: width, heightCss: height, widthPx: width, heightPx: height,
      scaleX: 1, scaleY: 1, seamScore: null
    });
    await putRecord({
      protocolVersion: 1,
      id,
      mode: "import",
      tabId: null,
      windowId: null,
      sourceUrl: "",
      sourceTitle: title,
      fileName: title,
      startedAt: now,
      finishedAt: now,
      state: "complete",
      semantics: { text: [], links: [] },
      thumbnail: await thumbnailOf(bitmap),
      report: {
        target: "image",
        mode: "import",
        dimensionsCss: { width, height },
        dimensionsPx: { width, height },
        tileCount: 1,
        seamConfidence: null,
        warnings: [],
        fallbackUsed: null,
        restoration: "not needed",
        coverage: { complete: true, ratio: 1, gaps: [] }
      }
    });
    return id;
  } finally {
    bitmap.close();
  }
}

export async function imageFromClipboard() {
  const items = await navigator.clipboard.read();
  for (const item of items) {
    const type = item.types.find((value) => value.startsWith("image/"));
    if (type) return await item.getType(type);
  }
  throw new Error("The clipboard has no image.");
}
