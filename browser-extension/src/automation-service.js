import { loadSettings } from "./settings.js";

const requests = new Map();
const earlyCancellations = new Map();
const originOf = value => { try { const url = new URL(value); return /^https?:$/.test(url.protocol) ? url.origin : null; } catch { return null; } };
const allowed = (settings, origin) => settings.automation.enabled && settings.automation.origins.includes(origin);
export async function installAutomationBridge(tab, revoke = false) {
  if (!tab?.id || !originOf(tab.url)) return;
  const settings = await loadSettings();
  if (!allowed(settings, originOf(tab.url))) {
    if (!revoke) return;
    await Promise.allSettled([
      chrome.scripting.executeScript({ target: { tabId: tab.id }, world: "ISOLATED", func: () => globalThis.__winshotAutomationBridge?.dispose() }),
      chrome.scripting.executeScript({ target: { tabId: tab.id }, world: "MAIN", func: () => window[Symbol.for("winshot.automation.api")]?.dispose() })
    ]);
    return;
  }
  // Bind injection to the checked document, so a navigation cannot install the
  // public API into the next (possibly ungranted) website.
  const frame = await chrome.webNavigation.getFrame({ tabId: tab.id, frameId: 0 });
  if (!frame?.documentId || !allowed(await loadSettings(), originOf(frame.url))) return;
  const target = { tabId: tab.id, documentIds: [frame.documentId] };
  // Static bundled code only. Websites never supply JavaScript for execution.
  await chrome.scripting.executeScript({ target, files: ["src/automation-content.js"] });
  await chrome.scripting.executeScript({ target, world: "MAIN", files: ["src/automation-page.js"] });
}
export async function refreshAutomationBridge({ revoke = false } = {}) {
  const settings = await loadSettings();
  for (const request of requests.values()) if (!allowed(settings, request.origin)) request.controller.abort();
  const tabs = await chrome.tabs.query({});
  await Promise.allSettled(tabs.filter(tab => originOf(tab.url)).map(tab => installAutomationBridge(tab, revoke)));
  return { enabled: settings.automation.enabled, origins: settings.automation.origins };
}
export function cancelAutomationForNavigation(tabId) {
  for (const request of requests.values()) if (request.tabId === tabId) request.controller.abort();
}
export async function handleAutomationRequest(message, sender, capture) {
  const tab = sender.tab, origin = originOf(sender.url);
  const settings = await loadSettings();
  if (!tab?.id || sender.frameId !== 0 || !origin || origin !== originOf(tab.url) || !allowed(settings, origin)) throw new Error("Website automation is not enabled for this page.");
  const current = await chrome.tabs.get(tab.id);
  const frame = await chrome.webNavigation.getFrame({ tabId: tab.id, frameId: 0 });
  if (originOf(current.url) !== origin || originOf(frame?.url) !== origin || (!sender.documentId || frame?.documentId !== sender.documentId)) throw new Error("The requesting page changed before its capture could start.");
  if (typeof message.id !== "string" || !/^[a-zA-Z0-9-]{1,80}$/.test(message.id)) throw new Error("Invalid automation request identity.");
  if (message.action === "cancel") {
    if (typeof message.targetId !== "string" || !/^[a-zA-Z0-9-]{1,80}$/.test(message.targetId)) throw new Error("Missing capture request identity.");
    const key = `${tab.id}:${message.targetId}`, request = requests.get(key);
    if (request?.origin === origin) request.controller.abort();
    // Storage-policy reads can complete out of order. Remember a cancellation
    // that races the capture's first message without ever touching manual work.
    earlyCancellations.set(key, { origin, expires: Date.now() + 60_000 });
    if (earlyCancellations.size > 128) earlyCancellations.delete(earlyCancellations.keys().next().value);
    return { cancelled: true };
  }
  if (message.action !== "capture" || !["visible", "full-page"].includes(message.mode)) throw new Error("The page API supports visible or full-page capture only.");
  if ([...requests.values()].some(request => request.tabId === tab.id)) throw new Error("This page already has an automation capture running.");
  const key = `${tab.id}:${message.id}`, controller = new AbortController();
  for (const [id, entry] of earlyCancellations) if (entry.expires < Date.now()) earlyCancellations.delete(id);
  if (earlyCancellations.get(key)?.origin === origin) {
    earlyCancellations.delete(key);
    throw new DOMException("Capture cancelled.", "AbortError");
  }
  requests.set(key, { tabId: tab.id, origin, controller });
  try {
    const latest = await loadSettings();
    if (!allowed(latest, origin)) throw new Error("Website access was revoked before capture could start.");
    if (controller.signal.aborted) throw new DOMException("Capture cancelled.", "AbortError");
    const result = await capture(current, message.mode, controller, latest);
    return { captureId: result.captureId, state: result.state, ...(result.error ? { error: result.error } : {}) };
  } finally { requests.delete(key); }
}
