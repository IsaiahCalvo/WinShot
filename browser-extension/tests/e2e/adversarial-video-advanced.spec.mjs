import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";

async function withVideoStudio(run) {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-video-advanced-")), downloads = path.join(profile, "downloads"); fs.mkdirSync(downloads);
  const browser = await launchCaptureBrowser(profile, { width: 1280, height: 1050 });
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const studio = await browser.newPage(), errors = [];
    studio.on("pageerror", error => errors.push(error.message));
    await (await studio.createCDPSession()).send("Browser.setDownloadBehavior", { behavior: "allow", downloadPath: downloads, eventsEnabled: true });
    await studio.goto(`chrome-extension://${id}/src/video-editor.html`); await studio.waitForSelector('body[data-state="ready"]');
    expect(await studio.title()).toBe("WinShot — Video studio");
    expect(browser.process().spawnargs.some(argument => argument.includes("autoplay-policy"))).toBe(false);
    await run({ browser, studio, profile, downloads });
    expect(errors).toEqual([]);
  } finally { await browser.close(); fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); }
}
async function fixtureVideo(studio, profile, duration = 8) {
  const encoded = await studio.evaluate(async seconds => {
    const { Output, BufferTarget, WebMOutputFormat, CanvasSource, AudioSampleSource, AudioSample } = await import(chrome.runtime.getURL("src/vendor/mediabunny/mediabunny.min.mjs"));
    const canvas = document.createElement("canvas"); canvas.width = 240; canvas.height = 160;
    const context = canvas.getContext("2d"), target = new BufferTarget(), output = new Output({ format: new WebMOutputFormat(), target });
    const video = new CanvasSource(canvas, { codec: "vp8", bitrate: 600000 }), audio = new AudioSampleSource({ codec: "opus", bitrate: 128000 });
    output.addVideoTrack(video, { frameRate: 30 }); output.addAudioTrack(audio); await output.start();
    await Promise.all([
      (async () => {
        for (let frame = 0; frame < seconds * 30; frame++) {
          for (const [color, x, y] of [["#e62828", 0, 0], ["#1ed250", 120, 0], ["#1e50e6", 0, 80], ["#e6d228", 120, 80]]) { context.fillStyle = color; context.fillRect(x, y, 120, 80); }
          context.fillStyle = "white"; context.fillRect(frame % 220, 74, 20, 12);
          await video.add(frame / 30, 1 / 30);
        }
        video.close();
      })(),
      (async () => {
        const rate = 48000;
        for (let offset = 0; offset < seconds * rate; offset += 960) {
          const frames = Math.min(960, seconds * rate - offset), data = new Float32Array(frames * 2);
          for (let index = 0; index < frames; index++) { const sample = .35 * Math.sin(2 * Math.PI * 440 * (offset + index) / rate); data[index] = sample; data[frames + index] = sample; }
          const sample = new AudioSample({ data, format: "f32-planar", numberOfChannels: 2, sampleRate: rate, timestamp: offset / rate });
          try { await audio.add(sample); } finally { sample.close(); }
        }
        audio.close();
      })()
    ]);
    await output.finalize();
    const bytes = new Uint8Array(target.buffer); let binary = "";
    for (let offset = 0; offset < bytes.length; offset += 32768) binary += String.fromCharCode(...bytes.subarray(offset, offset + 32768));
    return btoa(binary);
  }, duration);
  const file = path.join(profile, "quadrants-and-tone.webm"); fs.writeFileSync(file, Buffer.from(encoded, "base64")); return file;
}
async function openVideo(studio, file) {
  await (await studio.$("#video-file")).uploadFile(file);
  await studio.waitForFunction(() => document.body.dataset.state === "ready" && /Video saved locally/.test(document.querySelector("#status").textContent));
  await studio.waitForFunction(() => document.querySelector("#preview").readyState >= 2 && document.querySelector("#edited-preview").width > 1);
}
async function input(studio, selector, value) { await studio.$eval(selector, (node, next) => { node.value = String(next); node.dispatchEvent(new Event("input", { bubbles: true })); node.dispatchEvent(new Event("change", { bubbles: true })); }, value); }
async function exportedFile(downloads) {
  await expect.poll(() => fs.readdirSync(downloads).filter(name => !name.endsWith(".crdownload")).length, { timeout: 10000 }).toBeGreaterThan(0);
  return path.join(downloads, fs.readdirSync(downloads).find(name => !name.endsWith(".crdownload")));
}
async function tempExports(studio) { return studio.evaluate(async () => { const directory = await navigator.storage.getDirectory(), names = []; for await (const [name] of directory.entries()) if (name.startsWith("winshot-export-")) names.push(name); return names; }); }
async function audit(studio, file, type = "video/webm") {
  const encoded = fs.readFileSync(file).toString("base64");
  return studio.evaluate(async ({ encoded, type }) => {
    const { inspectVideo, mediaEvent, seekVideo } = await import(chrome.runtime.getURL("src/video-tools.js"));
    const blob = new Blob([Uint8Array.from(atob(encoded), c => c.charCodeAt(0))], { type }), metadata = await inspectVideo(blob);
    const { Input, BlobSource, ALL_FORMATS, EncodedPacketSink } = await import(chrome.runtime.getURL("src/vendor/mediabunny/mediabunny.min.mjs"));
    const container = new Input({ source: new BlobSource(blob), formats: ALL_FORMATS });
    const audioTrack = await container.getPrimaryAudioTrack(); metadata.audioChannels = audioTrack ? await audioTrack.getNumberOfChannels() : 0;
    const videoTrack = await container.getPrimaryVideoTrack(), timestamps = [];
    for await (const packet of new EncodedPacketSink(videoTrack).packets()) timestamps.push(packet.timestamp);
    timestamps.sort((a, b) => a - b); const intervals = timestamps.slice(1).map((value, index) => value - timestamps[index]);
    metadata.videoPackets = timestamps.length; metadata.duplicateVideoTimestamps = intervals.some(interval => interval < .001);
    const cadence = [...intervals].sort((a, b) => a - b); metadata.cadenceFps = 1 / cadence[Math.floor(cadence.length / 2)]; container.dispose();
    const video = document.createElement("video"), url = URL.createObjectURL(blob), ready = mediaEvent(video, "loadeddata"); video.src = url; video.muted = true; await ready;
    await seekVideo(video, Math.min(.3, metadata.duration / 4));
    const audio = new AudioContext(), source = audio.createMediaElementSource(video), analyser = audio.createAnalyser(), silent = audio.createGain(); silent.gain.value = 0; analyser.fftSize = 4096;
    source.connect(analyser); analyser.connect(silent).connect(audio.destination); video.muted = false;
    await audio.resume(); await video.play(); await new Promise(resolve => setTimeout(resolve, 220));
    const canvas = document.createElement("canvas"); canvas.width = metadata.width; canvas.height = metadata.height; const ctx = canvas.getContext("2d"); ctx.drawImage(video, 0, 0);
    const pixel = (x, y) => [...ctx.getImageData(Math.round(metadata.width * x), Math.round(metadata.height * y), 1, 1).data];
    const samples = new Float32Array(analyser.fftSize), bins = new Float32Array(analyser.frequencyBinCount); analyser.getFloatTimeDomainData(samples); analyser.getFloatFrequencyData(bins);
    const rms = Math.sqrt(samples.reduce((sum, value) => sum + value * value, 0) / samples.length);
    let largest = -Infinity, peak = 0; for (let index = 1; index < bins.length; index++) if (bins[index] > largest) { largest = bins[index]; peak = index * audio.sampleRate / analyser.fftSize; }
    const result = { ...metadata, topLeft: pixel(.25, .25), bottomLeft: pixel(.25, .75), topRight: pixel(.75, .25), bottomRight: pixel(.75, .75), rms, peak };
    video.pause(); video.removeAttribute("src"); video.load(); await audio.close(); URL.revokeObjectURL(url); return result;
  }, { encoded, type });
}

