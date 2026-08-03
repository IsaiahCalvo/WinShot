import { DEFAULT_SETTINGS, normalizeSettings, renderPdfTemplate } from "./settings.js";

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

function pdfHexUnicode(value) {
  let result = "";
  for (const character of String(value || "").slice(0, 1000)) {
    const codePoint = character.codePointAt(0);
    if (codePoint <= 0xffff) {
      result += codePoint.toString(16).padStart(4, "0");
    } else {
      const adjusted = codePoint - 0x10000;
      result += (0xd800 + (adjusted >> 10)).toString(16).padStart(4, "0");
      result += (0xdc00 + (adjusted & 0x3ff)).toString(16).padStart(4, "0");
    }
  }
  return result.toUpperCase();
}

function pdfDirectionalRuns(value) {
  return (String(value || "").match(/[\u0590-\u08ff]+|[^\u0590-\u08ff]+/g) || []).map((text) => ({
    text,
    encodedText: /^[\u0590-\u08ff]+$/.test(text) ? [...text].reverse().join("") : text
  }));
}

export function pdfPageSizePoints(options) {
  if (options.pageSize === "a4") return { width: 595.28, height: 841.89 };
  if (options.pageSize === "letter") return { width: 612, height: 792 };
  if (options.pageSize === "legal") return { width: 612, height: 1008 };
  if (options.pageSize === "custom") return { width: options.customWidthIn * 72, height: options.customHeightIn * 72 };
  return null;
}

function pdfContext(record, page, pages) {
  const date = new Date(record.startedAt || Date.now());
  return {
    title: record.sourceTitle || "WinShot Capture",
    url: record.sourceUrl || "",
    date: date.toISOString().slice(0, 10),
    time: date.toTimeString().slice(0, 8),
    page,
    pages
  };
}

function pageFurniture(options, record, pageNumber, totalPages, pageWidth, pageHeight) {
  const context = pdfContext(record, pageNumber, totalPages);
  const header = renderPdfTemplate(options.header, context);
  const footer = renderPdfTemplate(options.footer, context);
  const watermark = renderPdfTemplate(options.watermark, context);
  const margin = options.pageSize === "auto" && !header && !footer ? 0 : 24;
  const headerSpace = header ? 24 : 0;
  const footerSpace = footer ? 24 : 0;
  return {
    header,
    footer,
    watermark,
    margin,
    headerSpace,
    footerSpace,
    left: margin,
    bottom: margin + footerSpace,
    width: Math.max(1, pageWidth - margin * 2),
    height: Math.max(1, pageHeight - margin * 2 - headerSpace - footerSpace)
  };
}

function imagePage(image, options, pageNumber, totalPages) {
  const record = image.record;
  const fixed = pdfPageSizePoints(options);
  let pageWidth = fixed?.width || image.width * 0.75;
  let pageHeight = fixed?.height || image.height * 0.75;
  let furniture = pageFurniture(options, record, pageNumber, totalPages, pageWidth, pageHeight);
  if (!fixed && (furniture.header || furniture.footer)) {
    pageWidth += furniture.margin * 2;
    pageHeight += furniture.margin * 2 + furniture.headerSpace + furniture.footerSpace;
    furniture = pageFurniture(options, record, pageNumber, totalPages, pageWidth, pageHeight);
  }
  const scale = Math.min(furniture.width / image.width, furniture.height / image.height);
  const drawWidth = image.width * scale;
  const drawHeight = image.height * scale;
  const drawX = furniture.left + (furniture.width - drawWidth) / 2;
  const drawY = furniture.bottom + furniture.height - drawHeight;
  const cssWidth = Math.max(1, record.report.dimensionsCss?.width || record.report.dimensionsPx.width);
  const sourceScale = record.report.dimensionsPx.width / cssWidth;
  const pageTopCss = image.sourceTop / sourceScale;
  const pageBottomCss = (image.sourceTop + image.sourceHeight) / sourceScale;
  return {
    record,
    pageWidth,
    pageHeight,
    userUnit: 1,
    furniture,
    images: [{ image, name: "Im0", drawX, drawY, drawWidth, drawHeight }],
    textEntries: (record.semantics?.text || []).filter((entry) => entry.y < pageBottomCss && entry.y + entry.height > pageTopCss),
    linkEntries: (record.semantics?.links || []).filter((entry) => entry.y < pageBottomCss && entry.y + entry.height > pageTopCss),
    sourceTopCss: pageTopCss,
    sourceHeightCss: Math.max(1, pageBottomCss - pageTopCss),
    drawX,
    drawY,
    drawWidth,
    drawHeight,
    cssWidth
  };
}

