import test from "node:test";
import assert from "node:assert/strict";
import PostalMime from "postal-mime";
import { createEmailDraft, createNoteHtml, safeShareName } from "../../src/sharing.js";

test("email draft parses independently with exact attachment bytes and Unicode subject", async () => {
  const bytes = new Uint8Array(8193).map((_, i) => (i * 29) % 256);
  const draft = await createEmailDraft({ blob: new Blob([bytes], { type:"image/png" }), title:"Quarterly résumé 🚀\r\nBcc: evil@example.com", fileName:'../image"\r\nBcc: injected.png', sourceUrl:"https://example.com/a?q=notes" });
  const raw = await draft.text(), parsed = await PostalMime.parse(raw);
  assert.equal(parsed.subject, "Quarterly résumé 🚀 Bcc: evil@example.com");
  assert.equal(parsed.from, undefined); assert.equal(parsed.to, undefined); assert.equal(parsed.bcc, undefined);
  assert.equal(parsed.headers.find(h => h.key === "x-unsent")?.value, "1");
  assert.equal(parsed.attachments.length, 1); assert.equal(parsed.attachments[0].mimeType,"image/png");
  assert.deepEqual(new Uint8Array(parsed.attachments[0].content), bytes);
  assert.match(parsed.text, /https:\/\/example.com\/a\?q=notes/);
  assert(!/(?<!\r)\n/.test(raw));
  assert(raw.split("\r\n").every(line => line.length <= 998));
});

test("share notes embed only flattened image bytes with escaped metadata and safe source links", async () => {
  const bytes = new Uint8Array([137,80,78,71]);
  const html = await createNoteHtml({blob:new Blob([bytes],{type:"image/png"}),title:'<img src=x onerror="alert(1)">',sourceUrl:"javascript:alert(2)"});
  assert.match(html,/&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt;/);
  assert(!html.includes('href="javascript:')); assert(!html.includes('<img src=x'));
  assert(html.includes("default-src 'none'")); assert(html.includes("data:image/png;base64,iVBORw=="));
  const safe = await createNoteHtml({blob:new Blob([bytes],{type:"image/png"}),title:"A",sourceUrl:'https://example.com/?x="<x>'});
  assert(safe.includes('href="https://example.com/?x=%22%3Cx%3E"'));
});

test("long Unicode email titles use independently decodable RFC 2047 encoded words", async () => {
  const title = "🚀".repeat(70), draft = await createEmailDraft({blob:new Blob(["pixels"],{type:"image/png"}),title,fileName:"image.png"});
  const raw = await draft.text(), parsed = await PostalMime.parse(raw);
  assert.equal(parsed.subject,title);
  const words = raw.match(/=\?UTF-8\?B\?[^?]+\?=/g);
  assert(words.length > 1); assert(words.every(word=>word.length<=75));
  assert(raw.split("\r\n").every(line=>line.length<=998));
});

test("sharing rejects oversized payloads and non-flattened formats", async () => {
  await assert.rejects(createEmailDraft({blob:new Blob([new Uint8Array(18*1024*1024+1)],{type:"image/png"})}),/too large/);
  await assert.rejects(createNoteHtml({blob:new Blob(["project"],{type:"application/json"})}),/flattened PNG/);
  assert(!/[\\/"\r\n]/.test(safeShareName('../bad"\r\nname.png')));
});
