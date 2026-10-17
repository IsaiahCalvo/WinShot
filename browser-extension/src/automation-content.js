(() => {
  if (globalThis.__winshotAutomationBridge) return;
  const receive = event => {
    const request = event.data;
    if (event.source !== window || event.origin !== location.origin || request?.source !== "winshot-page-request-v1") return;
    if (typeof request.id !== "string" || request.id.length > 80) return;
    chrome.runtime.sendMessage({ type: "WINSHOT_AUTOMATION_REQUEST", id: request.id, action: request.action, mode: request.mode, targetId: request.targetId })
      .then(result => postMessage({ source: "winshot-page-response-v1", id: request.id, result }, location.origin))
      .catch(error => postMessage({ source: "winshot-page-response-v1", id: request.id, result: { error: error.message } }, location.origin));
  };
  addEventListener("message", receive);
  globalThis.__winshotAutomationBridge = { dispose() { removeEventListener("message", receive); delete globalThis.__winshotAutomationBridge; } };
})();
