import test from "node:test";
import assert from "node:assert/strict";
import { normalizeSettings, normalizeAutomationOrigins } from "../../src/settings.js";
import { archiveFiles } from "../../src/file-archive.js";

test("website grants preserve exact HTTP origins and reject wildcard, path, credentials and opaque origins", () => {
  assert.deepEqual(normalizeAutomationOrigins(["https://example.com", "https://example.com/", "https://sub.example.com:8443", "https://*.example.com", "https://example.com/private", "file:///tmp/image.png", "https://user:pass@example.com", "https://example.com/?x=1", "null"]), ["https://example.com", "https://sub.example.com:8443"]);
  const settings = normalizeSettings({ captureActions: { visible: "png", region: "run-code" }, batchScope: "all-windows", batchOutput: "combined-pdf", automation: { enabled: true, origins: ["https://example.com"] } });
  assert.equal(settings.captureActions.visible, "png"); assert.equal(settings.captureActions.region, "editor"); assert.equal(settings.batchScope, "all-windows"); assert.equal(settings.batchOutput, "combined-pdf");
  assert.deepEqual(normalizeSettings().automation, { enabled: false, origins: [] });
});

test("batch ZIP stores UTF-8 file names and source bytes with valid CRC and central directory offsets", async () => {
  const bytes = Buffer.from(await (await archiveFiles([{ name: "01-日本.png", blob: new Blob(["123456789"]) }, { name: "02.pdf", blob: new Blob(["%PDF-example"]) }])).arrayBuffer());
  let at = 0; const names = [], offsets = [];
  for (let i = 0; i < 2; i++) {
    offsets.push(at); assert.equal(bytes.readUInt32LE(at), 0x04034b50);
    const size = bytes.readUInt32LE(at + 18), length = bytes.readUInt16LE(at + 26);
    names.push(bytes.subarray(at + 30, at + 30 + length).toString("utf8"));
    if (!i) { assert.equal(bytes.readUInt32LE(at + 14), 0xcbf43926); assert.equal(bytes.subarray(at + 30 + length, at + 30 + length + size).toString(), "123456789"); }
    at += 30 + length + size;
  }
  assert.deepEqual(names, ["01-日本.png", "02.pdf"]);
  for (const offset of offsets) { assert.equal(bytes.readUInt32LE(at), 0x02014b50); assert.equal(bytes.readUInt32LE(at + 42), offset); at += 46 + bytes.readUInt16LE(at + 28); }
  assert.equal(bytes.readUInt32LE(at), 0x06054b50); assert.equal(bytes.readUInt16LE(at + 10), 2);
});

test("automation backend rejects stale documents and child-frame senders before capture", async () => {
  const { handleAutomationRequest } = await import("../../src/automation-service.js");
  const previous = globalThis.chrome;
  const settings = { automation: { enabled: true, origins: ["https://example.com"] } };
  const sender = { tab: { id: 9, windowId: 2, url: "https://example.com/start" }, frameId: 0, url: "https://example.com/start", documentId: "old-document" };
  let called = false;
  globalThis.chrome = {
    storage: { local: { get: async () => ({ "winshot:capture-settings": settings }) } },
    tabs: { get: async () => ({ id: 9, windowId: 2, url: "https://example.com/next" }) },
    webNavigation: { getFrame: async () => ({ url: "https://example.com/next", documentId: "new-document" }) }
  };
  try {
    await assert.rejects(handleAutomationRequest({ id: "stale", action: "capture", mode: "visible" }, sender, () => { called = true; }), /page changed/);
    await assert.rejects(handleAutomationRequest({ id: "child", action: "capture", mode: "visible" }, { ...sender, frameId: 2 }, () => { called = true; }), /not enabled/);
    assert.equal(called, false);
  } finally { globalThis.chrome = previous; }
});

test("automation revocation during validation and cancellation before capture cannot start a late request", async () => {
  const { handleAutomationRequest } = await import("../../src/automation-service.js");
  const previous = globalThis.chrome, sender = { tab: { id: 42, windowId: 2, url: "https://example.com/" }, frameId: 0, url: "https://example.com/", documentId: "same-document" };
  let reads = 0, revoke = true, called = false;
  globalThis.chrome = {
    storage: { local: { get: async () => ({ "winshot:capture-settings": { automation: { enabled: !revoke || ++reads === 1, origins: ["https://example.com"] } } }) } },
    tabs: { get: async () => sender.tab },
    webNavigation: { getFrame: async () => ({ url: sender.url, documentId: sender.documentId }) }
  };
  try {
    await assert.rejects(handleAutomationRequest({ id: "revoked-before-start", action: "capture", mode: "visible" }, sender, () => { called = true; }), /revoked/);
    revoke = false;
    assert.deepEqual(await handleAutomationRequest({ id: "cancel-message", action: "cancel", targetId: "cancel-before-start" }, sender), { cancelled: true });
    await assert.rejects(handleAutomationRequest({ id: "cancel-before-start", action: "capture", mode: "visible" }, sender, () => { called = true; }), /cancelled/);
    assert.equal(called, false);
  } finally { globalThis.chrome = previous; }
});
