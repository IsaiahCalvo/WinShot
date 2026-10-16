import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import http from "node:http";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";

// Real Chromium media pipelines and a real installed MV3 extension. The
// synthetic fixture is encoded by Chrome, not a stubbed recording payload.
async function withStudio(run, { recording = false } = {}) {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-video-studio-"));
  const downloadPath = path.join(profile, "downloads"); fs.mkdirSync(downloadPath);
  const browser = await launchCaptureBrowser(profile, { width: 1280, height: 1000 }, { args: [
    ...(recording ? ["--auto-select-desktop-capture-source=WinShot recording source", "--auto-select-tab-capture-source-by-title=WinShot recording source", "--allow-http-screen-capture", "--enable-usermedia-screen-capturing"] : [])
  ] });
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const studio = await browser.newPage();
    const errors = []; studio.on("pageerror", error => errors.push(error.message));
    const client = await studio.createCDPSession();
    await client.send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath, eventsEnabled: true });
    await studio.goto(`chrome-extension://${id}/src/video-editor.html`);
    await studio.waitForSelector('body[data-state="ready"]');
    expect(await studio.title()).toBe("WinShot — Video studio");
    await run({ browser, studio, id, profile, downloadPath, errors });
    expect(errors).toEqual([]);
  } finally { await browser.close(); fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); }
}
async function makeVideo(studio, profile) {
  const pending = studio.evaluate(async () => new Promise((resolve, reject) => {
    const start = document.createElement("button"); start.id = "fixture-media-start"; start.textContent = "Start test media";
    start.style.cssText = "position:fixed;top:0;left:0;z-index:9999";
    document.body.append(start);
    start.addEventListener("click", () => {
      (async () => {
    const canvas = document.createElement("canvas"); canvas.width = 240; canvas.height = 160;
    const ctx = canvas.getContext("2d"), stream = canvas.captureStream(30);
    const audio = new AudioContext(), oscillator = audio.createOscillator(), gain = audio.createGain(), destination = audio.createMediaStreamDestination();
    await audio.resume(); oscillator.frequency.value = 440; gain.gain.value = .35;
    oscillator.connect(gain).connect(destination); oscillator.start();
    destination.stream.getAudioTracks().forEach(track => stream.addTrack(track));
    const recorder = new MediaRecorder(stream, { mimeType: "video/webm;codecs=vp8,opus" }), parts = [];
    recorder.ondataavailable = event => parts.push(event.data);
    const stopped = new Promise(resolve => recorder.onstop = resolve);
    recorder.start();
    const started = performance.now();
    while (performance.now() - started < 3200) {
      const elapsed = (performance.now() - started) / 1000;
      ctx.fillStyle = elapsed < .8 ? "#ee3030" : elapsed < 2.4 ? "#20d060" : "#2050e0";
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.fillStyle = "white"; ctx.fillRect(Math.floor(elapsed * 40), 120, 18, 18);
      await new Promise(resolve => setTimeout(resolve, 30));
    }
    recorder.stop(); await stopped;
    stream.getTracks().forEach(track => track.stop()); oscillator.stop(); await audio.close();
    const blob = new Blob(parts, { type: "video/webm" });
    return btoa(String.fromCharCode(...new Uint8Array(await blob.arrayBuffer())));
      })().then(resolve, reject).finally(() => start.remove());
    }, { once: true });
  }));
  await studio.waitForSelector("#fixture-media-start");
  await studio.click("#fixture-media-start");
  const bytes = await pending;
  const file = path.join(profile, "color-and-tone.webm"); fs.writeFileSync(file, Buffer.from(bytes, "base64")); return file;
}
async function importVideo(studio, file) {
  await (await studio.$("#video-file")).uploadFile(file);
  await studio.waitForFunction(() => document.body.dataset.state === "ready" && /Video saved locally/.test(document.querySelector("#status").textContent));
  await studio.waitForFunction(() => document.querySelector("#preview").readyState >= 2);
}
async function change(studio, selector, value) {
  await studio.$eval(selector, (node, next) => { node.value = String(next); node.dispatchEvent(new Event("input", { bubbles: true })); node.dispatchEvent(new Event("change", { bubbles: true })); }, value);
}
async function waitForRecovery(studio) {
  try { await studio.waitForFunction(() => document.body.dataset.state === "ready", { timeout: 10000 }); }
  catch (error) {
    const recovery = await studio.evaluate(() => ({ state: document.body.dataset.state, status: document.querySelector("#status").textContent, title: document.querySelector("#video-title").value, faults: window.previewLoadFaults, sourceDisabled: document.querySelector("#save-source").disabled, errors: window.recoveryPageErrors || [] }));
    throw new Error(`${error.message}; recording recovery: ${JSON.stringify(recovery)}`);
  }
}
async function waitDownload(downloadPath) {
  await expect.poll(() => fs.readdirSync(downloadPath).filter(name => !name.endsWith(".crdownload")).length, { timeout: 15000 }).toBeGreaterThan(0);
  return path.join(downloadPath, fs.readdirSync(downloadPath).find(name => !name.endsWith(".crdownload")));
}
async function auditVideo(studio, file) {
  const bytes = fs.readFileSync(file).toString("base64");
  return studio.evaluate(async encoded => {
    const { inspectVideo, mediaEvent, seekVideo } = await import(chrome.runtime.getURL("src/video-tools.js"));
    const blob = new Blob([Uint8Array.from(atob(encoded), char => char.charCodeAt(0))], { type: "video/webm" });
    const metadata = await inspectVideo(blob), video = document.createElement("video"), url = URL.createObjectURL(blob);
    video.muted = true; const ready = mediaEvent(video, "loadeddata"); video.src = url; await ready;
    await seekVideo(video, Math.min(.25, metadata.duration / 2));
    const canvas = document.createElement("canvas"); canvas.width = metadata.width; canvas.height = metadata.height;
    const ctx = canvas.getContext("2d");
    // Decode actual exported sound, rather than just checking for an audio track.
    const audio = new AudioContext(), source = audio.createMediaElementSource(video), analyser = audio.createAnalyser();
    const silent = audio.createGain(); silent.gain.value = 0;
    source.connect(analyser); analyser.connect(silent).connect(audio.destination);
    video.muted = false;
    const decoded = new Promise(resolve => {
      const frame = video.requestVideoFrameCallback(() => { clearTimeout(timeout); resolve(); });
      // A valid static screen recording may contain a single decoded frame.
      // It has no future frame callback after seek; sample that retained frame.
      const timeout = setTimeout(() => { video.cancelVideoFrameCallback(frame); resolve(); }, 400);
    });
    await audio.resume(); await video.play(); await decoded; await new Promise(resolve => setTimeout(resolve, 180));
    ctx.drawImage(video, 0, 0);
    const pixel = [...ctx.getImageData(30, 30, 1, 1).data];
    const samples = new Float32Array(analyser.fftSize); analyser.getFloatTimeDomainData(samples);
    const rms = Math.sqrt(samples.reduce((total, sample) => total + sample * sample, 0) / samples.length);
    video.pause(); video.removeAttribute("src"); video.load(); URL.revokeObjectURL(url); await audio.close();
    return { ...metadata, pixel, rms };
  }, bytes);
}

