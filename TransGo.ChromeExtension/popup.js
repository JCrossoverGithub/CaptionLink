"use strict";

const gatewayUrlInput = document.querySelector("#gateway-url");
const gatewayTokenInput = document.querySelector("#gateway-token");
const startButton = document.querySelector("#start");
const stopButton = document.querySelector("#stop");
const statusElement = document.querySelector("#status");

initialize().catch(showError);

startButton.addEventListener("click", async () => {
  setBusy(true);

  try {
    const gatewayUrl = gatewayUrlInput.value.trim();
    const token = gatewayTokenInput.value;

    if (!gatewayUrl || !token.trim()) {
      throw new Error("Enter both the gateway URL and token.");
    }

    await chrome.storage.local.set({ gatewayUrl, gatewayToken: token });

    showStatus("Starting tab capture…");

    const response = await chrome.runtime.sendMessage({
      target: "background",
      type: "start",
      gatewayUrl,
      token
    });

    if (!response?.ok) {
      throw new Error(response?.error || "Unable to start captions.");
    }

    showStatus("Listening to the active tab.");
  } catch (error) {
    showError(error);
  } finally {
    setBusy(false);
  }
});

stopButton.addEventListener("click", async () => {
  setBusy(true);

  try {
    const response = await chrome.runtime.sendMessage({
      target: "background",
      type: "stop"
    });

    if (!response?.ok) {
      throw new Error(response?.error || "Unable to stop captions.");
    }

    showStatus("Captions stopped.");
  } catch (error) {
    showError(error);
  } finally {
    setBusy(false);
  }
});

async function initialize() {
  const settings = await chrome.storage.local.get([
    "gatewayUrl",
    "gatewayToken"
  ]);

  gatewayUrlInput.value = settings.gatewayUrl || "";
  gatewayTokenInput.value = settings.gatewayToken || "";

  const response = await chrome.runtime.sendMessage({
    target: "background",
    type: "get_state"
  });

  const state = response?.captureState;

  if (state?.detail) {
    showStatus(state.detail, state.status === "error");
  }
}

function setBusy(isBusy) {
  startButton.disabled = isBusy;
  stopButton.disabled = isBusy;
}

function showStatus(message, isError = false) {
  statusElement.textContent = message;
  statusElement.classList.toggle("error", isError);
}

function showError(error) {
  showStatus(
    error instanceof Error ? error.message : String(error),
    true);
}
