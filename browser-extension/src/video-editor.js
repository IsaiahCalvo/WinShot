import { saveVideo, getVideo, listVideos, updateVideo, deleteVideo } from "./video-store.js";
import { MAX_VIDEO_BYTES, inspectVideo, supportedRecordingType, exportVideoClip, mediaEvent, seekVideo } from "./video-tools.js";

const $ = selector => document.querySelector(selector);
let preview = $("#preview");
const status = $("#status");
const editingControls = ["#video-title", "#resolution", "#quality", "#fps", "#mute-export", "#mono-export", "#volume", "#trim-start", "#trim-end", "#start-time", "#end-time", "#reset-trim", "#play-selection"];
let current, currentBlob, currentUrl, state = "loading", recorder, recordingStream, recordingTimer, exportAbort, previewSelection = false;
let metadataRevision = 0;
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
  $("#export-video").title = supportedRecordingType() ? "Export selected clip as WebM" : "A supported WebM encoder is unavailable. You can still save the original.";
  $("#import-video").disabled = $("#empty-import").disabled = busy;
  $("#save-source").disabled = busy || !currentBlob;
  $("#export-video").disabled = busy || !currentBlob || current?.previewUnavailable || !supportedRecordingType();
  for (const selector of editingControls) $(selector).disabled = busy || !current || current.previewUnavailable;
  $("#capture-audio").disabled = busy;
  $("#stop-record").hidden = next !== "recording";
  $("#cancel-export").hidden = next !== "exporting";
  $("#export-progress").hidden = next !== "exporting";
  $("#recording-indicator").hidden = next !== "recording";
  for (const button of document.querySelectorAll(".history-row button")) button.disabled = busy;
  $("#save-local").disabled = busy;
  $("#refresh-history").disabled = busy;
}
function outputSettings() {
  const limit = $("#resolution").value;
  const factor = limit === "original" ? 1 : Math.min(1, Number(limit) / current.height, Number(limit) * 16 / 9 / current.width);
  return { width: Math.max(2, Math.round(current.width * factor / 2) * 2), height: Math.max(2, Math.round(current.height * factor / 2) * 2), fps: Number($("#fps").value), quality: $("#quality").value, mute: $("#mute-export").checked, mono: $("#mono-export").checked, volume: Number($("#volume").value) / 100 };
}
function updateTrimUI() {
  if (!current) return;
  const start = current.trimStart, end = current.trimEnd;
  $("#trim-start").value = $("#start-time").value = start;
  $("#trim-end").value = $("#end-time").value = end;
  $("#start-label").textContent = timeLabel(start);
  $("#end-label").textContent = timeLabel(end);
  $("#selection-duration").textContent = `${timeLabel(end - start)} selected`;
  $("#trim-selected").style.left = `${start / current.duration * 100}%`;
  $("#trim-selected").style.right = `${(1 - end / current.duration) * 100}%`;
}
async function rememberEdits() {
  if (!current) return;
  current.title = $("#video-title").value.trim() || "Untitled recording";
  current.exportOptions = outputSettings();
  if (!current.persisted) return;
  const revision = ++metadataRevision;
  try { await updateVideo(current.id, { title: current.title, trimStart: current.trimStart, trimEnd: current.trimEnd, exportOptions: current.exportOptions, resolution: $("#resolution").value }); }
  catch { if (revision === metadataRevision) message("Your edit settings could not be saved. The source is safe; you can still export.", true); }
}
function releasePreview() {
  preview.pause(); preview.srcObject = null;
  preview.removeAttribute("src"); preview.load();
  if (currentUrl) URL.revokeObjectURL(currentUrl);
  currentUrl = undefined;
}
async function showVideo(record, blob) {
  // Prepare the complete media surface off-screen. A metadata/seek failure must
  // not replace the only in-memory source or leave the visible controls broken.
  const nextRecord = { ...record, persisted: record.persisted !== false };
  if (!Number.isFinite(nextRecord.duration) || nextRecord.duration < .1)
    throw new Error("A playable video track of at least 0.1 seconds could not be read from this file.");
  nextRecord.trimStart = Math.max(0, Math.min(nextRecord.duration - .1, nextRecord.trimStart || 0));
  nextRecord.trimEnd = Math.max(nextRecord.trimStart + .1, Math.min(nextRecord.duration, nextRecord.trimEnd || nextRecord.duration));
  const staged = preview.cloneNode(false), nextUrl = URL.createObjectURL(blob);
  let committed = false;
  staged.srcObject = null; staged.controls = true; staged.hidden = false;
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
    $("#save-local").hidden = current.persisted;
    if (current.persisted) history.replaceState(null, "", `video-editor.html?id=${encodeURIComponent(current.id)}`);
    else history.replaceState(null, "", "video-editor.html");
    updateTrimUI(); setState("ready");
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
  const inspected = error.inspectedRecord;
  try {
    // Do not reopen bytes we already failed to preview. A duration-less stub
    // would seek to NaN; even a measured one can stall Save original on seeked.
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
      if (Number.isFinite(inspected?.duration) && inspected.width && inspected.height)
        Object.assign(current, { duration: inspected.duration, width: inspected.width, height: inspected.height });
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
  try { await showVideo(record, blob); }
  catch (error) { throw Object.assign(error, { inspectedRecord: record }); }
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
    if (!current || state === "recording") return;
    $("#playhead").style.left = `${Math.min(1, preview.currentTime / current.duration) * 100}%`;
    if (previewSelection && preview.currentTime >= current.trimEnd) { preview.pause(); previewSelection = false; }
  });
  element.addEventListener("error", () => { if (element !== preview) return; if (current && state === "ready") message("Chrome could not play this video. Save the original or import a supported video.", true); });
}
bindPreview(preview);
for (const selector of ["#video-title", "#resolution", "#quality", "#fps", "#mute-export", "#mono-export"]) $(selector).addEventListener("change", () => { preview.muted = $("#mute-export").checked; rememberEdits().then(refreshHistory); });
$("#volume").addEventListener("input", () => { $("#volume-value").textContent = `${$("#volume").value}%`; preview.volume = Math.min(1, Number($("#volume").value) / 100); });
$("#volume").addEventListener("change", rememberEdits);
function fileName(extension, suffix = "") {
  const title = (current?.title || "WinShot video").replace(/[<>:"/\\|?*\x00-\x1f]/g, "-").slice(0, 120);
  return `${title}${suffix}.${extension}`;
}
async function download(blob, name) {
  const url = URL.createObjectURL(blob);
  try { await chrome.downloads.download({ url, filename: name, saveAs: true }); }
  finally { setTimeout(() => URL.revokeObjectURL(url), 60_000); }
}
$("#save-source").addEventListener("click", async () => {
  try {
    const originalExtension = current.originalName?.match(/\.([a-z0-9]{1,8})$/i)?.[1] || ({ "video/mp4": "mp4", "video/quicktime": "mov", "video/ogg": "ogv", "video/x-matroska": "mkv" }[currentBlob.type.split(";")[0]] || "webm");
    await download(currentBlob, fileName(originalExtension)); message("Original video sent to Chrome downloads."); }
  catch (error) { message(`The original could not be downloaded: ${error.message}`, true); }
});
$("#export-video").addEventListener("click", async () => {
  if (!currentBlob || state !== "ready") return;
  preview.pause(); previewSelection = false; setState("exporting"); exportAbort = new AbortController();
  $("#export-progress").value = 0;
  const selected = current.trimEnd - current.trimStart;
  message(`Exporting ${timeLabel(selected)} of video. Keep this tab visible…`);
  try {
    const blob = await exportVideoClip(currentBlob, { start: current.trimStart, end: current.trimEnd, ...outputSettings(), signal: exportAbort.signal, onProgress: fraction => { $("#export-progress").value = fraction; message(`Exporting clip · ${Math.round(fraction * 100)}% · keep this tab visible`); } });
    if (exportAbort.signal.aborted) throw exportAbort.signal.reason;
    await download(blob, fileName("webm", "-clip"));
    message("Clip exported as WebM. Your original and trim settings are still in history.");
  } catch (error) { message(error.name === "AbortError" ? "Export cancelled. Your source video and trim settings are safe." : error.message, error.name !== "AbortError"); }
  finally { exportAbort = null; setState("ready"); }
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
else { setState("ready"); await refreshHistory(); message("Record a screen or import a video. Everything stays on your device."); }
