const textEncoder = new TextEncoder();
const MAX_CANVAS_EDGE = 16384;
const MAX_CANVAS_PIXELS = 120_000_000;

function ascii(value) {
  return textEncoder.encode(value);
}

function concat(parts) {
  const length = parts.reduce((sum, part) => sum + part.byteLength, 0);
  const result = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    result.set(part, offset);
    offset += part.byteLength;
  }
  return result;
}

function crcTable() {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
}

const CRC_TABLE = crcTable();

function pngChunk(type, data = new Uint8Array()) {
  const name = ascii(type);
  const body = concat([name, data]);
  let crc = 0xffffffff;
  for (const value of body) crc = CRC_TABLE[(crc ^ value) & 0xff] ^ (crc >>> 8);
  crc = (crc ^ 0xffffffff) >>> 0;
  const output = new Uint8Array(12 + data.byteLength);
  const view = new DataView(output.buffer);
  view.setUint32(0, data.byteLength);
  output.set(name, 4);
  output.set(data, 8);
  view.setUint32(8 + data.byteLength, crc);
  return output;
}

async function decodeIntersecting(tiles, left, top, width, height) {
  const matching = tiles.filter((tile) =>
    tile.destXPx < left + width && tile.destXPx + tile.widthPx > left &&
    tile.destYPx < top + height && tile.destYPx + tile.heightPx > top);
  return await Promise.all(matching.map(async (tile) => ({ tile, bitmap: await createImageBitmap(tile.blob) })));
}

async function renderBandPixels(width, top, height, tiles, background = "#ffffff") {
  const output = new Uint8ClampedArray(width * height * 4);
  const decoded = await decodeIntersecting(tiles, 0, top, width, height);
  try {
    for (let stripeLeft = 0; stripeLeft < width; stripeLeft += 8192) {
      const stripeWidth = Math.min(8192, width - stripeLeft);
      const canvas = new OffscreenCanvas(stripeWidth, height);
      const context = canvas.getContext("2d", { alpha: false, willReadFrequently: true });
      context.fillStyle = background;
      context.fillRect(0, 0, stripeWidth, height);
      for (const { tile, bitmap } of decoded) {
        context.drawImage(bitmap, tile.destXPx - stripeLeft, tile.destYPx - top, tile.widthPx, tile.heightPx);
      }
      const stripe = context.getImageData(0, 0, stripeWidth, height).data;
      for (let y = 0; y < height; y++) {
        output.set(stripe.subarray(y * stripeWidth * 4, (y + 1) * stripeWidth * 4), (y * width + stripeLeft) * 4);
      }
    }
  } finally {
    for (const { bitmap } of decoded) bitmap.close();
  }
  return output;
}

export async function exportPng(record, tiles, onProgress = () => {}) {
  const { width, height } = record.report.dimensionsPx;
  if (width < 1 || height < 1 || width > 0x7fffffff || height > 0x7fffffff) throw new Error("Invalid PNG dimensions.");
  if (typeof CompressionStream !== "function") throw new Error("This browser does not support streamed PNG export. Use PDF instead.");

  const bandHeight = Math.max(1, Math.min(128, Math.floor(32_000_000 / (width * 4))));
  let nextTop = 0;
  const raw = new ReadableStream({
    async pull(controller) {
      if (nextTop >= height) {
        controller.close();
        return;
      }
      const count = Math.min(bandHeight, height - nextTop);
      const pixels = await renderBandPixels(width, nextTop, count, tiles);
      const scanlines = new Uint8Array((width * 4 + 1) * count);
      for (let y = 0; y < count; y++) {
        const target = y * (width * 4 + 1);
        scanlines[target] = 0;
        scanlines.set(pixels.subarray(y * width * 4, (y + 1) * width * 4), target + 1);
      }
      nextTop += count;
      onProgress(nextTop / height);
      controller.enqueue(scanlines);
    }
  });
  const compressed = new Uint8Array(await new Response(raw.pipeThrough(new CompressionStream("deflate"))).arrayBuffer());
  const header = new Uint8Array(13);
  const view = new DataView(header.buffer);
  view.setUint32(0, width);
  view.setUint32(4, height);
  header.set([8, 6, 0, 0, 0], 8);
  const chunks = [];
  for (let offset = 0; offset < compressed.length; offset += 8 * 1024 * 1024) {
    chunks.push(pngChunk("IDAT", compressed.subarray(offset, offset + 8 * 1024 * 1024)));
  }
  return new Blob([
    new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk("IHDR", header),
    ...chunks,
    pngChunk("IEND")
  ], { type: "image/png" });
}

