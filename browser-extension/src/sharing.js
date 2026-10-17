import { createMimeMessage } from "./vendor/mimetext/mimetext.mjs";

const MAX_SHARE_BYTES = 32 * 1024 * 1024;
const MAX_EMAIL_BYTES = 18 * 1024 * 1024;
const utf8 = new TextEncoder();

export function safeShareName(value = "winshot-capture.png") {
  return String(value).normalize("NFKD").replace(/[^\x20-\x7e]|[\\/<>:"|?*]/g, "_").replace(/^\.+/, "_").slice(0, 180) || "winshot-capture.png";
}
function safeUrl(value) {
  try { const url = new URL(value); return /^(https?:)$/.test(url.protocol) ? url.href : ""; }
  catch { return ""; }
}
function escapeHtml(value) { return String(value).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]); }
function base64(bytes) {
  let binary = "";
  for (let i = 0; i < bytes.length; i += 16384) binary += String.fromCharCode(...bytes.subarray(i, i + 16384));
  return btoa(binary);
}
function folded(value) { return value.match(/.{1,76}/g)?.join("\r\n") || ""; }
function encodedSubject(value) {
  const words = []; let word = "", bytes = 0;
  for (const char of value) {
    const length = utf8.encode(char).length;
    if (bytes + length > 42) { words.push(`=?UTF-8?B?${base64(utf8.encode(word))}?=`); word = ""; bytes = 0; }
    word += char; bytes += length;
  }
  if (word) words.push(`=?UTF-8?B?${base64(utf8.encode(word))}?=`);
  return words.join("\r\n ");
}
function checkPayload(payload, max = MAX_SHARE_BYTES) {
  if (!(payload?.blob instanceof Blob) || !payload.blob.size) throw new Error("There is no image to share yet.");
  if (payload.blob.size > max) throw new Error(`This image is too large for this sharing option (${Math.round(max / 1024 / 1024)} MB limit). Save the image and attach it manually.`);
  if (payload.blob.type !== "image/png") throw new Error("Sharing needs a flattened PNG image.");
  return payload;
}

export async function createEmailDraft(payload) {
  checkPayload(payload, MAX_EMAIL_BYTES);
  const message = createMimeMessage();
  // An unsent draft lets the mail application choose its own sending account.
  // Disable MIMEText's required From and its From-dependent ID generator.
  for (const field of message.headers.fields) if (["From", "Message-ID"].includes(field.name)) field.disabled = true;
  // MIMEText handles MIME structure and attachment encoding. Fold this header
  // at UTF-8 codepoint boundaries to honor RFC 2047's 75-character word limit.
  message.headers.fields.find(field => field.name === "Subject").dump = encodedSubject;
  message.setHeader("X-Unsent", "1");
  message.setSubject(String(payload.title || "WinShot capture").replace(/[\x00-\x1f\x7f]+/g, " ").slice(0, 150));
  const url = safeUrl(payload.sourceUrl);
  const text = `${String(payload.title || "WinShot capture").slice(0, 1000)}${url ? `\r\n${url}` : ""}\r\n\r\nImage attached. Created with WinShot.`;
  message.addMessage({ contentType: "text/plain", encoding: "base64", data: folded(base64(utf8.encode(text))) });
  message.addAttachment({ filename: safeShareName(payload.fileName), contentType: "image/png", data: folded(base64(new Uint8Array(await payload.blob.arrayBuffer()))) });
  return new Blob([message.asRaw() + "\r\n"], { type: "message/rfc822" });
}

export async function createNoteHtml(payload) {
  checkPayload(payload);
  const url = safeUrl(payload.sourceUrl), title = escapeHtml(String(payload.title || "WinShot capture").slice(0, 1000));
  const data = base64(new Uint8Array(await payload.blob.arrayBuffer()));
  return `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'"><title>${title}</title></head><body><h1>${title}</h1>${url ? `<p><a href="${escapeHtml(url)}" rel="noreferrer">${escapeHtml(url)}</a></p>` : ""}<img alt="${title}" src="data:image/png;base64,${data}" style="max-width:100%;height:auto"></body></html>`;
}

