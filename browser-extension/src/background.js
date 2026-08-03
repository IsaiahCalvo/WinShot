import {
  CaptureMode,
  SessionState,
  buildTilePlan,
  createSession,
  friendlyError,
  restrictedPageReason,
  validateCoverage
} from "./contract.js";
import { listRecords, putRecord, putTile } from "./store.js";

const activeCaptures = new Map();
const pickerFrames = new Map();
let activeBatch = null;
let lastScreenshotAt = 0;

async function recoverInterruptedRecords() {
  const records = await listRecords();
  for (const record of records) {
    if (record.state !== SessionState.PREPARING && record.state !== SessionState.CAPTURING) continue;
    record.state = SessionState.FAILED;
    record.finishedAt = new Date().toISOString();
    record.error = "The extension service worker stopped during capture. The page watchdog restored page changes; retry the capture.";
    record.report ||= { warnings: [] };
    record.report.warnings ||= [];
    record.report.warnings.push(record.error);
    record.report.restoration = "page watchdog requested after service-worker restart";
    await putRecord(record);
  }
}

function captureId() {
  return `${Date.now().toString(36)}-${crypto.randomUUID()}`;
}

async function activeTab() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab?.id) throw new Error("No active browser tab was found.");
  return tab;
}

async function sendToTab(tabId, message, frameId = 0) {
  const response = await chrome.tabs.sendMessage(tabId, message, { frameId });
  if (response?.error) throw new Error(response.error);
  return response;
}

async function injectCaptureScript(tabId, frameId = 0, allFrames = false) {
  return await chrome.scripting.executeScript({
    target: allFrames ? { tabId, allFrames: true } : { tabId, frameIds: [frameId] },
    files: ["src/content.js"]
  });
}

function screenshotMetrics(metrics, frameHost) {
  if (metrics.frame?.isTop) return metrics;
  const context = frameHost || metrics.frame;
  if (!context?.accessible && !context?.located) throw new Error("The selected frame is cross-origin and its position could not be proven.");
  return {
    ...metrics,
    viewport: context.topViewport,
    clip: {
      ...metrics.clip,
      left: metrics.clip.left + context.left,
      top: metrics.clip.top + context.top
    }
  };
}

async function locateFrameChain(session) {
  const frames = await chrome.webNavigation.getAllFrames({ tabId: session.tabId });
  const byId = new Map(frames.map((frame) => [frame.frameId, frame]));
  const edges = [];
  let child = byId.get(session.frameId);
  while (child && child.frameId !== 0) {
    edges.push({ parentFrameId: child.parentFrameId, child });
    child = byId.get(child.parentFrameId);
  }
  if (!edges.length || child?.frameId !== 0) throw new Error("The browser no longer exposes the selected frame tree.");
  edges.reverse();

  let left = 0;
  let top = 0;
  let topViewport = null;
  const preparedFrameIds = [];
  try {
    for (const edge of edges) {
      await injectCaptureScript(session.tabId, edge.parentFrameId);
      const located = await sendToTab(session.tabId, {
        type: "WINSHOT_LOCATE_FRAME",
        sessionId: session.id,
        frameUrl: edge.child.url
      }, edge.parentFrameId);
      preparedFrameIds.push(edge.parentFrameId);
      left += located.left;
      top += located.top;
      topViewport ||= located.topViewport;
    }
  } catch (error) {
    await restoreFrameChain(session, preparedFrameIds);
    throw error;
  }
  return { located: true, left, top, topViewport, preparedFrameIds };
}

async function restoreFrameChain(session, preparedFrameIds) {
  for (const frameId of [...preparedFrameIds].reverse()) {
    try { await sendToTab(session.tabId, { type: "WINSHOT_RESTORE_FRAME_HOST", sessionId: session.id }, frameId); }
    catch { session.report.warnings.push(`Frame ${frameId} disconnected before parent-page restoration was acknowledged.`); }
  }
}

async function heartbeatFrameChain(session, preparedFrameIds) {
  await Promise.allSettled(preparedFrameIds.map((frameId) =>
    sendToTab(session.tabId, { type: "WINSHOT_HEARTBEAT_FRAME_HOST", sessionId: session.id }, frameId)));
}

