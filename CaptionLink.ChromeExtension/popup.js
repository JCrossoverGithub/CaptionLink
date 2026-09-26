"use strict";

const gatewayUrlInput = document.querySelector("#gateway-url");
const gatewayTokenInput = document.querySelector("#gateway-token");
const startButton = document.querySelector("#start");
const stopButton = document.querySelector("#stop");
const statusElement = document.querySelector("#status");
const latencySection = document.querySelector("#latency");
const latencySummaryElement = document.querySelector("#latency-summary");

initialize().catch(showError);

gatewayUrlInput.addEventListener("input", () => {
  void chrome.storage.local.set({
    gatewayUrl: gatewayUrlInput.value
  });
});

gatewayTokenInput.addEventListener("input", () => {
  void chrome.storage.local.set({
    gatewayToken: gatewayTokenInput.value
  });
});

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
    await loadLatencyReport();
  } catch (error) {
    showError(error);
  } finally {
    setBusy(false);
  }
});

async function initialize() {
  const settings = await chrome.storage.local.get([
    "gatewayUrl",
    "gatewayToken",
    "lastLatencyReport"
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

  showLatencyReport(settings.lastLatencyReport);
}

async function loadLatencyReport() {
  const { lastLatencyReport } =
    await chrome.storage.local.get("lastLatencyReport");

  showLatencyReport(lastLatencyReport);
}

function showLatencyReport(report) {
  const displayMetric =
    report?.metrics?.captureToDisplayMilliseconds;

  if (!displayMetric || !Number.isFinite(displayMetric.p50)) {
    latencySection.hidden = true;
    return;
  }

  latencySummaryElement.textContent =
    `P50 ${Math.round(displayMetric.p50)} ms · ` +
    `P95 ${Math.round(displayMetric.p95)} ms · ` +
    `${report.displayedCaptionCount} displayed captions`;
  latencySection.hidden = false;
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
