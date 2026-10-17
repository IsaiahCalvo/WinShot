import test from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { inspectPublicDocument, publicDocumentError, trackDocumentResponses } from "../../tools/live-document-health.mjs";

function emitResponse(page, frame, status, url = frame.url(), type = "document", navigation = true) {
  page.emit("response", { url: () => url, status: () => status, request: () => ({ frame: () => frame, resourceType: () => type, isNavigationRequest: () => navigation }) });
}

test("native document status belongs to the exact selected frame, including duplicate URLs", async () => {
  const page = new EventEmitter(), tracker = trackDocumentResponses(page);
  const healthy = { url: () => "https://example.com/embedded", evaluate: async () => ({ url: healthy.url(), title: "Embedded application", text: "Normal application content" }) };
  const failed = { url: healthy.url, evaluate: async () => ({ url: failed.url(), title: "", text: "upstream connect error or disconnect/reset before headers. reset reason: connection termination" }) };
  emitResponse(page, healthy, 200); emitResponse(page, failed, 503);
  emitResponse(page, healthy, 500, healthy.url(), "image", false);
  assert.equal((await inspectPublicDocument(healthy, tracker)).error, null);
  assert.match((await inspectPublicDocument(failed, tracker)).error, /HTTP 503/);
  assert.equal(tracker.current(healthy).status, 200);
  tracker.dispose(); assert.equal(page.listenerCount("response"), 0);
});

test("document navigation replaces redirect/error status and never poisons healthy srcdoc", () => {
  const page = new EventEmitter(), tracker = trackDocumentResponses(page);
  let url = "https://example.com/old";
  const frame = { url: () => url };
  emitResponse(page, frame, 503);
  url = "https://example.com/redirect"; emitResponse(page, frame, 302);
  url = "https://example.com/final"; emitResponse(page, frame, 200);
  assert.equal(tracker.current(frame).status, 200);
  url += "#section"; assert.equal(tracker.current(frame).status, 200);
  url = "about:srcdoc";
  assert.equal(tracker.current(frame), null);
  assert.equal(publicDocumentError({ url, title: "Embedded content", text: "A normal embedded document", response: { url: "https://example.com/old", status: 503 } }), null);
  tracker.dispose();
});

test("proxy and browser failure documents are rejected even without a failing HTTP status", () => {
  for (const response of [null, { url: "https://example.com/frame", status: 200 }]) {
    assert.match(publicDocumentError({ url: "https://example.com/frame", text: "upstream connect error or disconnect/reset before headers. reset reason: connection termination", response }), /error document/i);
    assert.match(publicDocumentError({ url: "https://example.com/frame", title: "Just a moment...", text: "Verifying your browser", response }), /Blocked/);
  }
  assert.match(publicDocumentError({ url: "chrome-error://chromewebdata/", title: "example.com" }), /Browser error/);
  assert.match(publicDocumentError({ url: "about:srcdoc", text: "Blocked: Only HTTPS requests are allowed" }), /error document/i);
  assert.match(publicDocumentError({ title: "", text: "This site can’t be reached Check if there is a typo in example.com. ERR_NAME_NOT_RESOLVED" }), /error document/i);
});

test("normal pages discussing HTTP failures and ordinary embedded documents remain eligible", () => {
  for (const document of [
    { url: "https://example.com/docs/errors", title: "503 Service Unavailable explained", text: "HTTP errors include upstream connect error or disconnect/reset before headers." },
    { url: "https://example.com/proxy-reference", title: "Proxy error reference", text: "Learn how a proxy error document is generated." },
    { url: "about:srcdoc", title: "Inline example", text: "A complete ordinary inline example" },
    { url: "about:blank", title: "Application", text: "A document constructed by the parent application" }
  ]) assert.equal(publicDocumentError(document), null);
});