test("browser video studio imports, persists trim edits, and exports playable clipped video with pixels and sound", async () => {
  await withStudio(async ({ studio, profile, downloadPath }) => {
    const fixture = await makeVideo(studio, profile); await importVideo(studio, fixture);
    expect(await studio.$eval("#preview", node => node.videoWidth)).toBe(240);
    expect(await studio.$eval("#preview", node => Number.isFinite(node.duration))).toBe(true);
    await change(studio, "#start-time", 1); await change(studio, "#end-time", 2);
    await change(studio, "#video-title", "My browser clip");
    await change(studio, "#volume", 50); await studio.click("#mono-export");
    await studio.waitForFunction(async () => { const { getVideo } = await import(chrome.runtime.getURL("src/video-store.js")); const saved = await getVideo(new URL(location.href).searchParams.get("id")); return saved.record.trimStart === 1 && saved.record.trimEnd === 2 && saved.record.title === "My browser clip"; });
    await studio.reload(); await studio.waitForSelector('body[data-state="ready"]');
    expect(await studio.$eval("#start-time", node => node.value)).toBe("1"); expect(await studio.$eval("#end-time", node => node.value)).toBe("2");
    await studio.evaluate(() => { const download = chrome.downloads.download.bind(chrome.downloads); window.downloadRequests = []; chrome.downloads.download = options => { window.downloadRequests.push({ ...options }); return download(options); }; });
    await studio.click("#export-video"); await studio.waitForFunction(() => /Clip exported/.test(document.querySelector("#status").textContent));
    const file = await waitDownload(downloadPath), audit = await auditVideo(studio, file);
    // Headless Chrome auto-accepts Save As with its blob URL basename. Observe
    // the exact filename request while forwarding it to the real download API.
    expect(await studio.evaluate(() => window.downloadRequests[0].filename)).toBe("My browser clip-clip.webm");
    expect(audit.width).toBe(240); expect(audit.height).toBe(160);
    expect(audit.duration).toBeGreaterThan(.85); expect(audit.duration).toBeLessThan(1.35);
    expect(audit.pixel[1]).toBeGreaterThan(160); expect(audit.pixel[0]).toBeLessThan(80);
    expect(audit.rms).toBeGreaterThan(.05); expect(audit.rms).toBeLessThan(.2);
    expect(await studio.$eval("#video-history", node => node.textContent)).toContain("My browser clip");
  });
});