function singleImagePage(images, options, pageNumber, totalPages) {
  const record = images[0].record;
  const sourceWidth = record.report.dimensionsPx.width;
  const sourceHeight = record.report.dimensionsPx.height;
  const fixed = pdfPageSizePoints(options);
  const naturalWidth = sourceWidth * 0.75;
  const naturalHeight = sourceHeight * 0.75;
  const userUnit = fixed ? 1 : Math.max(1, naturalWidth / 14000, naturalHeight / 14000);
  let pageWidth = fixed?.width || naturalWidth / userUnit;
  let pageHeight = fixed?.height || naturalHeight / userUnit;
  let furniture = pageFurniture(options, record, pageNumber, totalPages, pageWidth, pageHeight);
  if (!fixed && (furniture.header || furniture.footer)) {
    pageWidth += furniture.margin * 2;
    pageHeight += furniture.margin * 2 + furniture.headerSpace + furniture.footerSpace;
    furniture = pageFurniture(options, record, pageNumber, totalPages, pageWidth, pageHeight);
  }
  const scale = Math.min(furniture.width / sourceWidth, furniture.height / sourceHeight);
  const drawWidth = sourceWidth * scale;
  const drawHeight = sourceHeight * scale;
  const drawX = furniture.left + (furniture.width - drawWidth) / 2;
  const drawY = furniture.bottom + furniture.height - drawHeight;
  const cssWidth = Math.max(1, record.report.dimensionsCss?.width || sourceWidth);
  return {
    record,
    pageWidth,
    pageHeight,
    userUnit,
    furniture,
    images: images.map((image, index) => ({
      image,
      name: `Im${index}`,
      drawX,
      drawY: drawY + drawHeight - ((image.sourceTop + image.sourceHeight) / sourceHeight) * drawHeight,
      drawWidth,
      drawHeight: (image.sourceHeight / sourceHeight) * drawHeight
    })),
    textEntries: record.semantics?.text || [],
    linkEntries: record.semantics?.links || [],
    sourceTopCss: 0,
    sourceHeightCss: Math.max(1, record.report.dimensionsCss?.height || sourceHeight),
    drawX,
    drawY,
    drawWidth,
    drawHeight,
    cssWidth
  };
}

