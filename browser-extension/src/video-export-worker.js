import { Input, Output, Conversion, BlobSource, ALL_FORMATS, WebMOutputFormat, Mp4OutputFormat, StreamTarget, BufferTarget, AudioSample, Quality, canEncodeVideo, canEncodeAudio } from "./vendor/mediabunny/mediabunny.min.mjs";
import { videoGeometry, compatibleVideoTransform } from "./video-transforms.js";

const MAX_BYTES = 1024 * 1024 * 1024, MEMORY_LIMIT = 64 * 1024 * 1024;
let conversion, input, output, directory, entry, operation, cancelled = false;
// Conversion normalizes source frames before our speed callback. Its input
// normalization rate differs from the final muxer's timestamp grid when retimed.
class RetimedOutput extends Output {
  constructor(options, frameRate) { super(options); this.expectedFrameRate = frameRate; }
  addVideoTrack(source, metadata) { return super.addVideoTrack(source, { ...metadata, frameRate: this.expectedFrameRate }); }
}
function checkCancelled() { if (cancelled) throw new DOMException("Export cancelled. Your source is safe.", "AbortError"); }
function report(type, data = {}) { postMessage({ type, ...data }); }
async function codecs(width = 1280, height = 720, frameRate = 30) {
  if (typeof VideoEncoder !== "function" || typeof VideoDecoder !== "function" || typeof AudioEncoder !== "function" || typeof AudioDecoder !== "function") return { available: false, webm: null, mp4: null };
  const firstVideo = async candidates => { for (const codec of candidates) if (await canEncodeVideo(codec, { width, height, frameRate })) return codec; return null; };
  const firstAudio = async candidates => { for (const codec of candidates) if (await canEncodeAudio(codec, { sampleRate: 48000, numberOfChannels: 2 })) return codec; return null; };
  const [webmVideo, mp4Video, webmAudio, mp4Audio] = await Promise.all([firstVideo(["vp8", "vp9"]), firstVideo(["avc", "vp9", "av1"]), firstAudio(["opus"]), firstAudio(["aac", "opus"])]);
  return { available: true, webm: webmVideo ? { video: webmVideo, audio: webmAudio } : null, mp4: mp4Video ? { video: mp4Video, audio: mp4Audio } : null };
}
async function cleanup() {
  input?.dispose(); input = undefined;
  if (entry && directory) await directory.removeEntry(entry).catch(() => {});
  entry = undefined;
}
async function targetForExport() {
  try {
    directory = await navigator.storage.getDirectory();
    entry = `winshot-export-${crypto.randomUUID()}.tmp`;
    const handle = await directory.getFileHandle(entry, { create: true });
    const writable = await handle.createWritable();
    report("storage", { entry });
    const stream = new WritableStream({
      async write(chunk) { checkCancelled(); if (chunk.position + chunk.data.byteLength > MAX_BYTES) throw new Error("The export reached 1 GB. Choose a shorter clip or a smaller resolution."); await writable.write(chunk); },
      async close() { await writable.close(); },
      async abort(reason) { await writable.abort(reason).catch(() => {}); }
    });
    return { target: new StreamTarget(stream, { chunked: true, chunkSize: 1024 * 1024 }), result: () => handle.getFile(), storage: "disk" };
  } catch (error) {
    if (entry && directory) await directory.removeEntry(entry).catch(() => {});
    entry = undefined;
    // Small exports still work when the profile disallows private file storage.
    // Encoders use backpressure; the output buffer has an explicit small cap.
    const target = new BufferTarget();
    target.on("write", ({ end }) => { if (end > MEMORY_LIMIT) throw new Error("Private export storage is unavailable and the export exceeded 64 MB. Try a shorter clip or compatibility WebM export."); });
    return { target, result: () => new Blob([target.buffer]), storage: "memory" };
  }
}
function processAudio(sample, speed, volume) {
  checkCancelled();
  const channels = sample.numberOfChannels, sourceFrames = sample.numberOfFrames, rate = sample.sampleRate;
  const start = Math.round(sample.timestamp * rate / speed), end = Math.round((sample.timestamp + sample.duration) * rate / speed);
  const frames = end - start;
  if (frames <= 0) return null;
  if (frames * channels > 8_388_608) throw new Error("An audio sample is too large to process safely.");
  const data = new Float32Array(frames * channels), source = new Float32Array(sourceFrames);
  for (let channel = 0; channel < channels; channel++) {
    sample.copyTo(source, { planeIndex: channel, format: "f32-planar" });
    for (let frame = 0; frame < frames; frame++) {
      const position = frame * sourceFrames / frames, lo = Math.min(sourceFrames - 1, Math.floor(position)), hi = Math.min(sourceFrames - 1, lo + 1);
      data[channel * frames + frame] = Math.max(-1, Math.min(1, (source[lo] + (source[hi] - source[lo]) * (position - lo)) * volume));
    }
  }
  return new AudioSample({ data, format: "f32-planar", sampleRate: rate, numberOfChannels: channels, timestamp: start / rate });
}
async function exportClip(blob, options) {
  const started = performance.now();
  if (!(blob instanceof Blob) || !blob.size || blob.size > MAX_BYTES) throw new Error("Choose a non-empty video smaller than 1 GB.");
  const { start, end } = options;
  if (!Number.isFinite(start) || !Number.isFinite(end) || start < 0 || end - start < .1) throw new Error("Choose a clip of at least 0.1 seconds.");
  input = new Input({ source: new BlobSource(blob), formats: ALL_FORMATS });
  let video, audio;
  try { video = await input.getPrimaryVideoTrack(); audio = await input.getPrimaryAudioTrack(); }
  catch { const error = new Error("This source cannot be read for fast export. Try WebM compatibility export."); error.code = "FAST_UNSUPPORTED"; throw error; }
  if (!video) { const error = new Error("This file has no video track readable by fast export. Try WebM compatibility export."); error.code = "FAST_UNSUPPORTED"; throw error; }
  const geometry = videoGeometry(await video.getDisplayWidth(), await video.getDisplayHeight(), options);
  const duration = await input.computeDuration();
  if (Number.isFinite(duration) && end > duration + .1) throw new Error("The selected end is beyond the video duration.");
  checkCancelled();
  const supported = await codecs(geometry.width, geometry.height, geometry.fps);
  const selected = supported[options.format === "mp4" ? "mp4" : "webm"];
  if (!selected || (audio && !options.mute && !selected.audio)) { const error = new Error(`Fast ${options.format === "mp4" ? "MP4" : "WebM"} encoding with this audio is unavailable in this Chrome. Try WebM compatibility export or mute the clip.`); error.code = "FAST_UNSUPPORTED"; throw error; }
  if (!(await video.canDecode()) || (audio && !options.mute && !(await audio.canDecode()))) { const error = new Error("Chrome cannot decode this source for fast export. Try WebM compatibility export."); error.code = "FAST_UNSUPPORTED"; throw error; }
  checkCancelled();
  const target = await targetForExport();
  checkCancelled();
  output = new RetimedOutput({ format: options.format === "mp4" ? new Mp4OutputFormat({ fastStart: "fragmented" }) : new WebMOutputFormat(), target: target.target }, geometry.fps);
  const speed = geometry.speed, volume = Number(options.volume ?? 1);
  if (!Number.isFinite(volume) || volume < 0 || volume > 2) throw new Error("Choose an export volume between 0% and 200%.");
  conversion = await Conversion.init({
    input, output, tracks: "primary", copy: false, trim: { start, end }, showWarnings: false,
    video: { ...compatibleVideoTransform(geometry), width: geometry.width, height: geometry.height, fit: "fill", codec: selected.video, quality: new Quality(options.quality === "compact" ? "low" : options.quality === "high" ? "high" : "medium"), frameRate: geometry.fps / speed, allowTransformationMetadata: false,
      process: speed === 1 ? undefined : sample => { checkCancelled(); const adjusted = sample.clone(); adjusted.setTimestamp(sample.timestamp / speed); adjusted.setDuration(sample.duration / speed); return adjusted; } },
    audio: { discard: Boolean(options.mute), codec: selected.audio || undefined, numberOfChannels: options.mono ? 1 : undefined, sampleRate: 48000,
      process: speed !== 1 || volume !== 1 ? sample => processAudio(sample, speed, volume) : undefined }
  });
  const unexpected = conversion.discardedTracks.filter(item => item.track.type === "video" || (item.track.type === "audio" && !options.mute));
  if (!conversion.isValid || unexpected.length) { const error = new Error(`Fast export could not retain every requested track: ${unexpected.map(item => item.reason).join(", ") || "unsupported conversion"}. Try compatibility WebM export.`); error.code = "FAST_UNSUPPORTED"; throw error; }
  conversion.onProgress = progress => { checkCancelled(); report("progress", { progress }); };
  report("encoding", { engine: "fast", format: options.format || "webm", codecs: selected, storage: target.storage });
  await conversion.execute();
  checkCancelled();
  const file = await target.result();
  if (!file.size) throw new Error("Chrome produced an empty export.");
  const mime = options.format === "mp4" ? "video/mp4" : "video/webm";
  const result = file.type === mime ? file : file.slice(0, file.size, mime);
  input.dispose(); input = undefined;
  report("result", { blob: result, entry, info: { engine: "fast", format: options.format || "webm", codecs: selected, storage: target.storage, elapsedMs: performance.now() - started, duration: (end - start) / speed, width: geometry.width, height: geometry.height } });
}
onmessage = async ({ data }) => {
  if (data.type === "release") { cancelled = true; await conversion?.cancel().catch(() => {}); await operation?.catch(() => {}); await cleanup(); report("released"); close(); return; }
  if (data.type === "cancel") { cancelled = true; await conversion?.cancel().catch(() => {}); return; }
  if (data.type === "probe") { try { report("capabilities", { capabilities: await codecs(data.width, data.height, data.fps) }); } catch (error) { report("error", { message: error.message }); } return; }
  if (data.type !== "start") return;
  try { operation = exportClip(data.blob, data.options); await operation; }
  catch (error) {
    await conversion?.cancel().catch(() => {});
    await output?.cancel().catch(() => {});
    await cleanup();
    report("error", { message: error.message || "Fast video export failed.", name: error.name, code: error.code });
  }
};