test("browser video studio cancellation preserves the source and a later muted resized export succeeds", async () => {
  await withStudio(async ({ studio, profile, downloadPath }) => {
    const fixture = await makeVideo(studio, profile); await importVideo(studio, fixture);
    const before = await studio.evaluate(async () => { const { getVideo } = await import(chrome.runtime.getURL("src/video-store.js")); const saved = await getVideo(new URL(location.href).searchParams.get("id")); return { id: saved.record.id, size: saved.blob.size }; });
    await studio.click("#export-video"); await studio.waitForFunction(() => document.querySelector("#export-progress").value > .1);
    await studio.click("#cancel-export"); await studio.waitForFunction(() => document.body.dataset.state === "ready" && /cancelled/.test(document.querySelector("#status").textContent));
    expect(fs.readdirSync(downloadPath)).toEqual([]);
    const after = await studio.evaluate(async () => { const { getVideo } = await import(chrome.runtime.getURL("src/video-store.js")); const saved = await getVideo(new URL(location.href).searchParams.get("id")); return { id: saved.record.id, size: saved.blob.size }; });
    expect(after).toEqual(before);
    await change(studio, "#start-time", 1); await change(studio, "#end-time", 2);
    await studio.click("#mute-export");
    expect(await studio.$eval("#preview", node => node.muted)).toBe(true);
    await change(studio, "#volume", 50);
    expect(await studio.$eval("#preview", node => node.volume)).toBe(.5);
    // Exercise the real exporter size option with an actual smaller raster;
    // the UI's named limits intentionally avoid upscaling small recordings.
    const exportBytes = await studio.evaluate(async () => {
      const { getVideo } = await import(chrome.runtime.getURL("src/video-store.js")); const { exportVideoClip } = await import(chrome.runtime.getURL("src/video-tools.js"));
      const saved = await getVideo(new URL(location.href).searchParams.get("id"));
      const blob = await exportVideoClip(saved.blob, { start: 1, end: 2, width: 120, height: 80, mute: true, fps: 24 });
      return btoa(String.fromCharCode(...new Uint8Array(await blob.arrayBuffer())));
    });
    const file = path.join(profile, "muted-resized.webm"); fs.writeFileSync(file, Buffer.from(exportBytes, "base64"));
    const audit = await auditVideo(studio, file);
    expect(audit.width).toBe(120); expect(audit.height).toBe(80); expect(audit.duration).toBeGreaterThan(.85); expect(audit.duration).toBeLessThan(1.35);
    expect(audit.pixel[1]).toBeGreaterThan(160); expect(audit.rms).toBeLessThan(.001);
  });
});

