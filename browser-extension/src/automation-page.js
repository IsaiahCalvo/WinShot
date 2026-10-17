(() => {
  const key = Symbol.for("winshot.automation.api");
  if (window[key]) return;
  const previous = Object.getOwnPropertyDescriptor(window, "WinShotCapture");
  if (previous && previous.value?.version !== "winshot-page-api-v1") return;
  const pending = new Map();
  const requestId = () => typeof crypto.randomUUID === "function" ? crypto.randomUUID() : [...crypto.getRandomValues(new Uint8Array(16))].map(byte => byte.toString(16).padStart(2, "0")).join("");
  function request(action, data = {}, id = requestId()) {
    const promise = new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pending.delete(id); reject(new Error("WinShot did not finish this request. Check its capture status.")); }, 65 * 60 * 1000);
      pending.set(id, { resolve, reject, timer });
      postMessage({ source: "winshot-page-request-v1", id, action, ...data }, location.origin);
    });
    promise.requestId = id;
    return promise;
  }
  const receive = event => {
    const response = event.data;
    if (event.source !== window || event.origin !== location.origin || response?.source !== "winshot-page-response-v1") return;
    const item = pending.get(response.id);
    if (!item) return;
    clearTimeout(item.timer); pending.delete(response.id);
    response.result?.error ? item.reject(new Error(response.result.error)) : item.resolve(response.result);
  };
  addEventListener("message", receive);
  const api = Object.freeze({
    version: "winshot-page-api-v1",
    capture(options = {}) {
      if (!options || !["visible", "full-page"].includes(options.mode ?? "full-page")) return Promise.reject(new Error("Choose visible or full-page capture."));
      const result = request("capture", { mode: options.mode ?? "full-page" });
      result.cancel = () => request("cancel", { targetId: result.requestId });
      return result;
    },
    cancel: requestId => request("cancel", { targetId: requestId })
  });
  Object.defineProperty(window, "WinShotCapture", { configurable: true, value: api });
  window[key] = { dispose() {
    removeEventListener("message", receive);
    for (const item of pending.values()) { clearTimeout(item.timer); item.reject(new Error("WinShot website access was revoked.")); }
    pending.clear();
    if (window.WinShotCapture === api) previous ? Object.defineProperty(window, "WinShotCapture", previous) : delete window.WinShotCapture;
    delete window[key];
  } };
  dispatchEvent(new Event("winshot-ready"));
})();