export function initializeSharing({ getPayload, notify }) {
  if (document.querySelector("#share-capture")) return;
  const style = document.createElement("link"); style.rel = "stylesheet"; style.href = "sharing.css"; document.head.append(style);
  const trigger = document.createElement("button"); trigger.id = "share-capture"; trigger.textContent = "Share";
  document.querySelector("header .actions")?.append(trigger);
  const dialog = document.createElement("dialog"); dialog.id = "share-dialog";
  dialog.innerHTML = `<form method="dialog" class="share-heading"><h2>Share your image</h2><button aria-label="Close sharing" value="close">×</button></form>
    <p>Share a flattened copy with your current edits.</p>
    <div class="share-options"><button id="share-onenote">Copy for OneNote</button><button id="share-email">Save email draft</button><button id="share-note">Save image note</button><button id="share-prepare">Prepare file to share or drag</button></div>
    <p class="share-help">For OneNote, copy here, open your note and paste. Email drafts include the image attachment; open the .eml file in a desktop mail app and choose your recipient. Some mail apps open .eml files as messages instead of drafts.</p>
    <div id="share-file-ready" hidden><button id="share-system">Share file…</button><a id="share-drag" draggable="true">Drag image or click to save</a></div><p id="share-status" role="status" aria-live="polite"></p>`;
  document.body.append(dialog);
  const status = dialog.querySelector("#share-status"); let file, fileUrl, busy = false, generation = 0;
  const buttons = () => [...dialog.querySelectorAll(".share-options button")];
  const revoke = () => { if (fileUrl) URL.revokeObjectURL(fileUrl); fileUrl = undefined; file = undefined; dialog.querySelector("#share-file-ready").hidden = true; };
  const invalidate = () => { generation++; busy = false; revoke(); buttons().forEach(b => b.disabled = false); };
  const download = (blob, name) => { const url = URL.createObjectURL(blob), a = document.createElement("a"); a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 30000); };
  const run = async (action) => {
    if (busy) return;
    const current = generation;
    const assertCurrent = () => { if (generation !== current || !dialog.open) throw new DOMException("Sharing was canceled.", "AbortError"); };
    busy = true; buttons().forEach((b) => b.disabled = true); status.textContent = "Preparing your edited image…";
    try { await action(assertCurrent); } catch (error) { if (generation === current && dialog.open) { status.textContent = error.message; notify(error.message); } }
    finally { if (generation === current) { busy = false; buttons().forEach((b) => b.disabled = false); } }
  };
  trigger.addEventListener("click", () => { invalidate(); status.textContent = ""; dialog.showModal(); });
  dialog.addEventListener("close", invalidate);
  dialog.addEventListener("keydown", event => event.stopPropagation());
  window.addEventListener("pagehide", invalidate);
  dialog.querySelector("#share-onenote").addEventListener("click", () => run(async (assertCurrent) => {
    if (!navigator.clipboard?.write || typeof ClipboardItem !== "function") throw new Error("Image copying is unavailable. Save an image note or PNG, then insert it into OneNote.");
    const payload = Promise.resolve().then(getPayload).then((p) => { assertCurrent(); return checkPayload(p); });
    // Pass promises immediately so encoding does not consume the user gesture.
    await navigator.clipboard.write([new ClipboardItem({
      "image/png": payload.then((p) => p.blob),
      "text/html": payload.then(async (p) => { const html = await createNoteHtml(p); assertCurrent(); return new Blob([html], { type: "text/html" }); }),
      "text/plain": payload.then((p) => new Blob([`${p.title || "WinShot capture"}\n${safeUrl(p.sourceUrl)}`], { type: "text/plain" }))
    })]);
    assertCurrent();
    status.textContent = "Copied. Open OneNote and paste into your note."; notify(status.textContent);
  }));
  dialog.querySelector("#share-email").addEventListener("click", () => run(async (assertCurrent) => {
    const payload = checkPayload(await getPayload(), MAX_EMAIL_BYTES);
    assertCurrent(); const draft = await createEmailDraft(payload); assertCurrent();
    download(draft, safeShareName(payload.fileName).replace(/\.png$/i, "") + ".eml");
    status.textContent = "Email draft saved with the edited image attached. Open it in your desktop mail app.";
  }));
  dialog.querySelector("#share-note").addEventListener("click", () => run(async (assertCurrent) => {
    const payload = checkPayload(await getPayload());
    assertCurrent(); const html = await createNoteHtml(payload); assertCurrent();
    download(new Blob([html], { type: "text/html" }), safeShareName(payload.fileName).replace(/\.png$/i, "") + ".html");
    status.textContent = "Image note saved. It contains your edited image and source link, and works offline.";
  }));
  dialog.querySelector("#share-prepare").addEventListener("click", () => run(async (assertCurrent) => {
    revoke(); const payload = checkPayload(await getPayload());
    assertCurrent();
    file = new File([payload.blob], safeShareName(payload.fileName), { type: "image/png" }); fileUrl = URL.createObjectURL(file);
    const drag = dialog.querySelector("#share-drag"); drag.href = fileUrl; drag.download = file.name;
    const system = dialog.querySelector("#share-system"), supported = typeof navigator.share === "function" && navigator.canShare?.({ files: [file] });
    system.disabled = !supported; dialog.querySelector("#share-file-ready").hidden = false;
    status.textContent = supported ? "Ready. Share the file, drag it to a supported app, or click to save." : "System file sharing is unavailable in this browser. Drag the image or click to save it.";
  }));
  dialog.querySelector("#share-system").addEventListener("click", async () => {
    if (!file) return;
    const current = generation;
    try { await navigator.share({ files: [file], title: "WinShot image" }); if (generation === current && dialog.open) status.textContent = "Share sheet closed."; }
    catch (error) { if (generation === current && dialog.open) status.textContent = error.name === "AbortError" ? "Sharing canceled. Your image is still ready." : `Sharing failed: ${error.message}`; }
  });
  dialog.querySelector("#share-drag").addEventListener("dragstart", (event) => {
    if (!file || !fileUrl || !event.dataTransfer) { event.preventDefault(); return; }
    event.dataTransfer.setData("DownloadURL", `${file.type}:${file.name}:${fileUrl}`);
    event.dataTransfer.setData("text/uri-list", fileUrl);
    event.dataTransfer.effectAllowed = "copy";
  });
}