async function renderCanvas(record, tiles, type, quality = 0.9) {
  const { width, height } = record.report.dimensionsPx;
  if (width > MAX_CANVAS_EDGE || height > MAX_CANVAS_EDGE || width * height > MAX_CANVAS_PIXELS) {
    throw new Error("This format needs one browser canvas and is disabled for very large captures. Save PNG or PDF instead.");
  }
  const canvas = new OffscreenCanvas(width, height);
  const context = canvas.getContext("2d", { alpha: false });
  context.fillStyle = "#fff";
  context.fillRect(0, 0, width, height);
  for (const tile of tiles.sort((a, b) => a.index - b.index)) {
    const bitmap = await createImageBitmap(tile.blob);
    try {
      context.drawImage(bitmap, tile.destXPx, tile.destYPx, tile.widthPx, tile.heightPx);
    } finally {
      bitmap.close();
    }
  }
  return await canvas.convertToBlob({ type, quality });
}

export async function exportJpeg(record, tiles) {
  return await renderCanvas(record, tiles, "image/jpeg", 0.9);
}

export async function exportWebp(record, tiles) {
  return await renderCanvas(record, tiles, "image/webp", 0.9);
}

async function pdfPageJpeg(record, tiles, top, sliceHeight, outputWidth) {
  const sourceWidth = record.report.dimensionsPx.width;
  const scale = outputWidth / sourceWidth;
  const outputHeight = Math.max(1, Math.round(sliceHeight * scale));
  const canvas = new OffscreenCanvas(outputWidth, outputHeight);
  const context = canvas.getContext("2d", { alpha: false });
  context.fillStyle = "white";
  context.fillRect(0, 0, outputWidth, outputHeight);
  const decoded = await decodeIntersecting(tiles, 0, top, sourceWidth, sliceHeight);
  try {
    for (const { tile, bitmap } of decoded) {
      context.drawImage(bitmap,
        tile.destXPx * scale,
        (tile.destYPx - top) * scale,
        tile.widthPx * scale,
        tile.heightPx * scale);
    }
  } finally {
    for (const { bitmap } of decoded) bitmap.close();
  }
  const blob = await canvas.convertToBlob({ type: "image/jpeg", quality: 0.9 });
  return {
    bytes: new Uint8Array(await blob.arrayBuffer()),
    width: outputWidth,
    height: outputHeight,
    sourceTop: top,
    sourceHeight: sliceHeight
  };
}

function pdfLiteral(value) {
  return String(value || "")
    .normalize("NFKD")
    .replace(/[^\x20-\x7e]/g, " ")
    .replace(/\s+/g, " ")
    .trim()
    .replace(/[()\\]/g, (character) => `\\${character}`);
}

