import { importImage, importProject } from "./project-file.js";

const input = document.querySelector("#open-file"), status = document.querySelector("#status"), drop = document.querySelector("#drop-zone"), paste = document.querySelector("#paste-image");
let busy = false;
function error(message) { status.className = "error"; status.textContent = message; }
async function open(file) {
  if (busy || !file) return;
  busy = true; input.disabled = paste.disabled = true;
  status.className = ""; status.textContent = "Opening your file…";
  try {
    const record = /\.json$/i.test(file.name || "") || file.type === "application/json" ? await importProject(file) : await importImage(file);
    location.href = `editor.html?id=${encodeURIComponent(record.id)}`;
  } catch (e) { error(e.message); }
  finally { busy = false; input.disabled = paste.disabled = false; input.value = ""; }
}
input.addEventListener("change", () => open(input.files[0]));
drop.addEventListener("dragover", (e) => { e.preventDefault(); drop.classList.add("drag"); });
drop.addEventListener("dragleave", () => drop.classList.remove("drag"));
drop.addEventListener("drop", (e) => { e.preventDefault(); drop.classList.remove("drag"); if (e.dataTransfer.files.length !== 1) error("Open one image or project at a time."); else open(e.dataTransfer.files[0]); });
document.addEventListener("paste", (e) => {
  const file = [...e.clipboardData.items].find((item) => item.kind === "file" && item.type.startsWith("image/"))?.getAsFile();
  if (file) { e.preventDefault(); open(file); }
});
paste.addEventListener("click", async () => {
  try {
    if (!navigator.clipboard?.read) throw new Error("Press Ctrl+V or ⌘V to paste an image here.");
    const items = await navigator.clipboard.read();
    for (const item of items) {
      const type = item.types.find((t) => /^image\/(png|jpeg|webp|gif|bmp)$/.test(t));
      if (type) { await open(await item.getType(type)); return; }
    }
    throw new Error("The clipboard has no image. Copy an image, then paste again.");
  } catch (e) { error(/notallowed|permission|denied/i.test(e.name || e.message) ? "Clipboard access was not granted. Press Ctrl+V or ⌘V to paste, or choose a file." : e.message); }
});
