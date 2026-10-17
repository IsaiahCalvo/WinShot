// Shared geometry keeps the preview, offline encoder and compatibility encoder
// on the same crop/rotation coordinate system. Crop bounds follow rotation.
export function videoGeometry(sourceWidth, sourceHeight, options = {}) {
  if (![sourceWidth, sourceHeight].every(value => Number.isFinite(value) && value >= 2)) throw new Error("The source video dimensions are unavailable.");
  if (sourceWidth * sourceHeight > 16_777_216) throw new Error("The source video exceeds the 16-megapixel browser editing limit. Save the original or use a smaller recording.");
  const rotation = Number(options.rotation || 0);
  if (![0, 90, 180, 270].includes(rotation)) throw new Error("Choose a quarter-turn video rotation.");
  const rotatedWidth = rotation % 180 ? sourceHeight : sourceWidth;
  const rotatedHeight = rotation % 180 ? sourceWidth : sourceHeight;
  const crop = { left: 0, top: 0, width: 100, height: 100, ...options.crop };
  if (!Object.values(crop).every(value => Number.isFinite(Number(value))) || crop.left < 0 || crop.top < 0 || crop.width <= 0 || crop.height <= 0 || Number(crop.left) + Number(crop.width) > 100.001 || Number(crop.top) + Number(crop.height) > 100.001) throw new Error("Keep the crop inside the video and give it a positive width and height.");
  const left = Math.min(rotatedWidth - 2, Math.round(rotatedWidth * Number(crop.left) / 100));
  const top = Math.min(rotatedHeight - 2, Math.round(rotatedHeight * Number(crop.top) / 100));
  const cropWidth = Math.min(rotatedWidth - left, Math.max(2, Math.round(rotatedWidth * Number(crop.width) / 100)));
  const cropHeight = Math.min(rotatedHeight - top, Math.max(2, Math.round(rotatedHeight * Number(crop.height) / 100)));
  const width = Math.max(2, Math.round(Number(options.width || cropWidth) / 2) * 2);
  const height = Math.max(2, Math.round(Number(options.height || cropHeight) / 2) * 2);
  if (![width, height].every(Number.isFinite) || width * height > 16_777_216) throw new Error("Choose an output size of 16 megapixels or smaller.");
  const speed = Number(options.speed ?? 1), fps = Number(options.fps ?? 30);
  if (!Number.isFinite(speed) || speed < .25 || speed > 4) throw new Error("Choose a video speed between 0.25× and 4×.");
  if (!Number.isFinite(fps) || fps < 10 || fps > 60) throw new Error("Choose a frame rate between 10 and 60 fps.");
  return { sourceWidth, sourceHeight, rotation, rotatedWidth, rotatedHeight, crop: { left, top, width: cropWidth, height: cropHeight }, width, height, flipH: Boolean(options.flipH), flipV: Boolean(options.flipV), speed, fps };
}
export function drawVideoFrame(context, source, geometry, width = geometry.width, height = geometry.height) {
  const crop = geometry.crop;
  context.save();
  context.setTransform(1, 0, 0, 1, 0, 0);
  context.fillStyle = "#000"; context.fillRect(0, 0, width, height);
  context.scale(width / crop.width, height / crop.height);
  context.translate(-crop.left, -crop.top);
  context.translate(geometry.rotatedWidth / 2, geometry.rotatedHeight / 2);
  context.scale(geometry.flipH ? -1 : 1, geometry.flipV ? -1 : 1);
  context.rotate(geometry.rotation * Math.PI / 180);
  context.drawImage(source, -geometry.sourceWidth / 2, -geometry.sourceHeight / 2, geometry.sourceWidth, geometry.sourceHeight);
  context.restore();
}
export function compatibleVideoTransform(geometry) {
  return { rotate: (geometry.rotation + (geometry.flipV ? 180 : 0)) % 360, flip: geometry.flipH !== geometry.flipV, crop: geometry.crop };
}