function buildPdf(images, title, record) {
  const cssWidth = Math.max(1, record.report.dimensionsCss?.width || record.report.dimensionsPx.width);
  const sourceWidth = Math.max(1, record.report.dimensionsPx.width);
  const cssToSource = sourceWidth / cssWidth;
  const allText = record.semantics?.text || [];
  const allLinks = record.semantics?.links || [];
  const objects = [null, null, null];
  const pages = [];
  let nextId = 3;

  for (const image of images) {
    const pageTopCss = image.sourceTop / cssToSource;
    const pageBottomCss = (image.sourceTop + image.sourceHeight) / cssToSource;
    const textEntries = allText.filter((entry) => entry.y < pageBottomCss && entry.y + entry.height > pageTopCss);
    const linkEntries = allLinks.filter((entry) => entry.y < pageBottomCss && entry.y + entry.height > pageTopCss);
    pages.push({
      image,
      pageTopCss,
      textEntries,
      linkEntries,
      pageId: nextId++,
      contentId: nextId++,
      imageId: nextId++,
      annotationIds: linkEntries.map(() => nextId++)
    });
  }

  const objectCount = nextId - 1;
  objects.length = objectCount + 1;
  const pageRefs = pages.map((page) => page.pageId);
  objects[1] = [ascii("<< /Type /Catalog /Pages 2 0 R >>")];
  objects[2] = [ascii(`<< /Type /Pages /Count ${images.length} /Kids [${pageRefs.map((id) => `${id} 0 R`).join(" ")}] >>`)];

  for (const page of pages) {
    const { image, pageId, contentId, imageId } = page;
    const pageWidth = image.width * 0.75;
    const pageHeight = image.height * 0.75;
    const pointsPerCss = pageWidth / cssWidth;
    const commandLines = [`q\n${pageWidth} 0 0 ${pageHeight} 0 0 cm\n/Im0 Do\nQ`];
    for (const entry of page.textEntries) {
      const value = pdfLiteral(entry.text).slice(0, 1000);
      if (!value) continue;
      const x = Math.max(0, entry.x * pointsPerCss);
      const y = Math.max(0, pageHeight - (entry.y - page.pageTopCss + entry.height) * pointsPerCss);
      const fontSize = Math.max(4, Math.min(200, entry.fontSize * pointsPerCss));
      commandLines.push(`BT /F1 ${fontSize.toFixed(2)} Tf 3 Tr 1 0 0 1 ${x.toFixed(2)} ${y.toFixed(2)} Tm (${value}) Tj ET`);
    }
    const commands = ascii(`${commandLines.join("\n")}\n`);
    const annotations = page.annotationIds.length
      ? ` /Annots [${page.annotationIds.map((id) => `${id} 0 R`).join(" ")}]`
      : "";
    objects[pageId] = [ascii(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${pageWidth} ${pageHeight}] /Resources << /XObject << /Im0 ${imageId} 0 R >> /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents ${contentId} 0 R${annotations} >>`)];
    objects[contentId] = [ascii(`<< /Length ${commands.length} >>\nstream\n`), commands, ascii("endstream")];
    objects[imageId] = [
      ascii(`<< /Type /XObject /Subtype /Image /Width ${image.width} /Height ${image.height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${image.bytes.length} >>\nstream\n`),
      image.bytes,
      ascii("\nendstream")
    ];
    for (let i = 0; i < page.linkEntries.length; i++) {
      const entry = page.linkEntries[i];
      const left = Math.max(0, entry.x * pointsPerCss);
      const right = Math.min(pageWidth, (entry.x + entry.width) * pointsPerCss);
      const top = Math.min(pageHeight, pageHeight - (entry.y - page.pageTopCss) * pointsPerCss);
      const bottom = Math.max(0, pageHeight - (entry.y - page.pageTopCss + entry.height) * pointsPerCss);
      objects[page.annotationIds[i]] = [ascii(`<< /Type /Annot /Subtype /Link /Rect [${left.toFixed(2)} ${bottom.toFixed(2)} ${right.toFixed(2)} ${top.toFixed(2)}] /Border [0 0 0] /A << /S /URI /URI (${pdfLiteral(entry.url)}) >> >>`)];
    }
  }

  const parts = [ascii("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n")];
  const offsets = new Array(objectCount + 1).fill(0);
  let length = parts[0].length;
  for (let id = 1; id <= objectCount; id++) {
    offsets[id] = length;
    const prefix = ascii(`${id} 0 obj\n`);
    const suffix = ascii("\nendobj\n");
    parts.push(prefix, ...objects[id], suffix);
    length += prefix.length + objects[id].reduce((sum, part) => sum + part.length, 0) + suffix.length;
  }
  const xrefOffset = length;
  const xref = [`xref\n0 ${objectCount + 1}\n`, "0000000000 65535 f \n"];
  for (let id = 1; id <= objectCount; id++) xref.push(`${String(offsets[id]).padStart(10, "0")} 00000 n \n`);
  const safeTitle = String(title || "WinShot Capture").replace(/[()\\]/g, " ");
  xref.push(`trailer\n<< /Size ${objectCount + 1} /Root 1 0 R /Info << /Title (${safeTitle}) /Producer (WinShot Capture) >> >>\nstartxref\n${xrefOffset}\n%%EOF`);
  parts.push(ascii(xref.join("")));
  return new Blob(parts, { type: "application/pdf" });
}

export async function exportPdf(record, tiles, onProgress = () => {}) {
  const { width, height } = record.report.dimensionsPx;
  const outputWidth = Math.min(12000, width);
  const sourcePageHeight = Math.max(1, Math.floor(8000 * width / outputWidth));
  const images = [];
  for (let top = 0; top < height; top += sourcePageHeight) {
    images.push(await pdfPageJpeg(record, tiles, top, Math.min(sourcePageHeight, height - top), outputWidth));
    onProgress(Math.min(1, (top + sourcePageHeight) / height));
  }
  return buildPdf(images, record.sourceTitle, record);
}
