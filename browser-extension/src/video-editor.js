import { saveVideo, getVideo, listVideos, updateVideo, deleteVideo } from "./video-store.js";
import { MAX_VIDEO_BYTES, inspectVideo, supportedRecordingType, exportVideoClip, mediaEvent, seekVideo, supportsFastVideoExport, probeVideoExport, getVideoExportInfo, releaseVideoExport, cleanupStaleVideoExports } from "./video-tools.js";

import { videoGeometry, drawVideoFrame } from "./video-transforms.js";

const $ = selector => document.querySelector(selector);
let preview = $("#preview");
const status = $("#status");
const editingControls = ["#video-title", "#resolution", "#quality", "#fps", "#mute-export", "#mono-export", "#volume", "#trim-start", "#trim-end", "#start-time", "#end-time", "#reset-trim", "#play-selection", "#rotate-left", "#rotate-right", "#flip-horizontal", "#flip-vertical", "#video-speed", "#crop-preset", "#crop-left", "#crop-top", "#crop-width", "#crop-height", "#select-crop", "#reset-transform", "#video-format", "#export-engine", "#play-pause", "#step-back", "#step-forward", "#seek-position"];
let current, currentBlob, currentUrl, state = "loading", recorder, recordingStream, recordingTimer, exportAbort, previewSelection = false;
let metadataRevision = 0, editRotation = 0, capabilityRevision = 0, cropMode = false, cropDrag, frameRequest, capabilityTimer;
const editedCanvas = $("#edited-preview");
function timeLabel(value) {
  const minutes = Math.floor(value / 60), seconds = (value % 60).toFixed(1).padStart(4, "0");
  return `${minutes}:${seconds}`;
}
function message(text, error = false) { status.textContent = text; status.classList.toggle("error", error); }
function setState(next) {
  state = next;
  document.body.dataset.state = next;
  const busy = next !== "ready";
  document.body.setAttribute("aria-busy", String(busy));
  $("#record").disabled = busy || !navigator.mediaDevices?.getDisplayMedia || !supportedRecordingType();
  $("#record").title = !navigator.mediaDevices?.getDisplayMedia ? "Screen sharing is unavailable in this browser." : !supportedRecordingType() ? "A supported WebM encoder is unavailable in this browser." : "Choose a tab, window, or screen to record";
  const canExport = supportsFastVideoExport() || Boolean(supportedRecordingType());
  $("#export-video").title = canExport ? "Export the edited clip" : "A supported video encoder is unavailable. You can still save the original.";
  $("#import-video").disabled = $("#empty-import").disabled = busy;
  $("#save-source").disabled = busy || !currentBlob;
  $("#export-video").disabled = busy || !currentBlob || current?.previewUnavailable || !canExport;
  for (const selector of editingControls) $(selector).disabled = busy || !current || current.previewUnavailable;
  const hasPreview = Boolean(current && !current.previewUnavailable);
  editedCanvas.hidden = !hasPreview || next === "recording";
  $("#preview-transport").hidden = !hasPreview || next === "recording";
  if (next !== "ready") stopCropSelection();
  $("#capture-audio").disabled = busy;
  $("#stop-record").hidden = next !== "recording";
  $("#cancel-export").hidden = next !== "exporting";
  $("#export-progress").hidden = next !== "exporting";
  $("#recording-indicator").hidden = next !== "recording";
  for (const button of document.querySelectorAll(".history-row button")) button.disabled = busy;
  $("#save-local").disabled = busy;
  $("#refresh-history").disabled = busy;
}
function transformSettings() {
  return { rotation: editRotation, flipH: $("#flip-horizontal").checked, flipV: $("#flip-vertical").checked,
    speed: Number($("#video-speed").value), crop: { left: Number($("#crop-left").value), top: Number($("#crop-top").value), width: Number($("#crop-width").value), height: Number($("#crop-height").value) } };
}
function outputSettings() {
  const edits = transformSettings();
  const geometry = videoGeometry(current.width, current.height, edits);
  const limit = $("#resolution").value;
  const factor = limit === "original" ? 1 : Math.min(1, Number(limit) / geometry.crop.height, Number(limit) * 16 / 9 / geometry.crop.width);
  return { ...edits, format: $("#video-format").value, engine: $("#export-engine").value,
    width: Math.max(2, Math.round(geometry.crop.width * factor / 2) * 2), height: Math.max(2, Math.round(geometry.crop.height * factor / 2) * 2), fps: Number($("#fps").value), quality: $("#quality").value, mute: $("#mute-export").checked, mono: $("#mono-export").checked, volume: Number($("#volume").value) / 100 };
}
function paintPreview() {
  if (!current || current.previewUnavailable || preview.srcObject || preview.readyState < 2) return;
  try {
    const geometry = videoGeometry(current.width, current.height, transformSettings());
    const factor = Math.min(1, 1280 / geometry.width, 720 / geometry.height);
    const width = Math.max(2, Math.round(geometry.width * factor)), height = Math.max(2, Math.round(geometry.height * factor));
    if (editedCanvas.width !== width) editedCanvas.width = width;
    if (editedCanvas.height !== height) editedCanvas.height = height;
    const displayScale = Math.min(editedCanvas.parentElement.clientWidth / width, editedCanvas.parentElement.clientHeight / height);
    editedCanvas.style.width = `${Math.max(1, width * displayScale)}px`; editedCanvas.style.height = `${Math.max(1, height * displayScale)}px`;
    drawVideoFrame(editedCanvas.getContext("2d", { alpha: false }), preview, geometry, width, height);
    $("#seek-position").value = preview.currentTime;
    $("#preview-time").textContent = `${timeLabel(preview.currentTime)} / ${timeLabel(current.duration)}`;
  } catch { /* Invalid crop fields stay editable until the user completes them. */ }
}
function followPreviewFrames() {
  if (frameRequest) frameRequest.video.cancelVideoFrameCallback(frameRequest.id);
  if (preview.paused || preview.srcObject || !current || current.previewUnavailable) return;
  const video = preview;
  const next = () => { if (video !== preview) return; paintPreview(); if (previewSelection && video.currentTime >= current.trimEnd) { video.pause(); previewSelection = false; } if (!video.paused) frameRequest = { video, id: video.requestVideoFrameCallback(next) }; };
  frameRequest = { video, id: video.requestVideoFrameCallback(next) };
}
async function refreshCapabilities() {
  const revision = ++capabilityRevision;
  let settings;
  try { settings = current && !current.previewUnavailable ? outputSettings() : {}; } catch { return; }
  const capabilities = await probeVideoExport(settings);
  if (revision !== capabilityRevision) return;
  const names = { avc: "H.264", vp8: "VP8", vp9: "VP9", av1: "AV1", aac: "AAC", opus: "Opus" };
  const label = entry => entry ? `${names[entry.video]}${entry.audio ? ` + ${names[entry.audio]}` : " · muted only"}` : "unavailable";
  $("#export-capabilities").textContent = capabilities.available ? `Fast WebM: ${label(capabilities.webm)}. MP4: ${label(capabilities.mp4)}.${capabilities.mp4 && (capabilities.mp4.video !== "avc" || capabilities.mp4.audio !== "aac") ? " MP4 uses browser codecs; playback outside Chrome may vary." : ""}` : "Fast encoding unavailable. WebM compatibility export remains available when Chrome supports it.";
  $("#video-format option[value=mp4]").disabled = !capabilities.mp4;
}
function stopCropSelection() {
  cropMode = false; cropDrag = null;
  editedCanvas.classList.remove("cropping"); $("#crop-outline").hidden = true; $("#select-crop").setAttribute("aria-pressed", "false");
}