test("browser video studio handles bad imports and storage failure without replacing the saved source", async () => {
  await withStudio(async ({ studio, profile }) => {
    const fixture = await makeVideo(studio, profile); await importVideo(studio, fixture);
    const id = new URL(studio.url()).searchParams.get("id");
    const bad = path.join(profile, "broken.mp4"); fs.writeFileSync(bad, "not a video");
    await (await studio.$("#video-file")).uploadFile(bad);
    await studio.waitForFunction(() => document.body.dataset.state === "ready" && /could not read this video/.test(document.querySelector("#status").textContent));
    expect(new URL(studio.url()).searchParams.get("id")).toBe(id);
    expect(await studio.$eval("#save-source", node => node.disabled)).toBe(false);
    await studio.evaluate(() => { const original = IDBObjectStore.prototype.put; IDBObjectStore.prototype.put = function(...args) { if (this.name === "media") throw new DOMException("Test storage quota exhausted", "QuotaExceededError"); return original.apply(this, args); }; });
    await (await studio.$("#video-file")).uploadFile(fixture);
    await studio.waitForFunction(() => document.body.dataset.state === "ready" && /history storage is full/.test(document.querySelector("#status").textContent));
    expect(await studio.$eval("#save-local", node => node.hidden)).toBe(false);
    expect(await studio.$eval("#save-source", node => node.disabled)).toBe(false);
    expect(await studio.evaluate(async savedId => Boolean(await (await import(chrome.runtime.getURL("src/video-store.js"))).getVideo(savedId)), id)).toBe(true);
    expect(await studio.evaluate(async () => (await (await import(chrome.runtime.getURL("src/video-store.js"))).listVideos()).length)).toBe(1);
    await change(studio, "#video-title", "Unsaved take");
    studio.once("dialog", dialog => dialog.dismiss());
    await studio.click(".history-row button:first-child");
    expect(await studio.$eval("#video-title", node => node.value)).toBe("Unsaved take");
    expect(new URL(studio.url()).searchParams.has("id")).toBe(false);
    studio.once("dialog", dialog => dialog.accept());
    await studio.click(".history-row button:first-child");
    await studio.waitForFunction(savedId => new URL(location.href).searchParams.get("id") === savedId, {}, id);
  });
});

test("browser video studio reports denied sharing and unavailable encoders while keeping video import usable", async () => {
  await withStudio(async ({ studio, profile }) => {
    const fixture = await makeVideo(studio, profile); await importVideo(studio, fixture);
    await studio.evaluate(() => { navigator.mediaDevices.getDisplayMedia = async () => { throw new DOMException("Test chooser cancelled", "NotAllowedError"); }; });
    await studio.click("#record"); await studio.waitForFunction(() => /Sharing cancelled/.test(document.querySelector("#status").textContent));
    expect(await studio.$eval("#preview", node => node.srcObject)).toBeNull();
    expect(await studio.$eval("#export-video", node => node.disabled)).toBe(false);
    await studio.evaluateOnNewDocument(() => { MediaRecorder.isTypeSupported = () => false; });
    await studio.reload(); await studio.waitForSelector('body[data-state="ready"]');
    expect(await studio.$eval("#record", node => node.disabled)).toBe(true); expect(await studio.$eval("#export-video", node => node.disabled)).toBe(true);
    expect(await studio.$eval("#import-video", node => node.disabled)).toBe(false); expect(await studio.$eval("#save-source", node => node.disabled)).toBe(false);
  });
});

