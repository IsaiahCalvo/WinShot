const status = document.querySelector("#status");

async function request(message) {
  status.className = "";
  status.textContent = "Starting…";
  const response = await chrome.runtime.sendMessage(message);
  if (response?.error) throw new Error(response.error);
  return response;
}

for (const button of document.querySelectorAll("[data-mode]")) {
  button.addEventListener("click", async () => {
    try {
      status.textContent = "Capture running. Keep this tab active…";
      const result = await request({ type: "WINSHOT_START_CAPTURE", mode: button.dataset.mode });
      if (result?.error) throw new Error(result.error);
      status.textContent = result?.state === "complete" ? "Capture complete." : `Capture ${result?.state || "finished"}.`;
    } catch (error) {
      status.className = "error";
      status.textContent = error.message;
    }
  });
}

for (const button of document.querySelectorAll("[data-picker]")) {
  button.addEventListener("click", async () => {
    try {
      await request({ type: "WINSHOT_START_PICKER", mode: button.dataset.picker });
      window.close();
    } catch (error) {
      status.className = "error";
      status.textContent = error.message;
    }
  });
}

document.querySelector("#cancel").addEventListener("click", async () => {
  try {
    const result = await request({ type: "WINSHOT_CANCEL_CAPTURE" });
    status.textContent = result.cancelled ? "Cancelling and restoring the page…" : "No active capture in this tab.";
  } catch (error) {
    status.className = "error";
    status.textContent = error.message;
  }
});

chrome.runtime.onMessage.addListener((message) => {
  if (message.type !== "WINSHOT_PROGRESS") return;
  status.textContent = message.message;
});