function updateTrimUI() {
  if (!current) return;
  const start = current.trimStart, end = current.trimEnd;
  $("#trim-start").value = $("#start-time").value = start;
  $("#trim-end").value = $("#end-time").value = end;
  $("#start-label").textContent = timeLabel(start);
  $("#end-label").textContent = timeLabel(end);
  $("#selection-duration").textContent = `${timeLabel((end - start) / Number($("#video-speed").value))} output`;
  $("#trim-selected").style.left = `${start / current.duration * 100}%`;
  $("#trim-selected").style.right = `${(1 - end / current.duration) * 100}%`;
}
async function rememberEdits() {
  if (!current) return;
  current.title = $("#video-title").value.trim() || "Untitled recording";
  try { current.exportOptions = outputSettings(); } catch (error) { message(error.message, true); return; }
  if (!current.persisted) return;
  const revision = ++metadataRevision;
  try { await updateVideo(current.id, { title: current.title, trimStart: current.trimStart, trimEnd: current.trimEnd, exportOptions: current.exportOptions, resolution: $("#resolution").value }); }
  catch { if (revision === metadataRevision) message("Your edit settings could not be saved. The source is safe; you can still export.", true); }
}
function releasePreview() {
  preview.pause();
  if (frameRequest) { frameRequest.video.cancelVideoFrameCallback(frameRequest.id); frameRequest = null; }
  preview.srcObject = null;
  preview.removeAttribute("src"); preview.load();
  if (currentUrl) URL.revokeObjectURL(currentUrl);
  currentUrl = undefined;
}
async function showVideo(record, blob) {
  // Prepare the complete media surface off-screen. A metadata/seek failure must
  // not replace the only in-memory source or leave the visible controls broken.
  const nextRecord = { ...record, persisted: record.persisted !== false };
  nextRecord.trimStart = Math.max(0, Math.min(nextRecord.duration - .1, nextRecord.trimStart || 0));
  nextRecord.trimEnd = Math.max(nextRecord.trimStart + .1, Math.min(nextRecord.duration, nextRecord.trimEnd || nextRecord.duration));
  const staged = preview.cloneNode(false), nextUrl = URL.createObjectURL(blob);
  let committed = false;
  staged.srcObject = null; staged.controls = false; staged.hidden = false; staged.tabIndex = -1;
  staged.muted = Boolean(nextRecord.exportOptions?.mute);
  staged.volume = Math.min(1, nextRecord.exportOptions?.volume ?? 1);
  try {
    const ready = mediaEvent(staged, "loadedmetadata");
    ready.catch(() => {}); // A synchronous browser assignment failure is handled below.
    staged.src = nextUrl;
    await ready;
    if (!Number.isFinite(staged.duration)) await seekVideo(staged, 1e10);
    await seekVideo(staged, nextRecord.trimStart);

    releasePreview();
    preview.replaceWith(staged); preview = staged; bindPreview(preview);
    current = nextRecord; currentBlob = blob; currentUrl = nextUrl;
    committed = true;
    previewSelection = false;
    $("#empty-state").hidden = true; $("#timeline").hidden = false;
    $("#video-title").value = current.title;
    $("#video-meta").textContent = `${current.width} × ${current.height} · ${timeLabel(current.duration)} · ${(blob.size / 1024 / 1024).toFixed(1)} MB`;
    for (const selector of ["#trim-start", "#trim-end", "#start-time", "#end-time"]) $(selector).max = current.duration;
    $("#resolution").value = current.resolution || (current.width * current.height > 16_777_216 ? "1080" : "original");
    $("#quality").value = current.exportOptions?.quality || "balanced";
    $("#fps").value = String(current.exportOptions?.fps || 30);
    $("#mute-export").checked = Boolean(current.exportOptions?.mute);
    $("#mono-export").checked = Boolean(current.exportOptions?.mono);
    $("#volume").value = Math.round((current.exportOptions?.volume ?? 1) * 100);
    $("#volume-value").textContent = `${$("#volume").value}%`;
    editRotation = current.exportOptions?.rotation || 0;
    $("#flip-horizontal").checked = Boolean(current.exportOptions?.flipH);
    $("#flip-vertical").checked = Boolean(current.exportOptions?.flipV);
    $("#video-speed").value = String(current.exportOptions?.speed || 1);
    const crop = { left: 0, top: 0, width: 100, height: 100, ...current.exportOptions?.crop };
    for (const [name, value] of Object.entries(crop)) $("#crop-" + name).value = value;
    $("#crop-preset").value = crop.left === 0 && crop.top === 0 && crop.width === 100 && crop.height === 100 ? "full" : "custom";
    $("#video-format").value = current.exportOptions?.format || "webm";
    $("#export-engine").value = current.exportOptions?.engine || "auto";
    $("#seek-position").max = current.duration;
    preview.playbackRate = Number($("#video-speed").value); preview.preservesPitch = false;
    $("#save-local").hidden = current.persisted;
    if (current.persisted) history.replaceState(null, "", `video-editor.html?id=${encodeURIComponent(current.id)}`);
    else history.replaceState(null, "", "video-editor.html");
    updateTrimUI(); setState("ready"); paintPreview(); void refreshCapabilities();
    await refreshHistory();
  } finally {
    if (!committed) {
      staged.pause(); staged.removeAttribute("src"); staged.load();
      URL.revokeObjectURL(nextUrl);
    }
  }
}
async function recoverRecordingPreview(error, recoveryBlob) {
  let extra = "";
  const hadPrevious = Boolean(currentBlob);
  try {
    if (currentBlob && !current?.previewUnavailable) await showVideo(current, currentBlob);
    else { releasePreview(); preview.hidden = true; $("#empty-state").hidden = false; }
  } catch {
    // Restoration is best effort. Keep current/currentBlob even if the browser
    // cannot build a preview, so Save original and Retry history remain usable.
    releasePreview(); preview.hidden = true; $("#empty-state").hidden = false; $("#timeline").hidden = true;
    extra = " The previous video is still available through Save original, but its preview could not reload.";
  } finally {
    if (!hadPrevious && recoveryBlob?.size) {
      // A recording can be downloadable even if Chrome cannot reopen it. Keep
      // its raw bytes without claiming it is a playable editable history item.
      current = { id: crypto.randomUUID(), title: `Recording recovery ${new Date().toLocaleString()}`, originalName: "recording.webm", persisted: false, previewUnavailable: true };
      currentBlob = recoveryBlob;
      $("#video-title").value = current.title;
      $("#video-meta").textContent = `Preview unavailable · ${(recoveryBlob.size / 1024 / 1024).toFixed(1)} MB`;
      $("#timeline").hidden = true; $("#save-local").hidden = true;
      history.replaceState(null, "", "video-editor.html");
      extra = " Your recording bytes are still available through Save original. Save them before closing this tab.";
    }
    setState("ready");
    await refreshHistory();
  }
  message(`${error.message}${extra}`, true);
}