test("browser video studio records a real selected Chrome surface and opens the playable recording in its editor tab", async () => {
  // Own this source server: a concurrent Playwright command may stop its shared
  // fixture server while this chooser test is still running.
  const sourceServer = http.createServer((_request, response) => { response.setHeader("content-type", "text/html"); response.end('<title>WinShot recording source</title><div style="height:100vh;background:#26c574;display:grid;place-items:center;color:white;font:40px system-ui">WinShot recording source</div>'); });
  await new Promise(resolve => sourceServer.listen(0, "127.0.0.1", resolve));
  try {
  await withStudio(async ({ browser, studio, downloadPath }) => {
    const source = await browser.newPage();
    await source.goto(`http://127.0.0.1:${sourceServer.address().port}/`);
    await studio.evaluate(() => {
      const play = HTMLMediaElement.prototype.play;
      window.livePreviewPlayCalls = 0;
      HTMLMediaElement.prototype.play = function(...args) {
        if (this.id === "preview" && this.srcObject) {
          window.livePreviewPlayCalls++;
          // Only the optional UI preview's promise stalls. The chooser, shared
          // tracks, MediaRecorder, and video decoder all stay real.
          play.apply(this, args).catch(() => {});
          return new Promise(() => {});
        }
        return play.apply(this, args);
      };
    });
    await studio.bringToFront(); await studio.click("#record");
    await source.bringToFront();
    // Choosing a shared tab activates that source and hides the studio. Use
    // timer polling until we return; hidden-tab rAF polling would stall.
    await studio.waitForFunction(() => document.body.dataset.state === "recording", { polling: 100, timeout: 15000 });
    expect(await studio.evaluate(() => window.livePreviewPlayCalls)).toBe(1);
    expect(await studio.evaluate(() => document.hidden)).toBe(true);
    await new Promise(resolve => setTimeout(resolve, 1400));
    await studio.bringToFront();
    await studio.click("#stop-record");
    await studio.waitForFunction(() => document.body.dataset.state === "ready" && /Video saved locally/.test(document.querySelector("#status").textContent));
    const metadata = await studio.evaluate(async () => { const { getVideo } = await import(chrome.runtime.getURL("src/video-store.js")); const saved = await getVideo(new URL(location.href).searchParams.get("id")); return { ...saved.record, size: saved.blob.size }; });
    expect(metadata.source).toBe("recording"); expect(metadata.duration).toBeGreaterThan(1); expect(metadata.size).toBeGreaterThan(1000);
    expect(await studio.$eval("#preview", node => node.srcObject)).toBeNull();
    await studio.click("#save-source"); const file = await waitDownload(downloadPath), audit = await auditVideo(studio, file);
    expect(audit.pixel[1]).toBeGreaterThan(120); expect(audit.pixel[0]).toBeLessThan(100);
    expect(audit.duration).toBeGreaterThan(1);
  }, { recording: true });
  } finally { await new Promise(resolve => sourceServer.close(resolve)); }
});


test("browser video studio exports sound after metadata loads beyond transient activation under Chrome default autoplay policy", async () => {
  await withStudio(async ({ browser, studio, profile, downloadPath }) => {
    expect(browser.process().spawnargs.some(argument => argument.includes("autoplay-policy"))).toBe(false);
    const fixture = await makeVideo(studio, profile); await importVideo(studio, fixture);
    await change(studio, "#start-time", 1); await change(studio, "#end-time", 2);
    await studio.evaluate(() => {
      const source = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "src");
      const play = HTMLMediaElement.prototype.play, connect = AudioNode.prototype.connect;
      window.exportPolicyAudit = { delayed: 0, play: [], speakerConnections: 0 };
      window.delayExportSource = true;
      Object.defineProperty(HTMLMediaElement.prototype, "src", { ...source, set(value) {
        if (window.delayExportSource && this instanceof HTMLVideoElement && !this.isConnected && String(value).startsWith("blob:")) {
          window.exportPolicyAudit.delayed++;
          setTimeout(() => source.set.call(this, value), 6200);
        } else source.set.call(this, value);
      } });
      HTMLMediaElement.prototype.play = function(...args) {
        if (window.delayExportSource && !this.isConnected) window.exportPolicyAudit.play.push({ active: navigator.userActivation.isActive, muted: this.muted });
        return play.apply(this, args);
      };
      AudioNode.prototype.connect = function(destination, ...args) {
        if (window.delayExportSource && destination instanceof AudioDestinationNode) window.exportPolicyAudit.speakerConnections++;
        return connect.call(this, destination, ...args);
      };
    });
    await studio.click("#export-video");
    await studio.waitForFunction(() => document.body.dataset.state === "ready", { timeout: 20000 });
    const result = await studio.evaluate(() => ({ status: document.querySelector("#status").textContent, ...window.exportPolicyAudit }));
    expect(result.delayed).toBe(1);
    expect(result.play.some(call => !call.active && !call.muted), JSON.stringify(result)).toBe(true);
    expect(result.speakerConnections).toBe(0);
    expect(result.status, JSON.stringify(result)).toContain("Clip exported");
    await studio.evaluate(() => { window.delayExportSource = false; });
    const file = await waitDownload(downloadPath), audit = await auditVideo(studio, file);
    expect(audit.duration).toBeGreaterThan(.85); expect(audit.duration).toBeLessThan(1.35);
    expect(audit.rms).toBeGreaterThan(.1); expect(audit.pixel[1]).toBeGreaterThan(160);
  });
});


