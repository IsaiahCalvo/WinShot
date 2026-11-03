// Development auto-update. When WinShot is loaded unpacked ("Load unpacked" in
// chrome://extensions), the build writes src/build-stamp.json last. Every 30 seconds the
// service worker reads it; when it differs from the stamp this copy started with, the
// extension reloads itself so a fresh build shows up with no clicks. Open WinShot pages
// (editor, history, settings) are reopened after the reload; marks are already saved.
//
// Store and policy installs never run this: getSelf() must report installType
// "development", which only an unpacked extension has.

const ALARM = "winshot-dev-reload";
const LOADED_KEY = "winshot:dev-loaded-stamp";   // session storage: cleared by every reload
const REOPEN_KEY = "winshot:dev-reopen-tabs";    // local storage: survives the reload

async function isDevelopmentInstall() {
  try {
    const self = await chrome.management.getSelf();
    return self.installType === "development";
  } catch {
    return false;
  }
}

async function readStamp() {
  try {
    const response = await fetch(chrome.runtime.getURL("src/build-stamp.json"), { cache: "no-store" });
    if (!response.ok) return null;
    const stamp = await response.json();
    return typeof stamp?.id === "string" && stamp.id ? stamp.id : null;
  } catch {
    // Missing while a build is half-way through copying files: try again next time.
    return null;
  }
}

async function reopenPages() {
  const { [REOPEN_KEY]: pages } = await chrome.storage.local.get(REOPEN_KEY);
  if (!Array.isArray(pages) || !pages.length) return;
  await chrome.storage.local.remove(REOPEN_KEY);
  for (const page of pages) {
    try {
      await chrome.tabs.create({ url: page.url, windowId: page.windowId, index: page.index, active: page.active });
    } catch {
      try { await chrome.tabs.create({ url: page.url, active: page.active }); } catch { /* window gone */ }
    }
  }
}

async function check(isBusy) {
  const current = await readStamp();
  if (!current) return;
  const { [LOADED_KEY]: loaded } = await chrome.storage.session.get(LOADED_KEY);
  if (!loaded) {
    await chrome.storage.session.set({ [LOADED_KEY]: current });
    return;
  }
  if (loaded === current || isBusy()) return;
  const base = chrome.runtime.getURL("");
  const tabs = await chrome.tabs.query({});
  const pages = tabs
    .filter((tab) => tab.url?.startsWith(base) && !tab.url.includes("/src/popup.html"))
    .map((tab) => ({ url: tab.url, windowId: tab.windowId, index: tab.index, active: tab.active }));
  if (pages.length) await chrome.storage.local.set({ [REOPEN_KEY]: pages });
  chrome.runtime.reload();
}

export const DEV_RELOAD_ALARM = ALARM;

// Called from the service worker's top-level onAlarm listener (MV3 needs listeners
// registered synchronously so the alarm can wake the worker).
export async function handleDevReloadAlarm(isBusy) {
  if (!(await isDevelopmentInstall())) return;
  await check(isBusy);
}

// isBusy: () => boolean — true while a capture or batch runs, so a reload never cuts one off.
export async function startDevReload(isBusy) {
  if (!(await isDevelopmentInstall())) return false;
  await reopenPages().catch(() => {});
  if (!(await chrome.alarms.get(ALARM))) chrome.alarms.create(ALARM, { periodInMinutes: 0.5 });
  // Record what this copy loaded with straight away, not on the first alarm.
  await check(isBusy).catch(() => {});
  return true;
}