async function refreshHistory() {
  const list = $("#video-history");
  try {
    const videos = await listVideos();
    list.replaceChildren();
    if (!videos.length) { const empty = document.createElement("p"); empty.className = "hint"; empty.textContent = "Your recordings and imported videos appear here."; list.append(empty); }
    for (const record of videos) {
      const row = document.createElement("div"); row.className = "history-row";
      row.setAttribute("aria-current", String(record.id === current?.id));
      const open = document.createElement("button");
      open.disabled = state !== "ready";
      const title = document.createElement("span"); title.className = "video-name"; title.textContent = record.title;
      const detail = document.createElement("small"); detail.textContent = `${timeLabel(record.duration)} · ${new Date(record.createdAt).toLocaleDateString()}`;
      open.append(title, detail);
      open.addEventListener("click", () => openSaved(record.id));
      const remove = document.createElement("button"); remove.className = "delete-video"; remove.textContent = "×"; remove.setAttribute("aria-label", `Delete ${record.title}`); remove.disabled = state !== "ready";
      remove.addEventListener("click", async () => {
        if (!confirm(`Delete “${record.title}” from WinShot history? Exported files stay on your computer.`)) return;
        try {
          await deleteVideo(record.id);
          if (current?.id === record.id) { current.persisted = false; $("#save-local").hidden = false; history.replaceState(null, "", "video-editor.html"); }
          await refreshHistory(); message("Removed from history. Your open preview and exported files are still available.");
        } catch { message("This video could not be deleted. Try again.", true); }
      });
      row.append(open, remove); list.append(row);
    }
  } catch { list.textContent = "Video history is unavailable. You can still import, record, and export."; }
}
function canReplaceVideo() {
  return current?.persisted !== false || confirm("This video is not saved in WinShot history. Save the original or retry saving before replacing it. Discard this open copy and continue?");
}
async function openSaved(id) {
  if (id !== current?.id && !canReplaceVideo()) return;
  if (state !== "ready" && state !== "loading") return;
  setState("loading");
  try {
    const saved = await getVideo(id);
    if (!saved) throw new Error("This video is no longer in history. Import its saved file to edit it again.");
    await showVideo(saved.record, saved.blob);
    message("Video opened. Trim a range or save the original.");
  } catch (error) { setState("ready"); await refreshHistory(); message(error.message, true); }
}
async function keepVideo(blob, { title, knownDuration, source = "import", originalName }) {
  const metadata = await inspectVideo(blob, knownDuration);
  let record = { id: crypto.randomUUID(), title, createdAt: new Date().toISOString(), ...metadata, trimStart: 0, trimEnd: metadata.duration, source, originalName, persisted: true };
  let failed = false;
  try { record = await saveVideo(blob, record); }
  catch { record.persisted = false; failed = true; }
  await showVideo(record, blob);
  message(failed ? "Video opened, but history storage is full or unavailable. Save the original before closing this tab, or free space and retry." : "Video saved locally. Preview it, trim a range, then export.", failed);
}
$("#import-video").addEventListener("click", () => $("#video-file").click());
$("#empty-import").addEventListener("click", () => $("#video-file").click());
$("#video-file").addEventListener("change", async event => {
  const file = event.target.files[0]; event.target.value = "";
  if (!file || state !== "ready" || !canReplaceVideo()) return;
  setState("loading"); message("Opening video…");
  try { await keepVideo(file, { title: file.name.replace(/\.[^.]+$/, "") || "Imported video", originalName: file.name }); }
  catch (error) { setState("ready"); message(error.message, true); }
});
$("#save-local").addEventListener("click", async () => {
  if (!current || !currentBlob) return;
  setState("loading");
  try { current = { ...await saveVideo(currentBlob, { ...current, persisted: true }), persisted: true }; $("#save-local").hidden = true; history.replaceState(null, "", `video-editor.html?id=${encodeURIComponent(current.id)}`); message("Video saved to history."); }
  catch { message("History storage is still full or unavailable. Save the original before closing this tab.", true); }
  finally { setState("ready"); await refreshHistory(); }
});
$("#record").addEventListener("click", async () => {
  if (state !== "ready" || !canReplaceVideo()) return;
  const mimeType = supportedRecordingType();
  if (!navigator.mediaDevices?.getDisplayMedia || !mimeType) { message("Screen recording is unavailable in this browser. Import a video to edit it instead.", true); return; }
  setState("choosing"); message("Choose a tab, window, or screen in Chrome’s sharing dialog.");
  try {
    recordingStream = await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: { ideal: 30, max: 30 } }, audio: $("#capture-audio").checked });
    if (!recordingStream.getVideoTracks().length) throw new Error("No video source was shared. Choose a tab, window, or screen.");
    const parts = []; let bytes = 0, stopReason = "", recordingError;
    const started = performance.now();
    recorder = new MediaRecorder(recordingStream, { mimeType, videoBitsPerSecond: 8_000_000 });
    recorder.ondataavailable = event => {
      if (event.data.size) { parts.push(event.data); bytes += event.data.size; }
      if (bytes >= MAX_VIDEO_BYTES && recorder.state !== "inactive") { stopReason = "The recording reached the 1 GB limit."; recorder.stop(); }
    };
    recorder.onerror = event => { recordingError = event.error || new Error("Chrome stopped recording unexpectedly."); if (recorder.state !== "inactive") recorder.stop(); };
    recorder.onstop = async () => {
      clearInterval(recordingTimer);
      const duration = (performance.now() - started) / 1000;
      recordingStream.getTracks().forEach(track => track.stop()); recordingStream = null; recorder = null;
      setState("loading"); message("Finishing your recording…");
      const recordedBlob = new Blob(parts, { type: mimeType });
      try {
        await keepVideo(recordedBlob, { title: `Recording ${new Date().toLocaleString()}`, knownDuration: duration, source: "recording" });
        if (recordingError || stopReason) message(`${recordingError?.message || stopReason} The playable recording was kept; check it before exporting.`, true);
      } catch (error) { await recoverRecordingPreview(error, recordedBlob); }
    };
    recordingStream.getVideoTracks()[0].addEventListener("ended", () => { if (recorder?.state !== "inactive") recorder?.stop(); }, { once: true });
    preview.pause(); preview.removeAttribute("src"); preview.srcObject = recordingStream; preview.controls = false; preview.muted = true; preview.hidden = false; $("#empty-state").hidden = true;
    // Recording consumes the shared stream directly. Chrome may defer a hidden
    // preview play promise after activating the selected source tab. Never let
    // that optional preview prevent the recorder from starting.
    const sharedStream = recordingStream;
    preview.play().catch(() => { if (state === "recording" && recordingStream === sharedStream) message("Recording continues. The live preview is unavailable; use Stop recording or Chrome’s Stop sharing button."); });
    recorder.start(1000); $("#recording-time").textContent = "Recording · 0:00.0"; setState("recording");
    message(recordingStream.getAudioTracks().length ? "Recording video and shared audio. Stop here or use Chrome’s Stop sharing button." : "Recording video. This source did not provide shared audio.");
    recordingTimer = setInterval(() => {
      const duration = (performance.now() - started) / 1000;
      $("#recording-time").textContent = `Recording · ${timeLabel(duration)}`;
      if (duration >= 1800 && recorder?.state !== "inactive") { stopReason = "The recording reached the 30 minute limit."; recorder.stop(); }
    }, 200);
  } catch (error) {
    if (recorder) { recorder.onstop = null; recorder.onerror = null; if (recorder.state !== "inactive") recorder.stop(); }
    recordingStream?.getTracks().forEach(track => track.stop()); recordingStream = null; recorder = null;
    await recoverRecordingPreview(error);
    message(error.name === "NotAllowedError" || error.name === "AbortError" ? "Sharing cancelled or not allowed. Choose Record screen to try again." : `Recording could not start: ${error.message}`, !["NotAllowedError", "AbortError"].includes(error.name));
  }
});
$("#stop-record").addEventListener("click", () => { if (recorder && recorder.state !== "inactive") { $("#stop-record").disabled = true; recorder.stop(); $("#stop-record").disabled = false; } });
function setTrim(which, value) {
  if (!current || !Number.isFinite(value)) return;
  preview.pause(); previewSelection = false;
  if (which === "start") current.trimStart = Math.max(0, Math.min(current.trimEnd - .1, value));
  else current.trimEnd = Math.min(current.duration, Math.max(current.trimStart + .1, value));
  updateTrimUI();
  preview.currentTime = which === "start" ? current.trimStart : Math.max(current.trimStart, current.trimEnd - .05);
  rememberEdits();
}
for (const [selector, which] of [["#trim-start", "start"], ["#trim-end", "end"], ["#start-time", "start"], ["#end-time", "end"]]) $(selector).addEventListener("input", event => setTrim(which, Number(event.target.value)));
$("#reset-trim").addEventListener("click", () => { current.trimStart = 0; current.trimEnd = current.duration; updateTrimUI(); rememberEdits(); });
$("#play-selection").addEventListener("click", async () => {
  preview.currentTime = current.trimStart; previewSelection = true;
  try { await preview.play(); } catch { message("Playback could not start. Use the video’s Play button.", true); }
});
function bindPreview(element) {
  element.addEventListener("timeupdate", () => {
    if (element !== preview) return;
    paintPreview();
    if (!current || state === "recording") return;
    $("#playhead").style.left = `${Math.min(1, preview.currentTime / current.duration) * 100}%`;
    if (previewSelection && preview.currentTime >= current.trimEnd) { preview.pause(); previewSelection = false; }
  });
  for (const event of ["loadeddata", "seeked", "pause", "play"]) element.addEventListener(event, () => {
    if (element !== preview) return;
    $("#play-pause").textContent = preview.paused ? "▶" : "❚❚";
    $("#play-pause").setAttribute("aria-label", preview.paused ? "Play video" : "Pause video");
    requestAnimationFrame(paintPreview); followPreviewFrames();
  });
  element.addEventListener("error", () => { if (element !== preview) return; if (current && state === "ready") message("Chrome could not play this video. Save the original or import a supported video.", true); });
}
bindPreview(preview);
function queueCapabilities() { clearTimeout(capabilityTimer); capabilityTimer = setTimeout(() => { void refreshCapabilities(); }, 250); }
function applyVideoEdits() {
  if (!current || current.previewUnavailable) return;
  try {
    outputSettings();
    preview.playbackRate = Number($("#video-speed").value); preview.preservesPitch = false;
    paintPreview(); updateTrimUI(); void rememberEdits(); queueCapabilities();
  } catch (error) { message(error.message, true); }
}
function setCropFields(crop) { for (const [name, value] of Object.entries(crop)) $("#crop-" + name).value = Math.round(value * 10) / 10; }
$("#rotate-left").addEventListener("click", () => { editRotation = (editRotation + 270) % 360; applyVideoEdits(); });
$("#rotate-right").addEventListener("click", () => { editRotation = (editRotation + 90) % 360; applyVideoEdits(); });
for (const selector of ["#flip-horizontal", "#flip-vertical", "#video-speed"]) $(selector).addEventListener("change", applyVideoEdits);
for (const selector of ["#crop-left", "#crop-top", "#crop-width", "#crop-height"]) $(selector).addEventListener("input", () => { $("#crop-preset").value = "custom"; applyVideoEdits(); });
$("#crop-preset").addEventListener("change", () => {
  const preset = $("#crop-preset").value;
  if (preset === "custom") return;
  if (preset === "full") setCropFields({ left: 0, top: 0, width: 100, height: 100 });
  else {
    const geometry = videoGeometry(current.width, current.height, { rotation: editRotation });
    const ratio = { square: 1, wide: 16 / 9, portrait: 9 / 16 }[preset];
    const width = Math.min(geometry.rotatedWidth, geometry.rotatedHeight * ratio), height = width / ratio;
    setCropFields({ left: (1 - width / geometry.rotatedWidth) * 50, top: (1 - height / geometry.rotatedHeight) * 50, width: width / geometry.rotatedWidth * 100, height: height / geometry.rotatedHeight * 100 });
  }
  applyVideoEdits();
});
$("#reset-transform").addEventListener("click", () => {
  editRotation = 0; $("#flip-horizontal").checked = $("#flip-vertical").checked = false; $("#video-speed").value = "1"; $("#crop-preset").value = "full";
  setCropFields({ left: 0, top: 0, width: 100, height: 100 }); stopCropSelection(); applyVideoEdits();
});
$("#video-format").addEventListener("change", () => { if ($("#video-format").value === "mp4" && $("#export-engine").value === "realtime") $("#export-engine").value = "auto"; void rememberEdits(); });
$("#export-engine").addEventListener("change", () => { if ($("#export-engine").value === "realtime") $("#video-format").value = "webm"; void rememberEdits(); });
$("#play-pause").addEventListener("click", async () => {
  if (state !== "ready" || !current || current.previewUnavailable) return;
  if (preview.paused) { previewSelection = false; try { await preview.play(); } catch { message("Playback could not start. Try Play again.", true); } }
  else preview.pause();
});
for (const [selector, direction] of [["#step-back", -1], ["#step-forward", 1]]) $(selector).addEventListener("click", () => {
  preview.pause(); previewSelection = false;
  preview.currentTime = Math.max(0, Math.min(current.duration, preview.currentTime + direction * Number($("#video-speed").value) / Number($("#fps").value)));
});
$("#seek-position").addEventListener("input", () => { preview.pause(); previewSelection = false; preview.currentTime = Number($("#seek-position").value); });
$("#select-crop").addEventListener("click", () => {
  if (cropMode) return stopCropSelection();
  preview.pause(); cropMode = true; editedCanvas.classList.add("cropping"); $("#select-crop").setAttribute("aria-pressed", "true"); message("Drag a rectangle on the preview, or adjust the crop fields. Escape cancels selection.");
});
function cropPoint(event, rectangle) { return { x: Math.max(0, Math.min(1, (event.clientX - rectangle.left) / rectangle.width)), y: Math.max(0, Math.min(1, (event.clientY - rectangle.top) / rectangle.height)) }; }
editedCanvas.addEventListener("pointerdown", event => {
  if (!cropMode || state !== "ready") return;
  const rect = editedCanvas.getBoundingClientRect(); cropDrag = { rect, start: cropPoint(event, rect), crop: transformSettings().crop };
  editedCanvas.setPointerCapture(event.pointerId);
});
editedCanvas.addEventListener("pointermove", event => {
  if (!cropDrag) return;
  const end = cropPoint(event, cropDrag.rect), parent = editedCanvas.parentElement.getBoundingClientRect(), outline = $("#crop-outline"), rect = cropDrag.rect;
  outline.hidden = false;
  outline.style.left = `${rect.left - parent.left + Math.min(cropDrag.start.x, end.x) * rect.width}px`;
  outline.style.top = `${rect.top - parent.top + Math.min(cropDrag.start.y, end.y) * rect.height}px`;
  outline.style.width = `${Math.abs(cropDrag.start.x - end.x) * rect.width}px`; outline.style.height = `${Math.abs(cropDrag.start.y - end.y) * rect.height}px`;
});
editedCanvas.addEventListener("pointerup", event => {
  if (!cropDrag) return;
  const end = cropPoint(event, cropDrag.rect), start = cropDrag.start, crop = cropDrag.crop;
  if (Math.abs(end.x - start.x) * cropDrag.rect.width < 4 || Math.abs(end.y - start.y) * cropDrag.rect.height < 4) { cropDrag = null; $("#crop-outline").hidden = true; return; }
  const left = Math.round((crop.left + Math.min(start.x, end.x) * crop.width) * 10) / 10, top = Math.round((crop.top + Math.min(start.y, end.y) * crop.height) * 10) / 10;
  setCropFields({ left, top, width: Math.min(100 - left, Math.abs(start.x - end.x) * crop.width), height: Math.min(100 - top, Math.abs(start.y - end.y) * crop.height) });
  $("#crop-preset").value = "custom"; stopCropSelection(); applyVideoEdits(); message("Crop updated. Your original is untouched.");
});
editedCanvas.addEventListener("pointercancel", stopCropSelection);
addEventListener("keydown", event => { if (event.key === "Escape") stopCropSelection(); });
for (const selector of ["#video-title", "#resolution", "#quality", "#fps", "#mute-export", "#mono-export"]) $(selector).addEventListener("change", () => { preview.muted = $("#mute-export").checked; void rememberEdits().then(refreshHistory); paintPreview(); queueCapabilities(); });
$("#volume").addEventListener("input", () => { $("#volume-value").textContent = `${$("#volume").value}%`; preview.volume = Math.min(1, Number($("#volume").value) / 100); });
$("#volume").addEventListener("change", rememberEdits);
function fileName(extension, suffix = "") {
  const title = (current?.title || "WinShot video").replace(/[<>:"/\\|?*\x00-\x1f]/g, "-").slice(0, 120);
  return `${title}${suffix}.${extension}`;
}
async function download(blob, name, signal) {
  const url = URL.createObjectURL(blob);
  try {
    if (signal?.aborted) throw signal.reason;
    const id = await chrome.downloads.download({ url, filename: name, saveAs: true });
    await new Promise((resolve, reject) => {
      let timer;
      const clean = () => { clearTimeout(timer); chrome.downloads.onChanged.removeListener(changed); signal?.removeEventListener("abort", cancel); };
      const done = () => { clean(); resolve(); };
      const fail = error => { clean(); reject(error); };
      const changed = delta => { if (delta.id !== id) return; if (delta.state?.current === "complete") done(); else if (delta.state?.current === "interrupted") fail(signal?.aborted ? signal.reason || new DOMException("Cancelled", "AbortError") : new Error("The download was interrupted. Your original is safe; try exporting again.")); };
      const cancel = () => { chrome.downloads.cancel(id).catch(() => {}).finally(() => fail(signal.reason || new DOMException("Cancelled", "AbortError"))); };
      chrome.downloads.onChanged.addListener(changed);
      signal?.addEventListener("abort", cancel, { once: true });
      timer = setTimeout(() => { chrome.downloads.cancel(id).catch(() => {}); fail(new Error("The download did not finish. Your original is safe.")); }, 10 * 60 * 1000);
      chrome.downloads.search({ id }).then(items => { if (items[0]?.state === "complete") done(); else if (items[0]?.state === "interrupted") fail(new Error("The download was interrupted.")); }).catch(fail);
      if (signal?.aborted) cancel();
    });
  } finally { URL.revokeObjectURL(url); await releaseVideoExport(blob); }
}

$("#save-source").addEventListener("click", async () => {
  try {
    const originalExtension = current.originalName?.match(/\.([a-z0-9]{1,8})$/i)?.[1] || ({ "video/mp4": "mp4", "video/quicktime": "mov", "video/ogg": "ogv", "video/x-matroska": "mkv" }[currentBlob.type.split(";")[0]] || "webm");
    await download(currentBlob, fileName(originalExtension)); message("Original video sent to Chrome downloads."); }
  catch (error) { message(`The original could not be downloaded: ${error.message}`, true); }
});
$("#export-video").addEventListener("click", async () => {
  if (!currentBlob || state !== "ready" || current.previewUnavailable) return;
  preview.pause(); previewSelection = false; setState("exporting"); exportAbort = new AbortController();
  $("#export-progress").value = 0;
  let exported;
  try {
    const settings = outputSettings();
    let usedEngine = settings.engine === "realtime" ? "realtime" : "fast";
    message(`Preparing ${settings.format.toUpperCase()} export…`);
    exported = await exportVideoClip(currentBlob, { start: current.trimStart, end: current.trimEnd, ...settings, signal: exportAbort.signal,
      onEngine: info => { usedEngine = info.engine; },
      onProgress: fraction => { $("#export-progress").value = fraction; message(`${usedEngine === "fast" ? "Fast export" : "Compatibility export"} · ${Math.round(fraction * 100)}%${usedEngine === "realtime" ? " · keep this tab visible" : ""}`); } });
    if (exportAbort.signal.aborted) throw exportAbort.signal.reason;
    const info = getVideoExportInfo(exported);
    message(`Saving ${settings.format.toUpperCase()} clip…`);
    await download(exported, fileName(settings.format, "-clip"), exportAbort.signal);
    message(`Clip exported as ${settings.format.toUpperCase()}${info?.engine === "fast" ? ` · encoded in ${(info.elapsedMs / 1000).toFixed(1)}s` : ""}. Your original and edits are still in history.`);
  } catch (error) { message(error.name === "AbortError" ? "Export cancelled. Your source video and edits are safe." : error.message, error.name !== "AbortError"); }
  finally { if (exported) await releaseVideoExport(exported); exportAbort = null; setState("ready"); }
});

$("#cancel-export").addEventListener("click", () => { exportAbort?.abort(new DOMException("Cancelled", "AbortError")); });
$("#refresh-history").addEventListener("click", refreshHistory);
addEventListener("beforeunload", event => { if (["recording", "exporting"].includes(state) || current?.persisted === false) { event.preventDefault(); event.returnValue = ""; } });
addEventListener("pagehide", () => { exportAbort?.abort(); recordingStream?.getTracks().forEach(track => track.stop()); clearInterval(recordingTimer); releasePreview(); });
addEventListener("keydown", event => {
  if (event.code !== "Space" || state !== "ready" || !current || /INPUT|SELECT|TEXTAREA|BUTTON/.test(event.target.tagName)) return;
  event.preventDefault(); if (preview.paused) preview.play().catch(() => {}); else preview.pause();
});
const id = new URL(location.href).searchParams.get("id");
if (id) await openSaved(id);
else { setState("ready"); await refreshHistory(); void refreshCapabilities(); message("Record a screen or import a video. Everything stays on your device."); }

void cleanupStaleVideoExports();

new ResizeObserver(() => requestAnimationFrame(paintPreview)).observe(editedCanvas.parentElement);
