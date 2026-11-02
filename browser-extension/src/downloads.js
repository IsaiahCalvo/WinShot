// Saving files through chrome.downloads, so the user's "Save As" choice and the
// optional Downloads sub-folder from Settings apply to every export (FireShot parity).

function safeFolder(value) {
  return String(value || "")
    .split(/[\\/]+/)
    .map((part) => part.replace(/[<>:"|?*\x00-\x1f]/g, "-").replace(/^[. ]+|[. ]+$/g, "").trim())
    .filter((part) => part && part !== "." && part !== "..")
    .join("/");
}

function anchorDownload(url, filename) {
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = filename.split("/").pop();
  anchor.click();
}

function waitForDownload(id, timeoutMs = 120_000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => finish(new Error("The download did not finish in time.")), timeoutMs);
    const listener = (delta) => {
      if (delta.id !== id || !delta.state) return;
      if (delta.state.current === "complete") finish(null);
      else if (delta.state.current === "interrupted") finish(new Error("The download was cancelled or failed."));
    };
    const finish = async (error) => {
      clearTimeout(timer);
      chrome.downloads.onChanged.removeListener(listener);
      if (error) return reject(error);
      const [item] = await chrome.downloads.search({ id });
      resolve(item);
    };
    chrome.downloads.onChanged.addListener(listener);
    chrome.downloads.search({ id }).then(([item]) => {
      if (item?.state === "complete") finish(null);
      else if (item?.state === "interrupted") finish(new Error("The download was cancelled or failed."));
    });
  });
}

// Returns the finished DownloadItem when `wait` is set (its .filename is the full path).
export async function saveBlob(blob, filename, settings = {}, { wait = false, saveAs = null } = {}) {
  const folder = safeFolder(settings.downloadFolder);
  const target = folder ? `${folder}/${filename}` : filename;
  const url = URL.createObjectURL(blob);
  setTimeout(() => URL.revokeObjectURL(url), 120_000);
  const plain = !folder && !(saveAs ?? settings.saveAs) && !wait;
  if (plain || !chrome.downloads?.download) {
    // A plain save keeps the page's own download path: it honours Chrome's own
    // "ask where to save" setting and needs nothing from the downloads API.
    anchorDownload(url, target);
    return null;
  }
  try {
    const id = await chrome.downloads.download({
      url,
      filename: target,
      saveAs: saveAs ?? Boolean(settings.saveAs),
      conflictAction: "uniquify"
    });
    return wait ? await waitForDownload(id) : { id };
  } catch (error) {
    if (wait) throw error;
    anchorDownload(url, target);
    return null;
  }
}
