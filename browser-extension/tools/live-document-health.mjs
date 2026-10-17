// Public-site QA guards only. A successful capture of a network error page is
// not evidence that the intended public document was tested successfully.
function withoutFragment(url) {
  try { const value = new URL(url); value.hash = ""; return value.href; }
  catch { return String(url || "").split("#")[0]; }
}

export function trackDocumentResponses(page) {
  const responses = new WeakMap();
  const receive = response => {
    const request = response.request();
    if (!request.isNavigationRequest() || request.resourceType() !== "document") return;
    const frame = request.frame();
    if (frame) responses.set(frame, { url: response.url(), status: response.status() });
  };
  page.on("response", receive);
  return {
    current(frame, documentUrl = frame.url()) {
      const response = responses.get(frame);
      // A Frame survives navigation. Never apply an old HTTP response to its
      // new document (including an ordinary about:srcdoc or about:blank page).
      return response && withoutFragment(response.url) === withoutFragment(documentUrl) ? { ...response } : null;
    },
    dispose() { page.off("response", receive); }
  };
}

export function publicDocumentError({ url = "", title = "", text = "", response = null }) {
  if (response?.status >= 400 && withoutFragment(response.url) === withoutFragment(url)) return `HTTP ${response.status} loading ${url}`;
  if (/^(?:chrome-error:|edge-error:|about:neterror(?:[?#]|$))/i.test(url)) return `Browser error document: ${url}`;
  const heading = title.trim().replace(/\s+/g, " ");
  if (/^(?:just a moment[.!…]*|access denied[.!]*|attention required(?:!|\s*\|\s*cloudflare)?|privacy error|error response|(?:[45]\d\d\s+)?(?:bad gateway|service unavailable|forbidden|proxy error|gateway time(?:d?\s*out|out))|this site can(?:not|'t|’t) be reached)[.!…]*$/i.test(heading)) {
    return `Blocked or error document: ${heading}`;
  }
  const body = text.trim().replace(/\s+/g, " ");
  // Match explicit error-page signatures at the start of the visible document,
  // not mentions of errors in a normal tutorial, reference page or application.
  if (/^(?:upstream connect error or disconnect\/reset before headers\b|upstream request timeout\b|rbac:\s*access denied\b|blocked:\s*only https requests are allowed\b|error:\s*the requested url could not be retrieved\b|this request (?:has been|was) blocked by (?:the )?proxy\b)/i.test(body) ||
      /^(?:this site can(?:not|'t|’t) be reached\b|your connection is not private\b|verify (?:that )?you are human\b|checking (?:your )?browser before accessing\b)/i.test(body) && body.length < 3000) {
    return `Proxy, browser or access-block error document: ${body.slice(0, 180)}`;
  }
  return null;
}

export async function inspectPublicDocument(frame, tracker) {
  const document = await frame.evaluate(() => ({ url: location.href, title: document.title, text: document.body?.innerText.slice(0, 5000) || "" }));
  document.response = tracker.current(frame, document.url);
  return { ...document, error: publicDocumentError(document) };
}