test("advanced video UI persists crop, rotation, both flips and speed, then exports playable MP4 with aligned transformed pixels and sound", async () => {
  await withVideoStudio(async ({ studio, profile, downloads }) => {
    const file = await fixtureVideo(studio, profile); await openVideo(studio, file);
    await studio.click("#rotate-right"); await studio.click("#flip-horizontal"); await studio.click("#flip-vertical");
    await input(studio, "#crop-width", 50); await input(studio, "#video-speed", 2); await input(studio, "#start-time", 1.125); await input(studio, "#end-time", 3.625); await input(studio, "#volume", 50);
    await studio.click("#mono-export"); await input(studio, "#export-engine", "fast");
    await studio.waitForFunction(() => !document.querySelector('#video-format option[value="mp4"]').disabled, { timeout: 20000 });
    await input(studio, "#video-format", "mp4");
    await studio.waitForFunction(async () => { const { getVideo } = await import(chrome.runtime.getURL("src/video-store.js")); const saved = await getVideo(new URL(location.href).searchParams.get("id")); return saved.record.exportOptions?.format === "mp4" && saved.record.exportOptions?.speed === 2; });
    await studio.reload(); await studio.waitForSelector('body[data-state="ready"]');
    await studio.waitForFunction(() => { const canvas = document.querySelector("#edited-preview"); return canvas.width === 80 && canvas.getContext("2d").getImageData(40, 60, 1, 1).data[1] > 170; });
    if (process.env.WINSHOT_VIDEO_QA_DIR) {
      fs.mkdirSync(process.env.WINSHOT_VIDEO_QA_DIR, { recursive: true });
      await studio.evaluate(() => scrollTo(0, 0)); await studio.screenshot({ path: path.join(process.env.WINSHOT_VIDEO_QA_DIR, "advanced-video-desktop.png") });
      await studio.setViewport({ width: 390, height: 844 }); await studio.evaluate(() => scrollTo(0, 0));
      await studio.waitForFunction(() => document.querySelector("#edited-preview").getBoundingClientRect().width <= innerWidth);
      expect(await studio.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await studio.screenshot({ path: path.join(process.env.WINSHOT_VIDEO_QA_DIR, "advanced-video-mobile.png"), fullPage: true });
      await studio.setViewport({ width: 1280, height: 1050 });
    }
    const preview = await studio.$eval("#edited-preview", canvas => { const ctx = canvas.getContext("2d"); return { width: canvas.width, height: canvas.height, top: [...ctx.getImageData(canvas.width / 2, canvas.height / 4, 1, 1).data], bottom: [...ctx.getImageData(canvas.width / 2, canvas.height * .75, 1, 1).data] }; });
    expect(preview.width).toBe(80); expect(preview.height).toBe(240); expect(preview.top[1]).toBeGreaterThan(170); expect(preview.bottom[0]).toBeGreaterThan(170);
    await studio.evaluate(() => { const play = HTMLMediaElement.prototype.play; window.exportPlaybackCalls = 0; HTMLMediaElement.prototype.play = function(...args) { window.exportPlaybackCalls++; return play.apply(this, args); }; });
    await studio.click("#export-video"); await studio.waitForFunction(() => document.body.dataset.state === "ready" && /Clip exported as MP4/.test(document.querySelector("#status").textContent), { timeout: 30000 });
    expect(await studio.evaluate(() => window.exportPlaybackCalls)).toBe(0);
    expect(await tempExports(studio)).toEqual([]);
    const output = await exportedFile(downloads), decoded = await audit(studio, output, "video/mp4");
    expect(decoded.width).toBe(80); expect(decoded.height).toBe(240); expect(decoded.duration).toBeGreaterThan(1.20); expect(decoded.duration).toBeLessThan(1.30);
    expect(decoded.topLeft[1]).toBeGreaterThan(170); expect(decoded.topLeft[0]).toBeLessThan(80); expect(decoded.bottomLeft[0]).toBeGreaterThan(170); expect(decoded.bottomLeft[1]).toBeLessThan(80);
    expect(decoded.audioChannels).toBe(1); expect(decoded.duplicateVideoTimestamps).toBe(false); expect(decoded.cadenceFps).toBeGreaterThan(29); expect(decoded.cadenceFps).toBeLessThan(31);
    expect(decoded.rms).toBeGreaterThan(.06); expect(decoded.rms).toBeLessThan(.2); expect(decoded.peak).toBeGreaterThan(820); expect(decoded.peak).toBeLessThan(940);
    const original = await studio.evaluate(async () => { const saved = await (await import(chrome.runtime.getURL("src/video-store.js"))).getVideo(new URL(location.href).searchParams.get("id")); return [...new Uint8Array(await crypto.subtle.digest("SHA-256", await saved.blob.arrayBuffer()))].map(x => x.toString(16).padStart(2, "0")).join(""); });
    expect(original).toBe(crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex"));
    console.log(JSON.stringify({ verification: "transformed-MP4", decoded, codecs: await studio.$eval("#export-capabilities", node => node.textContent) }));
  });
});

test("fast offline WebM export preserves slowed playback tone and outpaces compatibility export on the same source and settings", async () => {
  await withVideoStudio(async ({ studio, profile }) => {
    const file = await fixtureVideo(studio, profile, 8); await openVideo(studio, file);
    const result = await studio.evaluate(async () => {
      const store = await import(chrome.runtime.getURL("src/video-store.js")), tools = await import(chrome.runtime.getURL("src/video-tools.js"));
      const saved = await store.getVideo(new URL(location.href).searchParams.get("id"));
      const started = performance.now(), output = await tools.exportVideoClip(saved.blob, { start: .75, end: 5.75, speed: .5, format: "webm", engine: "fast", width: 240, height: 160, fps: 30 });
      const info = tools.getVideoExportInfo(output), elapsed = performance.now() - started, bytes = new Uint8Array(await output.arrayBuffer()); let binary = "";
      for (let offset = 0; offset < bytes.length; offset += 32768) binary += String.fromCharCode(...bytes.subarray(offset, offset + 32768));
      await tools.releaseVideoExport(output);
      const compatibilityStarted = performance.now(), compatibility = await tools.exportVideoClip(saved.blob, { start: .75, end: 5.75, speed: .5, format: "webm", engine: "realtime", width: 240, height: 160, fps: 30 });
      const compatibilityElapsed = performance.now() - compatibilityStarted, compatibilityBytes = new Uint8Array(await compatibility.arrayBuffer()); let compatibilityBinary = "";
      for (let offset = 0; offset < compatibilityBytes.length; offset += 32768) compatibilityBinary += String.fromCharCode(...compatibilityBytes.subarray(offset, offset + 32768));
      return { base64: btoa(binary), compatibilityBase64: btoa(compatibilityBinary), info, elapsed, compatibilityElapsed };
    });
    const output = path.join(profile, "slow-fast.webm"); fs.writeFileSync(output, Buffer.from(result.base64, "base64")); const decoded = await audit(studio, output);
    const compatibilityFile = path.join(profile, "slow-compatibility.webm"); fs.writeFileSync(compatibilityFile, Buffer.from(result.compatibilityBase64, "base64")); const compatibility = await audit(studio, compatibilityFile);
    expect(result.info.engine).toBe("fast"); expect(result.info.storage).toBe("disk"); expect(result.elapsed).toBeLessThan(result.compatibilityElapsed);
    expect(compatibility.duration).toBeGreaterThan(9.8); expect(compatibility.duration).toBeLessThan(10.4); expect(compatibility.rms).toBeGreaterThan(.15); expect(compatibility.peak).toBeGreaterThan(180); expect(compatibility.peak).toBeLessThan(260); expect(compatibility.topLeft[0]).toBeGreaterThan(170);
    expect(decoded.duplicateVideoTimestamps).toBe(false); expect(decoded.cadenceFps).toBeGreaterThan(29); expect(decoded.cadenceFps).toBeLessThan(31);
    expect(decoded.duration).toBeGreaterThan(9.94); expect(decoded.duration).toBeLessThan(10.06); expect(decoded.rms).toBeGreaterThan(.15); expect(decoded.peak).toBeGreaterThan(180); expect(decoded.peak).toBeLessThan(260);
    expect(decoded.topLeft[0]).toBeGreaterThan(170); expect(decoded.topRight[1]).toBeGreaterThan(170); expect(decoded.bottomLeft[2]).toBeGreaterThan(170);
    expect(await tempExports(studio)).toEqual([]);
    console.log(JSON.stringify({ verification: "fast-offline-WebM", outputDuration: decoded.duration, elapsedMs: result.elapsed, compatibilityElapsedMs: result.compatibilityElapsed, codecs: result.info.codecs, storage: result.info.storage }));
  });
});

test("fast video export cancellation cleans temporary output and keeps the original bytes available", async () => {
  await withVideoStudio(async ({ studio, profile }) => {
    const file = await fixtureVideo(studio, profile, 12); await openVideo(studio, file);
    const result = await studio.evaluate(async () => {
      const store = await import(chrome.runtime.getURL("src/video-store.js")), tools = await import(chrome.runtime.getURL("src/video-tools.js")); const id = new URL(location.href).searchParams.get("id"), saved = await store.getVideo(id);
      const before = [...new Uint8Array(await crypto.subtle.digest("SHA-256", await saved.blob.arrayBuffer()))];
      const controller = new AbortController(); let progressReached = false, errorName;
      try { await tools.exportVideoClip(saved.blob, { start: 0, end: 12, format: "mp4", engine: "fast", width: 480, height: 320, signal: controller.signal, onProgress: fraction => { if (fraction > .03) { progressReached = true; controller.abort(new DOMException("Test user cancellation", "AbortError")); } } }); } catch (error) { errorName = error.name; }
      const after = [...new Uint8Array(await crypto.subtle.digest("SHA-256", await (await store.getVideo(id)).blob.arrayBuffer()))];
      return { before, after, errorName, progressReached };
    });
    expect(result.errorName).toBe("AbortError"); expect(result.progressReached).toBe(true); expect(result.after).toEqual(result.before); expect(await tempExports(studio)).toEqual([]);
    expect(await studio.$eval("#save-source", node => node.disabled)).toBe(false);
  });
});

test("video crop drag, frame stepping and compatibility WebM keep transformed output usable when fast codecs are unavailable", async () => {
  await withVideoStudio(async ({ studio, profile, downloads }) => {
    const file = await fixtureVideo(studio, profile, 4); await openVideo(studio, file);
    await studio.click("#select-crop"); const box = await (await studio.$("#edited-preview")).boundingBox();
    await studio.mouse.move(box.x + box.width * .5, box.y + 2); await studio.mouse.down(); await studio.mouse.move(box.x + box.width - 2, box.y + box.height - 2, { steps: 8 }); await studio.mouse.up();
    expect(Number(await studio.$eval("#crop-left", node => node.value))).toBeGreaterThan(49);
    await studio.click("#step-forward"); await studio.waitForFunction(() => document.querySelector("#preview").currentTime > 0);
    await input(studio, "#crop-left", 50); await input(studio, "#crop-top", 0); await input(studio, "#crop-width", 50); await input(studio, "#crop-height", 100);
    await input(studio, "#start-time", 1); await input(studio, "#end-time", 2); await input(studio, "#video-speed", 2);
    await studio.evaluate(() => { window.VideoEncoder = undefined; window.VideoDecoder = undefined; });
    await input(studio, "#export-engine", "auto");
    await studio.click("#export-video"); await studio.waitForFunction(() => document.body.dataset.state === "ready" && /Clip exported as WEBM/.test(document.querySelector("#status").textContent), { timeout: 15000 });
    const output = await exportedFile(downloads), decoded = await audit(studio, output);
    expect(decoded.width).toBe(120); expect(decoded.height).toBe(160); expect(decoded.duration).toBeGreaterThan(.4); expect(decoded.duration).toBeLessThan(.75);
    expect(decoded.topLeft[1]).toBeGreaterThan(170); expect(decoded.bottomLeft[0]).toBeGreaterThan(170); expect(decoded.bottomLeft[1]).toBeGreaterThan(170);
    expect(decoded.peak).toBeGreaterThan(820); expect(decoded.peak).toBeLessThan(940);
  });
});