test("browser video studio keeps the prior original downloadable and controls usable when recording preview and restoration both fail", async () => {
  const sourceServer = http.createServer((_request, response) => { response.setHeader("content-type", "text/html"); response.end('<title>WinShot recording source</title><div style="height:100vh;background:#26c574">Recording recovery source</div>'); });
  await new Promise(resolve => sourceServer.listen(0, "127.0.0.1", resolve));
  try {
    await withStudio(async ({ browser, studio, profile, downloadPath }) => {
      const fixture = await makeVideo(studio, profile); await importVideo(studio, fixture);
      await change(studio, "#video-title", "Prior recording");
      const priorId = new URL(studio.url()).searchParams.get("id");
      const priorHash = crypto.createHash("sha256").update(fs.readFileSync(fixture)).digest("hex");
      await studio.evaluate(() => {
        const source = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "src");
        window.previewLoadFaults = 0;
        Object.defineProperty(HTMLMediaElement.prototype, "src", { ...source, set(value) {
          source.set.call(this, value);
          if (this.id === "preview" && String(value).startsWith("blob:")) {
            window.previewLoadFaults++;
            // Fail only preview surfaces, after the real recording has encoded,
            // passed inspection, and reached its separate display-load step.
            queueMicrotask(() => this.dispatchEvent(new Event("error")));
          }
        } });
      });
      const source = await browser.newPage(); await source.goto(`http://127.0.0.1:${sourceServer.address().port}/`);
      await studio.bringToFront(); await studio.click("#record");
      await studio.waitForFunction(() => document.hidden || document.body.dataset.state === "recording", { polling: 100, timeout: 15000 });
      await studio.bringToFront();
      await studio.waitForFunction(() => document.body.dataset.state === "recording", { polling: 100, timeout: 15000 });
      await new Promise(resolve => setTimeout(resolve, 1100));
      await studio.click("#stop-record");
      await waitForRecovery(studio);
      expect(await studio.$eval("#status", node => node.textContent)).toContain("previous video is still available");
      expect(await studio.evaluate(() => window.previewLoadFaults)).toBe(2);
      expect(await studio.$eval("#video-title", node => node.value)).toBe("Prior recording");
      expect(new URL(studio.url()).searchParams.get("id")).toBe(priorId);
      expect(await studio.$eval("#save-source", node => node.disabled)).toBe(false);
      expect(await studio.$eval("#import-video", node => node.disabled)).toBe(false);
      await studio.click("#save-source");
      const downloaded = await waitDownload(downloadPath);
      expect(crypto.createHash("sha256").update(fs.readFileSync(downloaded)).digest("hex")).toBe(priorHash);
      expect(await studio.evaluate(async () => (await (await import(chrome.runtime.getURL("src/video-store.js"))).listVideos()).length)).toBe(2);
    }, { recording: true });
  } finally { await new Promise(resolve => sourceServer.close(resolve)); }
});

