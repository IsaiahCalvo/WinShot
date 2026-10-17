const resources = new WeakMap(), active = new Set();
function aborted(signal) { return signal?.reason || new DOMException("Export cancelled. Your source is safe.", "AbortError"); }
async function removeTemporary(entry) {
  if (!entry || !navigator.storage?.getDirectory) return;
  try { const directory = await navigator.storage.getDirectory(); await directory.removeEntry(entry); } catch { /* An active file lock or unavailable profile storage is retried by its worker. */ }
}
export function supportsFastVideoExport() { return typeof Worker === "function" && typeof VideoEncoder === "function" && typeof VideoDecoder === "function"; }
export function getVideoExportInfo(blob) { return resources.get(blob)?.info || null; }
export async function releaseVideoExport(blob) {
  const resource = resources.get(blob);
  if (!resource) return;
  resources.delete(blob);
  await resource.release();
}
export async function cleanupStaleVideoExports() {
  if (!navigator.storage?.getDirectory) return;
  try {
    const directory = await navigator.storage.getDirectory();
    for await (const [name, handle] of directory.entries()) {
      if (!name.startsWith("winshot-export-") || !name.endsWith(".tmp") || handle.kind !== "file") continue;
      const file = await handle.getFile();
      if (Date.now() - file.lastModified > 6 * 60 * 60 * 1000) await directory.removeEntry(name).catch(() => {});
    }
  } catch { /* The encoder retains its small in-memory fallback. */ }
}
export async function probeVideoExport({ width = 1280, height = 720, fps = 30 } = {}) {
  if (!supportsFastVideoExport()) return { available: false, webm: null, mp4: null };
  return new Promise(resolve => {
    let worker;
    try { worker = new Worker(new URL("./video-export-worker.js", import.meta.url), { type: "module" }); }
    catch { resolve({ available: false, webm: null, mp4: null }); return; }
    const timer = setTimeout(() => { worker.terminate(); resolve({ available: false, webm: null, mp4: null }); }, 15000);
    const done = value => { clearTimeout(timer); worker.terminate(); resolve(value); };
    worker.onmessage = ({ data }) => { if (data.type === "capabilities") done(data.capabilities); else if (data.type === "error") done({ available: false, webm: null, mp4: null }); };
    worker.onerror = () => done({ available: false, webm: null, mp4: null });
    worker.postMessage({ type: "probe", width, height, fps });
  });
}
export async function exportVideoFast(blob, options = {}) {
  if (!supportsFastVideoExport()) { const error = new Error("Fast video export is unavailable in this Chrome. Choose WebM compatibility export."); error.code = "FAST_UNSUPPORTED"; throw error; }
  const { signal, onProgress = () => {}, onEngine = () => {}, ...settings } = options;
  if (signal?.aborted) throw aborted(signal);
  return new Promise((resolve, reject) => {
    let worker;
    try { worker = new Worker(new URL("./video-export-worker.js", import.meta.url), { type: "module" }); }
    catch { const error = new Error("Fast export workers are unavailable. Choose WebM compatibility export."); error.code = "FAST_UNSUPPORTED"; reject(error); return; }
    let entry, settled = false, released = false, idleTimer, releaseTimer;
    active.add(worker);
    function stop() { clearTimeout(idleTimer); clearTimeout(releaseTimer); signal?.removeEventListener("abort", cancel); worker.terminate(); active.delete(worker); }
    async function release() {
      if (released) return;
      released = true;
      await new Promise(done => {
        const timer = setTimeout(done, 3000);
        const message = ({ data }) => { if (data.type === "released") { clearTimeout(timer); worker.removeEventListener("message", message); done(); } };
        worker.addEventListener("message", message);
        worker.postMessage({ type: "release" });
      });
      stop(); await removeTemporary(entry);
    }
    async function fail(error) {
      if (settled) return;
      settled = true; clearTimeout(idleTimer); signal?.removeEventListener("abort", cancel);
      worker.postMessage({ type: "cancel" });
      await release();
      reject(error);
    }
    function cancel() { void fail(aborted(signal)); }
    function watchdog() {
      clearTimeout(idleTimer);
      idleTimer = setTimeout(() => { void fail(new Error("Fast export stopped responding. Your source is safe. Try a shorter clip or WebM compatibility export.")); }, 60000);
    }
    signal?.addEventListener("abort", cancel, { once: true });
    worker.onerror = event => { const error = new Error(event.message || "Fast video export could not start."); error.code = "FAST_UNSUPPORTED"; void fail(error); };
    worker.onmessage = ({ data }) => {
      if (data.type === "storage") entry = data.entry;
      if (settled && data.type !== "released") return;
      watchdog();
      if (data.type === "progress") onProgress(data.progress);
      if (data.type === "encoding") onEngine(data);
      if (data.type === "error") { const error = new Error(data.message); error.name = data.name || "Error"; error.code = data.code; void fail(signal?.aborted ? aborted(signal) : error); }
      if (data.type === "result") {
        settled = true; clearTimeout(idleTimer); signal?.removeEventListener("abort", cancel);
        entry = data.entry;
        resources.set(data.blob, { info: data.info, release });
        // A forgotten programmatic consumer cannot keep an export worker forever.
        releaseTimer = setTimeout(() => { void release(); }, 30 * 60 * 1000);
        resolve(data.blob);
      }
    };
    watchdog(); worker.postMessage({ type: "start", blob, options: settings });
  });
}
addEventListener("pagehide", () => { for (const worker of active) { worker.postMessage({ type: "cancel" }); worker.postMessage({ type: "release" }); } });
