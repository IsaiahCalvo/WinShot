export const MAX_VIDEO_BYTES = 1024 * 1024 * 1024;
export function supportedRecordingType() {
  if (typeof MediaRecorder !== "function") return "";
  return ["video/webm;codecs=vp8,opus", "video/webm;codecs=vp9,opus", "video/webm"]
    .find(type => MediaRecorder.isTypeSupported(type)) || "";
}
export function mediaEvent(node, success, { timeout = 15000, signal } = {}) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) return reject(signal.reason || new DOMException("Cancelled", "AbortError"));
    let timer;
    const clean = () => { clearTimeout(timer); node.removeEventListener(success, ready); node.removeEventListener("error", failed); signal?.removeEventListener("abort", aborted); };
    const ready = () => { clean(); resolve(); };
    const failed = () => { clean(); reject(new Error("Chrome could not read this video. Try a WebM or an MP4 supported by your browser.")); };
    const aborted = () => { clean(); reject(signal.reason || new DOMException("Cancelled", "AbortError")); };
    node.addEventListener(success, ready, { once: true });
    node.addEventListener("error", failed, { once: true });
    signal?.addEventListener("abort", aborted, { once: true });
    timer = setTimeout(() => { clean(); reject(new Error("This video did not finish loading. Try a smaller supported video.")); }, timeout);
  });
}
function bounded(promise, { timeout = 15000, signal, message = "Chrome's media encoder did not finish. Try again or save the original." } = {}) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) return reject(signal.reason || new DOMException("Cancelled", "AbortError"));
    const aborted = () => { clean(); reject(signal.reason || new DOMException("Cancelled", "AbortError")); };
    const timer = setTimeout(() => { clean(); reject(new Error(message)); }, timeout);
    const clean = () => { clearTimeout(timer); signal?.removeEventListener("abort", aborted); };
    signal?.addEventListener("abort", aborted, { once: true });
    Promise.resolve(promise).then(value => { clean(); resolve(value); }, error => { clean(); reject(error); });
  });
}
export async function seekVideo(video, time, signal) {
  if (Math.abs(video.currentTime - time) < 0.001 && !video.seeking) return;
  const ready = mediaEvent(video, "seeked", { signal });
  video.currentTime = time;
  await ready;
}
export async function inspectVideo(blob, knownDuration) {
  if (!blob.size || blob.size > MAX_VIDEO_BYTES) throw new Error("Choose a non-empty video smaller than 1 GB.");
  const video = document.createElement("video");
  video.preload = "metadata";
  video.muted = true;
  const url = URL.createObjectURL(blob);
  try {
    const ready = mediaEvent(video, "loadedmetadata");
    video.src = url;
    await ready;
    if (!Number.isFinite(video.duration)) {
      // MediaRecorder's streaming WebM omits duration. Chrome discovers its last
      // timestamp by seeking past EOF; never write a fabricated duration to it.
      const discovered = mediaEvent(video, "seeked");
      video.currentTime = 1e10;
      await discovered;
    }
    const duration = Number.isFinite(video.duration) && video.duration > 0 ? video.duration : knownDuration;
    if (!Number.isFinite(duration) || duration < .1 || !video.videoWidth || !video.videoHeight)
      throw new Error("A playable video track of at least 0.1 seconds could not be read from this file.");
    return { duration, width: video.videoWidth, height: video.videoHeight };
  } finally { video.removeAttribute("src"); video.load(); URL.revokeObjectURL(url); }
}