test("browser video studio keeps a first recording downloadable when its preview cannot be opened", async () => {
  const sourceServer = http.createServer((_request, response) => { response.setHeader("content-type", "text/html"); response.end('<title>WinShot recording source</title><div style="height:100vh;background:#26c574">First recording recovery</div>'); });
  await new Promise(resolve => sourceServer.listen(0, "127.0.0.1", resolve));
  try {
    await withStudio(async ({ browser, studio, downloadPath }) => {
      await studio.evaluate(() => {
        const source = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "src");
        window.previewLoadFaults = 0;
        window.originalPreviewSourceDescriptor = source;
        Object.defineProperty(HTMLMediaElement.prototype, "src", { ...source, set(value) {
          source.set.call(this, value);
          if (this.id === "preview" && String(value).startsWith("blob:")) {
            window.previewLoadFaults++;
            queueMicrotask(() => this.dispatchEvent(new Event("error")));
          }
        } });
      });
      const source = await browser.newPage(); await source.goto(`http://127.0.0.1:${sourceServer.address().port}/`);
      await studio.bringToFront(); await studio.click("#record");
      await studio.waitForFunction(() => document.hidden || document.body.dataset.state === "recording", { polling: 100, timeout: 15000 });
      await studio.bringToFront();
      await studio.waitForFunction(() => document.body.dataset.state === "recording", { polling: 100, timeout: 15000 });
      await new Promise(resolve => setTimeout(resolve, 1100));
      await studio.click("#stop-record");
      await waitForRecovery(studio);
      expect(await studio.$eval("#status", node => node.textContent)).toContain("recording bytes are still available");
      expect(await studio.evaluate(() => window.previewLoadFaults)).toBe(1);
      expect(await studio.$eval("#save-source", node => node.disabled)).toBe(false);
      expect(await studio.$eval("#export-video", node => node.disabled)).toBe(true);
      expect(await studio.$eval("#record", node => node.disabled)).toBe(false);
      await studio.click("#save-source");
      const downloaded = await waitDownload(downloadPath), audit = await auditVideo(studio, downloaded);
      expect(audit.pixel[1]).toBeGreaterThan(120); expect(audit.duration).toBeGreaterThan(.8);
      const savedHash = await studio.evaluate(async () => {
        const store = await import(chrome.runtime.getURL("src/video-store.js"));
        const records = await store.listVideos(), saved = await store.getVideo(records[0].id);
        return [...new Uint8Array(await crypto.subtle.digest("SHA-256", await saved.blob.arrayBuffer()))].map(byte => byte.toString(16).padStart(2, "0")).join("");
      });
      expect(crypto.createHash("sha256").update(fs.readFileSync(downloaded)).digest("hex")).toBe(savedHash);
      await studio.evaluate(() => {
        // The original preview decoder can work again. Canceling the next
        // chooser must preserve the raw-only source without inventing metadata.
        const source = window.originalPreviewSourceDescriptor;
        const seek = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "currentTime");
        window.rawRecoveryAudit = { loads: 0, invalidSeeks: 0, chooserDenials: 0 };
        Object.defineProperty(HTMLMediaElement.prototype, "src", { ...source, set(value) {
          if (this.id === "preview" && String(value).startsWith("blob:")) window.rawRecoveryAudit.loads++;
          source.set.call(this, value);
        } });
        Object.defineProperty(HTMLMediaElement.prototype, "currentTime", { ...seek, set(value) {
          if (!Number.isFinite(value)) window.rawRecoveryAudit.invalidSeeks++;
          seek.set.call(this, value);
        } });
        navigator.mediaDevices.getDisplayMedia = async () => { window.rawRecoveryAudit.chooserDenials++; throw new DOMException("Second sharing chooser cancelled", "NotAllowedError"); };
      });
      studio.once("dialog", dialog => dialog.accept());
      const cancelledAt = Date.now();
      await studio.click("#record");
      await studio.waitForFunction(() => document.body.dataset.state === "ready" && /Sharing cancelled/.test(document.querySelector("#status").textContent), { timeout: 1500 });
      expect(Date.now() - cancelledAt).toBeLessThan(1500);
      expect(await studio.evaluate(() => window.rawRecoveryAudit)).toEqual({ loads: 0, invalidSeeks: 0, chooserDenials: 1 });
      expect(await studio.$eval("#export-video", node => node.disabled)).toBe(true);
      expect(await studio.$eval("#start-time", node => node.disabled)).toBe(true);
      expect(await studio.$eval("#video-title", node => node.disabled)).toBe(true);
      expect(await studio.$eval("#save-source", node => node.disabled)).toBe(false);
      fs.rmSync(downloaded);
      await studio.click("#save-source");
      const afterCancel = await waitDownload(downloadPath);
      expect(crypto.createHash("sha256").update(fs.readFileSync(afterCancel)).digest("hex")).toBe(savedHash);
    }, { recording: true });
  } finally { await new Promise(resolve => sourceServer.close(resolve)); }
});