async function assertStillActive(session) {
  const [tab] = await chrome.tabs.query({ active: true, windowId: session.windowId });
  if (tab?.id !== session.tabId) {
    throw new Error("The active tab changed during capture. WinShot stopped to avoid capturing pixels from the wrong page.");
  }
  if (tab.url && session.sourceUrl && tab.url !== session.sourceUrl) {
    throw new Error("The page navigated during capture. WinShot stopped and restored the old page state where possible.");
  }
}

function throwIfAborted(signal) {
  if (signal.aborted) throw new DOMException("Capture cancelled.", "AbortError");
}

async function throttleCapture(minIntervalMs, signal) {
  const remaining = minIntervalMs - (Date.now() - lastScreenshotAt);
  if (remaining > 0) {
    await new Promise((resolve, reject) => {
      const timer = setTimeout(resolve, remaining);
      signal.addEventListener("abort", () => {
        clearTimeout(timer);
        reject(new DOMException("Capture cancelled.", "AbortError"));
      }, { once: true });
    });
  }
}

async function screenshot(windowId, minIntervalMs, signal) {
  await throttleCapture(minIntervalMs, signal);
  throwIfAborted(signal);
  const dataUrl = await chrome.tabs.captureVisibleTab(windowId, { format: "png" });
  lastScreenshotAt = Date.now();
  return await (await fetch(dataUrl)).blob();
}

async function cropScreenshot(blob, metrics) {
  const bitmap = await createImageBitmap(blob);
  try {
    const scaleX = bitmap.width / metrics.viewport.width;
    const scaleY = bitmap.height / metrics.viewport.height;
    if (!Number.isFinite(scaleX) || !Number.isFinite(scaleY) || scaleX <= 0 || scaleY <= 0) {
      throw new Error("The browser returned an invalid screenshot scale.");
    }
    const left = Math.max(0, Math.round(metrics.clip.left * scaleX));
    const top = Math.max(0, Math.round(metrics.clip.top * scaleY));
    const right = Math.min(bitmap.width, Math.round((metrics.clip.left + metrics.clip.width) * scaleX));
    const bottom = Math.min(bitmap.height, Math.round((metrics.clip.top + metrics.clip.height) * scaleY));
    const width = right - left;
    const height = bottom - top;
    if (width < 1 || height < 1) throw new Error("The selected target is outside the visible viewport.");

    const canvas = new OffscreenCanvas(width, height);
    const context = canvas.getContext("2d", { alpha: false, willReadFrequently: false });
    context.drawImage(bitmap, left, top, width, height, 0, 0, width, height);
    const cropped = await canvas.convertToBlob({ type: "image/png" });
    return { blob: cropped, scaleX, scaleY, width, height };
  } finally {
    bitmap.close();
  }
}

async function seamScore(previous, current) {
  const left = Math.max(previous.destXPx, current.destXPx);
  const top = Math.max(previous.destYPx, current.destYPx);
  const right = Math.min(previous.destXPx + previous.widthPx, current.destXPx + current.widthPx);
  const bottom = Math.min(previous.destYPx + previous.heightPx, current.destYPx + current.heightPx);
  if (right - left < 8 || bottom - top < 4) return null;

  const sampleWidth = Math.min(256, right - left);
  const sampleHeight = Math.min(64, bottom - top);
  const [a, b] = await Promise.all([createImageBitmap(previous.blob), createImageBitmap(current.blob)]);
  try {
    const ca = new OffscreenCanvas(sampleWidth, sampleHeight);
    const cb = new OffscreenCanvas(sampleWidth, sampleHeight);
    const xa = ca.getContext("2d", { willReadFrequently: true });
    const xb = cb.getContext("2d", { willReadFrequently: true });
    xa.drawImage(a, left - previous.destXPx, top - previous.destYPx, right - left, bottom - top, 0, 0, sampleWidth, sampleHeight);
    xb.drawImage(b, left - current.destXPx, top - current.destYPx, right - left, bottom - top, 0, 0, sampleWidth, sampleHeight);
    const da = xa.getImageData(0, 0, sampleWidth, sampleHeight).data;
    const db = xb.getImageData(0, 0, sampleWidth, sampleHeight).data;
    let error = 0;
    for (let i = 0; i < da.length; i += 4) {
      error += Math.abs(da[i] - db[i]) + Math.abs(da[i + 1] - db[i + 1]) + Math.abs(da[i + 2] - db[i + 2]);
    }
    return Math.max(0, 1 - error / ((da.length / 4) * 3 * 255));
  } finally {
    a.close();
    b.close();
  }
}