// Chrome's built-in encoder processes the selected interval at playback speed.
// This keeps the extension local and avoids shipping a large WASM codec bundle.
export async function exportVideoClip(blob, {
  start, end, width, height, fps = 30, quality = "balanced", mute = false, volume = 1, mono = false, signal, onProgress = () => {}
}) {
  const mimeType = supportedRecordingType();
  if (!mimeType || !HTMLCanvasElement.prototype.captureStream) throw new Error("This Chrome version cannot export WebM video. Save the original instead.");
  if (!Number.isFinite(start) || !Number.isFinite(end) || start < 0 || end <= start || end - start < .1) throw new Error("Choose a clip of at least 0.1 seconds.");
  if (signal?.aborted) throw signal.reason || new DOMException("Cancelled", "AbortError");
  if (document.hidden) throw new Error("Keep the video editor tab visible while exporting.");
  const video = document.createElement("video");
  video.muted = true;
  video.preload = "auto";
  const url = URL.createObjectURL(blob);
  let stream, recorder, audio, frame, stopPromise, canvasStream, destination;
  const parts = [];
  const localAbort = new AbortController();
  const abort = () => localAbort.abort(signal?.reason || new DOMException("Export cancelled. Your source video is safe.", "AbortError"));
  const hidden = () => { if (document.hidden) localAbort.abort(new Error("Export stopped because this tab was hidden. Keep the editor visible and try again; your source is safe.")); };
  signal?.addEventListener("abort", abort, { once: true });
  document.addEventListener("visibilitychange", hidden);
  try {
  // Resume the audio context inside the Export click's user activation.
  if (!mute) {
    const AudioContextClass = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextClass) throw new Error("Audio export is unavailable in this browser. Choose Mute or save the original.");
    audio = new AudioContextClass();
    await bounded(audio.resume(), { signal: localAbort.signal, message: "Audio could not start. Choose Mute or try Export again." });
  }
    const ready = mediaEvent(video, "loadeddata", { signal: localAbort.signal });
    video.src = url;
    await ready;
    if (Number.isFinite(video.duration) && end > video.duration + .1) throw new Error("The selected end is beyond the video duration.");
    await seekVideo(video, start, localAbort.signal);
    const outputWidth = Math.round(width || video.videoWidth), outputHeight = Math.round(height || video.videoHeight);
    if (outputWidth < 1 || outputHeight < 1 || outputWidth * outputHeight > 16_777_216) throw new Error("Choose an output size of 16 megapixels or smaller.");
    const canvas = document.createElement("canvas");
    canvas.width = outputWidth; canvas.height = outputHeight;
    const ctx = canvas.getContext("2d", { alpha: false });
    if (!ctx) throw new Error("Video rendering is unavailable. Save the original and reload the editor.");
    ctx.drawImage(video, 0, 0, outputWidth, outputHeight);
    canvasStream = canvas.captureStream(Math.min(60, Math.max(10, fps)));
    stream = new MediaStream(canvasStream.getVideoTracks());
    if (audio) {
      const source = audio.createMediaElementSource(video), gain = audio.createGain();
      gain.gain.value = Math.min(2, Math.max(0, volume));
      destination = audio.createMediaStreamDestination();
      if (mono) { gain.channelCount = 1; gain.channelCountMode = "explicit"; gain.channelInterpretation = "speakers"; destination.channelCount = 1; }
      source.connect(gain).connect(destination);
      // HTMLMediaElement.muted also silences MediaElementAudioSource in Chrome.
      // Route unmuted audio only to the recording destination, never speakers.
      video.muted = false;
      for (const track of destination.stream.getAudioTracks()) stream.addTrack(track);
    }
    const multiplier = { compact: .06, balanced: .12, high: .2 }[quality] || .12;
    recorder = new MediaRecorder(stream, { mimeType, videoBitsPerSecond: Math.round(Math.min(30_000_000, Math.max(250_000, outputWidth * outputHeight * fps * multiplier))), audioBitsPerSecond: 128_000 });
    let bytes = 0, recorderError;
    recorder.ondataavailable = event => {
      if (event.data.size) { parts.push(event.data); bytes += event.data.size; }
      if (bytes > MAX_VIDEO_BYTES) localAbort.abort(new Error("The export reached 1 GB. Choose a shorter clip or a smaller output size."));
    };
    stopPromise = new Promise((resolve, reject) => {
      recorder.onstop = resolve;
      recorder.onerror = event => { recorderError = event.error || new Error("Chrome's video encoder failed."); reject(recorderError); };
    });
    // Attach a handler immediately; an encoder error must never become an
    // unhandled rejection while the playback loop is still unwinding.
    stopPromise.catch(() => {});
    recorder.start(500);
    await bounded(video.play(), { signal: localAbort.signal, message: "Playback could not start for export. Try again or save the original." });
    let lastTime = video.currentTime, lastAdvance = performance.now();
    await new Promise((resolve, reject) => {
      const cancel = () => { clean(); reject(localAbort.signal.reason || new DOMException("Cancelled", "AbortError")); };
      const clean = () => { cancelAnimationFrame(frame); localAbort.signal.removeEventListener("abort", cancel); };
      localAbort.signal.addEventListener("abort", cancel, { once: true });
      const render = () => {
        if (localAbort.signal.aborted) return cancel();
        if (recorderError) { clean(); reject(recorderError); return; }
        if (video.error) { clean(); reject(new Error("The source video could not be decoded during export.")); return; }
        if (video.currentTime > lastTime + .001) { lastTime = video.currentTime; lastAdvance = performance.now(); }
        if (performance.now() - lastAdvance > 15000) { clean(); reject(new Error("Playback stalled during export. Try a shorter clip or save the original.")); return; }
        ctx.drawImage(video, 0, 0, outputWidth, outputHeight);
        onProgress(Math.max(0, Math.min(1, (video.currentTime - start) / (end - start))));
        if (video.currentTime >= end || video.ended) { clean(); resolve(); return; }
        frame = requestAnimationFrame(render);
      };
      render();
    });
    video.pause();
    if (recorder.state !== "inactive") recorder.stop();
    await bounded(stopPromise, { signal: localAbort.signal });
    if (localAbort.signal.aborted) throw localAbort.signal.reason;
    const result = new Blob(parts, { type: mimeType });
    if (!result.size) throw new Error("Chrome produced an empty video. Save the original and try again.");
    onProgress(1);
    return result;
  } finally {
    cancelAnimationFrame(frame);
    if (recorder && recorder.state !== "inactive") recorder.stop();
    if (stopPromise) await bounded(stopPromise, { timeout: 5000 }).catch(() => {});
    video.pause(); video.removeAttribute("src"); video.load();
    stream?.getTracks().forEach(track => track.stop());
    canvasStream?.getTracks().forEach(track => track.stop());
    destination?.stream.getTracks().forEach(track => track.stop());
    if (audio && audio.state !== "closed") await bounded(audio.close(), { timeout: 3000 }).catch(() => {});
    URL.revokeObjectURL(url);
    signal?.removeEventListener("abort", abort);
    document.removeEventListener("visibilitychange", hidden);
  }
}