function buildPdf(images, title, options = {}) {
  const normalized = normalizeSettings({ pdf: { ...DEFAULT_SETTINGS.pdf, ...options } }).pdf;
  const groups = [];
  if (normalized.layout === "single") {
    const byRecord = new Map();
    for (const image of images) {
      const list = byRecord.get(image.record.id) || [];
      list.push(image);
      byRecord.set(image.record.id, list);
    }
    const entries = [...byRecord.values()];
    for (let index = 0; index < entries.length; index++) groups.push(singleImagePage(entries[index], normalized, index + 1, entries.length));
  } else {
    for (let index = 0; index < images.length; index++) groups.push(imagePage(images[index], normalized, index + 1, images.length));
  }

  const objects = [null, null, null];
  let nextId = 3;
  const unicodeFontId = nextId++;
  const cidFontId = nextId++;
  const fontDescriptorId = nextId++;
  const toUnicodeId = nextId++;
  for (const page of groups) {
    page.pageId = nextId++;
    page.contentId = nextId++;
    for (const image of page.images) image.objectId = nextId++;
    page.annotationIds = page.linkEntries.map(() => nextId++);
  }
  const objectCount = nextId - 1;
  objects.length = objectCount + 1;
  objects[1] = [ascii("<< /Type /Catalog /Pages 2 0 R >>")];
  objects[2] = [ascii(`<< /Type /Pages /Count ${groups.length} /Kids [${groups.map((page) => `${page.pageId} 0 R`).join(" ")}] >>`)];
  const toUnicode = ascii(`/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /WinShotUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n1 beginbfrange\n<0000> <FFFF> <0000>\nendbfrange\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend`);
  objects[unicodeFontId] = [ascii(`<< /Type /Font /Subtype /Type0 /BaseFont /WinShotUnicode /Encoding /Identity-H /DescendantFonts [${cidFontId} 0 R] /ToUnicode ${toUnicodeId} 0 R >>`)];
  objects[cidFontId] = [ascii(`<< /Type /Font /Subtype /CIDFontType2 /BaseFont /WinShotUnicode /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor ${fontDescriptorId} 0 R /DW 500 /CIDToGIDMap /Identity >>`)];
  objects[fontDescriptorId] = [ascii("<< /Type /FontDescriptor /FontName /WinShotUnicode /Flags 4 /FontBBox [0 -250 1000 1000] /ItalicAngle 0 /Ascent 880 /Descent -220 /CapHeight 700 /StemV 80 >>")];
  objects[toUnicodeId] = [ascii(`<< /Length ${toUnicode.length} >>\nstream\n`), toUnicode, ascii("\nendstream")];

  for (let pageIndex = 0; pageIndex < groups.length; pageIndex++) {
    const page = groups[pageIndex];
    const lines = [];
    for (const entry of page.images) lines.push(`q\n${entry.drawWidth.toFixed(3)} 0 0 ${entry.drawHeight.toFixed(3)} ${entry.drawX.toFixed(3)} ${entry.drawY.toFixed(3)} cm\n/${entry.name} Do\nQ`);
    const xScale = page.drawWidth / page.cssWidth;
    const yScale = page.drawHeight / page.sourceHeightCss;
    for (const entry of page.textEntries) {
      const x = page.drawX + entry.x * xScale;
      const y = page.drawY + page.drawHeight - (entry.y - page.sourceTopCss + entry.height) * yScale;
      const fontSize = Math.max(4, Math.min(200, entry.fontSize * Math.min(xScale, yScale)));
      let cursorX = x;
      for (const run of pdfDirectionalRuns(entry.text)) {
        const value = pdfHexUnicode(run.encodedText);
        if (!value) continue;
        lines.push(`BT /F2 ${fontSize.toFixed(2)} Tf 3 Tr 1 0 0 1 ${cursorX.toFixed(2)} ${y.toFixed(2)} Tm <${value}> Tj ET`);
        cursorX += [...run.text].length * fontSize * 0.5;
      }
    }
    const furniture = page.furniture;
    if (furniture.header) lines.push(`BT /F1 10 Tf 0 Tr 0 g 1 0 0 1 ${furniture.left.toFixed(2)} ${(page.pageHeight - furniture.margin - 10).toFixed(2)} Tm (${pdfLiteral(furniture.header)}) Tj ET`);
    if (furniture.footer) lines.push(`BT /F1 10 Tf 0 Tr 0 g 1 0 0 1 ${furniture.left.toFixed(2)} ${Math.max(4, furniture.margin - 4).toFixed(2)} Tm (${pdfLiteral(furniture.footer)}) Tj ET`);
    if (furniture.watermark) {
      const text = pdfLiteral(furniture.watermark).slice(0, 300);
      lines.push(`q 0.75 g BT /F1 34 Tf 0 Tr 0.707 0.707 -0.707 0.707 ${(page.pageWidth * 0.25).toFixed(2)} ${(page.pageHeight * 0.35).toFixed(2)} Tm (${text}) Tj ET Q`);
    }
    const commands = ascii(`${lines.join("\n")}\n`);
    const xObjects = page.images.map((entry) => `/${entry.name} ${entry.objectId} 0 R`).join(" ");
    const annotations = page.annotationIds.length ? ` /Annots [${page.annotationIds.map((id) => `${id} 0 R`).join(" ")}]` : "";
    const userUnit = page.userUnit > 1 ? ` /UserUnit ${page.userUnit.toFixed(6)}` : "";
    objects[page.pageId] = [ascii(`<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${page.pageWidth.toFixed(3)} ${page.pageHeight.toFixed(3)}]${userUnit} /Resources << /XObject << ${xObjects} >> /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> /F2 ${unicodeFontId} 0 R >> >> /Contents ${page.contentId} 0 R${annotations} >>`)];
    objects[page.contentId] = [ascii(`<< /Length ${commands.length} >>\nstream\n`), commands, ascii("endstream")];
    for (const entry of page.images) {
      const image = entry.image;
      objects[entry.objectId] = [ascii(`<< /Type /XObject /Subtype /Image /Width ${image.width} /Height ${image.height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${image.bytes.length} >>\nstream\n`), image.bytes, ascii("\nendstream")];
    }
    for (let index = 0; index < page.linkEntries.length; index++) {
      const entry = page.linkEntries[index];
      const left = Math.max(0, page.drawX + entry.x * xScale);
      const right = Math.min(page.pageWidth, page.drawX + (entry.x + entry.width) * xScale);
      const top = Math.min(page.pageHeight, page.drawY + page.drawHeight - (entry.y - page.sourceTopCss) * yScale);
      const bottom = Math.max(0, page.drawY + page.drawHeight - (entry.y - page.sourceTopCss + entry.height) * yScale);
      objects[page.annotationIds[index]] = [ascii(`<< /Type /Annot /Subtype /Link /Rect [${left.toFixed(2)} ${bottom.toFixed(2)} ${right.toFixed(2)} ${top.toFixed(2)}] /Border [0 0 0] /A << /S /URI /URI (${pdfLiteral(entry.url)}) >> >>`)];
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

async function recordPdfImages(record, tiles, options, onProgress, progressStart = 0, progressSpan = 1) {
  const { width, height } = record.report.dimensionsPx;
  const outputWidth = Math.min(12000, width);
  const fixed = pdfPageSizePoints(options);
  const furniture = fixed ? pageFurniture(options, record, 1, 1, fixed.width, fixed.height) : null;
  const sourcePageHeight = options.layout === "single" || !fixed
    ? Math.max(1, Math.floor(8000 * width / outputWidth))
    : Math.max(1, Math.floor(width * furniture.height / furniture.width));
  const images = [];
  for (let top = 0; top < height; top += sourcePageHeight) {
    const image = await pdfPageJpeg(record, tiles, top, Math.min(sourcePageHeight, height - top), outputWidth);
    image.record = record;
    images.push(image);
    onProgress(progressStart + progressSpan * Math.min(1, (top + sourcePageHeight) / height));
  }
  return images;
}

export async function exportPdf(record, tiles, onProgress = () => {}, options = DEFAULT_SETTINGS.pdf) {
  const normalized = normalizeSettings({ pdf: options }).pdf;
  const images = await recordPdfImages(record, tiles, normalized, onProgress);
  return buildPdf(images, record.sourceTitle, normalized);
}

export async function exportBatchPdf(entries, onProgress = () => {}, options = DEFAULT_SETTINGS.pdf) {
  if (!entries.length) throw new Error("No completed captures are available for the PDF.");
  const normalized = normalizeSettings({ pdf: { ...options, layout: options.layout || "multipage" } }).pdf;
  const images = [];
  for (let index = 0; index < entries.length; index++) {
    const entry = entries[index];
    images.push(...await recordPdfImages(entry.record, entry.tiles, normalized, onProgress, index / entries.length, 1 / entries.length));
  }
  return buildPdf(images, `WinShot batch ${new Date().toISOString().slice(0, 10)}`, normalized);
}