function progress(session, message) {
  chrome.runtime.sendMessage({
    type: "WINSHOT_PROGRESS",
    captureId: session.id,
    state: session.state,
    tileCount: session.report.tileCount,
    message
  }).catch(() => {});
}

function fileSafeTitle(value) {
  const safe = (value || "capture").replace(/[<>:"/\\|?*\x00-\x1f]/g, " ").replace(/\s+/g, " ").trim();
  return (safe || "capture").slice(0, 100);
}

async function storeVisibleCapture(session, controller) {
  session.state = SessionState.CAPTURING;
  progress(session, "Capturing visible viewport…");
  await assertStillActive(session);
  const source = await screenshot(session.windowId, session.limits.minCaptureIntervalMs, controller.signal);
  const bitmap = await createImageBitmap(source);
  const width = bitmap.width;
  const height = bitmap.height;
  bitmap.close();
  try {
    await injectCaptureScript(session.tabId);
    const snapshot = await sendToTab(session.tabId, { type: "WINSHOT_VISIBLE_SEMANTICS" });
    session.semantics = snapshot.semantics;
    session.report.dimensionsCss = { width: snapshot.metrics.width, height: snapshot.metrics.height };
  } catch {
    session.semantics = { text: [], links: [] };
    session.report.warnings.push("Searchable text and links could not be read on this restricted page; the pixels were still captured.");
  }
  const tile = {
    captureId: session.id,
    index: 0,
    blob: source,
    destX: 0,
    destY: 0,
    destXPx: 0,
    destYPx: 0,
    widthCss: width,
    heightCss: height,
    widthPx: width,
    heightPx: height,
    scaleX: 1,
    scaleY: 1,
    seamScore: null
  };
  await putTile(tile);
  session.report.dimensionsCss ||= { width, height };
  session.report.dimensionsPx = { width, height };
  session.report.tileCount = 1;
  session.report.seamConfidence = { minimum: 1, average: 1, method: "single browser screenshot" };
  session.report.coverage = { complete: true, ratio: 1, gaps: [] };
  session.report.restoration = "not needed";
  session.state = SessionState.COMPLETE;
}

async function storeScrollingCapture(session, controller) {
  let prepared = false;
  let restored = false;
  const tileMetadata = [];
  const workingTiles = [];
  const seamScores = [];
  const semanticText = [];
  const semanticLinks = [];
  const semanticTextKeys = new Set();
  const semanticLinkKeys = new Set();
  const capturedPositions = new Set();
  const startTime = Date.now();
  let expectedScale = null;
  let frameHost = null;
  let frameHostPrepared = [];
  let extent;
  let viewportSize;

  try {
    await injectCaptureScript(session.tabId, session.frameId);
    const prep = await sendToTab(session.tabId, {
      type: "WINSHOT_PREPARE",
      sessionId: session.id,
      mode: session.mode,
      target: session.target
    }, session.frameId);
    prepared = true;
    if (session.frameId && !prep.metrics.frame?.accessible) {
      frameHost = await locateFrameChain(session);
      frameHostPrepared = frameHost.preparedFrameIds;
    }
    extent = { width: prep.metrics.width, height: prep.metrics.height };
    viewportSize = {
      width: Math.max(1, prep.metrics.clip.width || prep.metrics.viewport.width),
      height: Math.max(1, prep.metrics.clip.height || prep.metrics.viewport.height)
    };
    session.report.warnings.push(...(prep.warnings || []));
    session.report.dimensionsCss = { ...extent };
    session.report.target = prep.metrics.kind;
    session.state = SessionState.CAPTURING;
    await putRecord(session);

    if (extent.width > session.limits.maxWidthCss || extent.height > session.limits.maxHeightCss) {
      session.report.warnings.push(`Content exceeds the configured ${session.limits.maxWidthCss}×${session.limits.maxHeightCss} CSS-pixel safety limit.`);
      extent.width = Math.min(extent.width, session.limits.maxWidthCss);
      extent.height = Math.min(extent.height, session.limits.maxHeightCss);
    }

    let growthPass = 0;
    let index = 0;
    while (growthPass <= session.limits.maxGrowthPasses) {
      const plannedExtent = { ...extent };
      const plan = buildTilePlan({
        width: extent.width,
        height: extent.height,
        viewportWidth: viewportSize.width,
        viewportHeight: viewportSize.height,
        overlap: session.limits.overlapCss
      });
      let capturedThisPass = 0;
      for (const position of plan) {
        const key = `${position.x.toFixed(2)}:${position.y.toFixed(2)}`;
        if (capturedPositions.has(key)) continue;
        throwIfAborted(controller.signal);
        if (index >= session.limits.maxTiles) throw new Error(`Maximum tile limit (${session.limits.maxTiles}) reached.`);
        if (Date.now() - startTime >= session.limits.maxDurationMs) throw new Error("Maximum capture duration reached.");
        await assertStillActive(session);
        if (frameHostPrepared.length) await heartbeatFrameChain(session, frameHostPrepared);

        progress(session, `Capturing tile ${index + 1}…`);
        const result = await sendToTab(session.tabId, {
          type: "WINSHOT_SCROLL",
          sessionId: session.id,
          x: position.x,
          y: position.y,
          extent,
          viewport: viewportSize,
          settleTimeoutMs: session.limits.settleTimeoutMs
        }, session.frameId);
        if (result.metrics.url !== (session.frameUrl || session.sourceUrl)) throw new Error("The page navigated during capture.");
        if (!result.stable) session.report.warnings.push(`Tile ${index + 1} did not reach full layout stability before capture.`);
        for (const entry of result.semantics?.text || []) {
          const key = `${entry.text}|${entry.x.toFixed(1)}|${entry.y.toFixed(1)}`;
          if (!semanticTextKeys.has(key) && semanticText.length < 50000) {
            semanticTextKeys.add(key);
            semanticText.push(entry);
          }
        }
        for (const entry of result.semantics?.links || []) {
          const key = `${entry.url}|${entry.x.toFixed(1)}|${entry.y.toFixed(1)}`;
          if (!semanticLinkKeys.has(key) && semanticLinks.length < 10000) {
            semanticLinkKeys.add(key);
            semanticLinks.push(entry);
          }
        }

        const source = await screenshot(session.windowId, session.limits.minCaptureIntervalMs, controller.signal);
        const cropped = await cropScreenshot(source, screenshotMetrics(result.metrics, frameHost));
        const scale = (cropped.scaleX + cropped.scaleY) / 2;
        if (expectedScale === null) expectedScale = scale;
        else if (Math.abs(scale - expectedScale) / expectedScale > 0.02) {
          throw new Error("Browser zoom or display scaling changed during capture. Retry without moving the browser between monitors.");
        }

        const tile = {
          captureId: session.id,
          index,
          blob: cropped.blob,
          destX: result.metrics.destX,
          destY: result.metrics.destY,
          destXPx: Math.round(result.metrics.destX * expectedScale),
          destYPx: Math.round(result.metrics.destY * expectedScale),
          widthCss: cropped.width / expectedScale,
          heightCss: cropped.height / expectedScale,
          widthPx: cropped.width,
          heightPx: cropped.height,
          scaleX: cropped.scaleX,
          scaleY: cropped.scaleY,
          stable: result.stable,
          reachedX: result.metrics.reachedX,
          reachedY: result.metrics.reachedY,
          seamScore: null
        };

        const previous = [...workingTiles].reverse().find((candidate) =>
          candidate.destXPx < tile.destXPx + tile.widthPx && candidate.destXPx + candidate.widthPx > tile.destXPx &&
          candidate.destYPx < tile.destYPx + tile.heightPx && candidate.destYPx + candidate.heightPx > tile.destYPx);
        if (previous) {
          tile.seamScore = await seamScore(previous, tile);
          if (tile.seamScore !== null) seamScores.push(tile.seamScore);
        }

        await putTile(tile);
        workingTiles.push(tile);
        tileMetadata.push({ ...tile, blob: undefined });
        capturedPositions.add(key);
        index++;
        capturedThisPass++;
        session.report.tileCount = index;
        session.report.dimensionsCss = { ...extent };
        await putRecord({ ...session, tiles: tileMetadata });

        const measuredWidth = Math.min(result.metrics.width, session.limits.maxWidthCss);
        const measuredHeight = Math.min(result.metrics.height, session.limits.maxHeightCss);
        if (measuredWidth > extent.width + 1 || measuredHeight > extent.height + 1) {
          extent = { width: Math.max(extent.width, measuredWidth), height: Math.max(extent.height, measuredHeight) };
        }
      }
      if (!capturedThisPass) break;
      const latest = await sendToTab(session.tabId, { type: "WINSHOT_METRICS", sessionId: session.id }, session.frameId);
      const nextExtent = {
        width: Math.min(latest.width, session.limits.maxWidthCss),
        height: Math.min(latest.height, session.limits.maxHeightCss)
      };
      extent = { width: Math.max(extent.width, nextExtent.width), height: Math.max(extent.height, nextExtent.height) };
      if (extent.width <= plannedExtent.width + 1 && extent.height <= plannedExtent.height + 1) break;
      growthPass++;
    }

    if (growthPass > session.limits.maxGrowthPasses) {
      session.report.warnings.push("Content kept growing. Capture stopped at the explicit growth limit instead of guessing an end.");
    }

    const coverage = validateCoverage(extent.width, extent.height, tileMetadata);
    session.report.coverage = coverage;
    session.report.dimensionsCss = { ...extent };
    session.report.dimensionsPx = {
      width: Math.round(extent.width * (expectedScale || 1)),
      height: Math.round(extent.height * (expectedScale || 1))
    };
    const minimum = seamScores.length ? Math.min(...seamScores) : 1;
    const average = seamScores.length ? seamScores.reduce((sum, value) => sum + value, 0) / seamScores.length : 1;
    session.report.seamConfidence = { minimum, average, method: "DOM placement plus raster overlap comparison" };
    session.semantics = { text: semanticText, links: semanticLinks };
    if (minimum < 0.7) session.report.warnings.push("One or more overlaps changed substantially; the result is marked partial rather than silently accepted.");
    if (!coverage.complete) session.report.warnings.push(...coverage.gaps);
    session.state = coverage.complete && minimum >= 0.7 && growthPass <= session.limits.maxGrowthPasses
      ? SessionState.COMPLETE
      : SessionState.PARTIAL;
  } finally {
    if (prepared) {
      try {
        const result = await sendToTab(session.tabId, { type: "WINSHOT_RESTORE", sessionId: session.id, reason: "capture-finished" }, session.frameId);
        restored = Boolean(result?.restored);
      } catch {
        restored = false;
      }
      session.report.restoration = restored ? "verified" : "watchdog requested; page disconnected before acknowledgement";
    }
    if (frameHostPrepared.length) await restoreFrameChain(session, frameHostPrepared);
  }
}

async function runCapture(mode, target = null, tabOverride = null, options = {}) {
  const tab = tabOverride || await activeTab();
  if (!tab.id) throw new Error("No active tab.");
  if (activeCaptures.has(tab.id)) throw new Error("A WinShot capture is already running in this tab.");

  const session = createSession({ id: captureId(), mode, tab, target });
  session.frameId = target?.frameId || 0;
  session.frameUrl = target?.frameUrl || null;
  session.fileName = `${fileSafeTitle(tab.title)}-${mode}`;
  const controller = new AbortController();
  const activeEntry = { session, controller, failureReason: null };
  activeCaptures.set(tab.id, activeEntry);
  await putRecord(session);

  try {
    if (mode === CaptureMode.VISIBLE) await storeVisibleCapture(session, controller);
    else await storeScrollingCapture(session, controller);
  } catch (error) {
    if (activeEntry.failureReason) {
      session.state = SessionState.FAILED;
      session.error = activeEntry.failureReason;
      session.report.warnings.push(session.error);
    } else if (error?.name === "AbortError") {
      session.state = SessionState.CANCELLED;
      session.report.warnings.push("Capture cancelled. The original page state was restored where the page remained connected.");
    } else {
      session.state = SessionState.FAILED;
      session.error = friendlyError(error);
      session.report.warnings.push(session.error);
      if (mode !== CaptureMode.VISIBLE && /restricted|cannot access|cannot be scripted|receiving end/i.test(String(error))) {
        session.report.fallbackUsed = restrictedPageReason(tab.url);
      }
    }
  } finally {
    session.finishedAt = new Date().toISOString();
    await putRecord(session);
    activeCaptures.delete(tab.id);
  }

  if (options.openEditor !== false && session.state !== SessionState.CANCELLED) {
    await chrome.tabs.create({ url: chrome.runtime.getURL(`src/editor.html?id=${encodeURIComponent(session.id)}`) });
  }
  return { captureId: session.id, state: session.state, error: session.error };
}

function supportedBatchUrl(url) {
  return /^https?:\/\//i.test(url || "");
}

async function waitForTabComplete(tabId, signal, timeoutMs = 30000) {
  const current = await chrome.tabs.get(tabId);
  if (current.status === "complete") return current;
  return await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => finish(new Error("The page did not finish loading within 30 seconds.")), timeoutMs);
    const onUpdated = (updatedId, info, tab) => {
      if (updatedId === tabId && info.status === "complete") finish(null, tab);
    };
    const onAbort = () => finish(new DOMException("Batch capture cancelled.", "AbortError"));
    const finish = (error, tab) => {
      clearTimeout(timeout);
      chrome.tabs.onUpdated.removeListener(onUpdated);
      signal.removeEventListener("abort", onAbort);
      if (error) reject(error); else resolve(tab);
    };
    chrome.tabs.onUpdated.addListener(onUpdated);
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

async function captureBatchTab(tab, controller) {
  throwIfAborted(controller.signal);
  await chrome.tabs.update(tab.id, { active: true });
  const ready = await waitForTabComplete(tab.id, controller.signal);
  await new Promise((resolve) => setTimeout(resolve, 250));
  throwIfAborted(controller.signal);
  const result = await runCapture(CaptureMode.FULL_PAGE, null, ready, { openEditor: false });
  return { tabId: tab.id, title: ready.title || tab.title || ready.url, url: ready.url, ...result };
}

async function runBatch(kind, urls = []) {
  if (activeBatch) throw new Error("A WinShot batch is already running.");
  const original = await activeTab();
  const controller = new AbortController();
  const createdTabIds = [];
  const batchId = captureId();
  const results = [];
  const skipped = [];
  activeBatch = { id: batchId, controller, currentTabId: null };

  try {
    let tabs;
    if (kind === "all-tabs") {
      const candidates = await chrome.tabs.query({ windowId: original.windowId });
      const supported = candidates.filter((tab) => supportedBatchUrl(tab.url));
      tabs = supported.slice(0, 20);
      for (const tab of candidates.filter((tab) => !supportedBatchUrl(tab.url))) {
        skipped.push({ title: tab.title || tab.url || "Restricted tab", reason: "This browser page cannot be scripted." });
      }
      if (supported.length > 20) skipped.push({ title: "Additional tabs", reason: "The 20-tab safety limit was reached." });
    } else if (kind === "urls") {
      const unique = [...new Set(urls.map((url) => String(url).trim()).filter(Boolean))];
      const valid = unique.filter(supportedBatchUrl);
      for (const url of unique.filter((url) => !supportedBatchUrl(url))) skipped.push({ title: url, reason: "Only http:// and https:// URLs are supported." });
      if (valid.length > 20) skipped.push({ title: "Additional URLs", reason: "The 20-URL safety limit was reached." });
      tabs = [];
      for (const url of valid.slice(0, 20)) {
        throwIfAborted(controller.signal);
        const tab = await chrome.tabs.create({ url, active: false, windowId: original.windowId });
        createdTabIds.push(tab.id);
        tabs.push(tab);
      }
    } else {
      throw new Error("Unknown batch capture type.");
    }

    if (!tabs.length) throw new Error("No capturable web pages were found.");
    for (let index = 0; index < tabs.length; index++) {
      const tab = tabs[index];
      activeBatch.currentTabId = tab.id;
      progress({ id: batchId, state: SessionState.CAPTURING, report: { tileCount: index } }, `Batch ${index + 1} of ${tabs.length}: ${tab.title || tab.url}`);
      try {
        results.push(await captureBatchTab(tab, controller));
      } catch (error) {
        if (error?.name === "AbortError") throw error;
        results.push({ tabId: tab.id, title: tab.title || tab.url, url: tab.url, state: SessionState.FAILED, error: friendlyError(error) });
      }
    }
  } finally {
    for (const tabId of createdTabIds) {
      try { await chrome.tabs.remove(tabId); } catch { /* The page may already be closed. */ }
    }
    try { await chrome.tabs.update(original.id, { active: true }); } catch { /* The original tab may be gone. */ }
    activeBatch = null;
  }

  const summary = { id: batchId, kind, createdAt: new Date().toISOString(), results, skipped };
  await chrome.storage.local.set({ [`batch:${batchId}`]: summary });
  await chrome.tabs.create({ url: chrome.runtime.getURL(`src/batch.html?id=${encodeURIComponent(batchId)}`) });
  return {
    batchId,
    complete: results.filter((item) => item.state === SessionState.COMPLETE).length,
    failed: results.filter((item) => item.state !== SessionState.COMPLETE).length,
    skipped: skipped.length
  };
}

async function startPicker(mode) {
  const tab = await activeTab();
  try {
    const injected = await injectCaptureScript(tab.id, 0, true);
    const frameIds = [...new Set(injected.map((result) => result.frameId))];
    pickerFrames.set(tab.id, frameIds);
    const started = await Promise.allSettled(frameIds.map((frameId) =>
      sendToTab(tab.id, { type: "WINSHOT_START_PICKER", mode }, frameId)));
    if (!started.some((result) => result.status === "fulfilled")) throw new Error("The picker could not start in any accessible frame.");
    return { started: true, frameCount: frameIds.length };
  } catch (error) {
    pickerFrames.delete(tab.id);
    throw new Error(`${restrictedPageReason(tab.url)} (${friendlyError(error)})`);
  }
}

async function stopPickers(tabId, keepFrameId = null) {
  const frameIds = pickerFrames.get(tabId) || [];
  pickerFrames.delete(tabId);
  await Promise.allSettled(frameIds.filter((frameId) => frameId !== keepFrameId).map((frameId) =>
    sendToTab(tabId, { type: "WINSHOT_CANCEL_PICKER" }, frameId)));
}

async function cancelActiveCapture() {
  const tab = await activeTab();
  const active = activeCaptures.get(tab.id);
  active?.controller.abort();
  if (activeBatch) {
    activeBatch.controller.abort();
    activeCaptures.get(activeBatch.currentTabId)?.controller.abort();
  }
  return { cancelled: Boolean(active || activeBatch) };
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const handle = async () => {
    if (message.type === "WINSHOT_START_CAPTURE") return await runCapture(message.mode, message.target || null);
    if (message.type === "WINSHOT_START_PICKER") return await startPicker(message.mode);
    if (message.type === "WINSHOT_START_BATCH") return await runBatch(message.kind, message.urls || []);
    if (message.type === "WINSHOT_CANCEL_CAPTURE") {
      return await cancelActiveCapture();
    }
    if (message.type === "WINSHOT_PICK_RESULT") {
      const tab = sender.tab;
      if (!tab?.id) throw new Error("The picker tab is no longer available.");
      await stopPickers(tab.id, sender.frameId || 0);
      if (message.cancelled) return { cancelled: true };
      const target = {
        ...(message.target || {}),
        frameId: sender.frameId || 0,
        frameUrl: sender.url || tab.url
      };
      runCapture(message.mode, target, tab).catch(() => {});
      return { started: true };
    }
    return undefined;
  };
  handle().then(sendResponse, (error) => sendResponse({ error: friendlyError(error) }));
  return true;
});

async function handleCommand(command) {
  if (command === "capture-visible") return await runCapture(CaptureMode.VISIBLE);
  if (command === "capture-full-page") return await runCapture(CaptureMode.FULL_PAGE);
  return undefined;
}

chrome.commands.onCommand.addListener((command) => { handleCommand(command).catch(() => {}); });

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  const active = activeCaptures.get(tabId);
  if (!active || (!changeInfo.url && changeInfo.status !== "loading")) return;
  active.failureReason = "The page navigated during capture. WinShot stopped without claiming a complete image.";
  active.controller.abort();
});

chrome.tabs.onRemoved.addListener((tabId) => {
  const active = activeCaptures.get(tabId);
  if (!active) return;
  active.failureReason = "The captured tab closed during capture. No complete image was claimed.";
  active.controller.abort();
});

// Playwright evaluates inside the private service-worker context because current branded
// Chrome/Edge builds no longer accept side-load flags. This hook is not visible to webpages.
globalThis.__winshotTest = Object.freeze({ runCapture, runBatch, startPicker, cancelActiveCapture, handleCommand });

recoverInterruptedRecords().catch(() => {});
