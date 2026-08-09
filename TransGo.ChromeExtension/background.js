"use strict";

const OFFSCREEN_DOCUMENT = "offscreen.html";

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.target !== "background") {
    return false;
  }

  handleMessage(message, sender)
    .then((value) => sendResponse({ ok: true, ...value }))
    .catch((error) => sendResponse({
      ok: false,
      error: error instanceof Error ? error.message : String(error)
    }));

  return true;
});

chrome.tabs.onRemoved.addListener(async (tabId) => {
  const state = await chrome.storage.session.get("captureState");

  if (state.captureState?.tabId === tabId) {
    await sendToOffscreen({ type: "stop", reason: "tab_closed" })
      .catch(() => undefined);
    await chrome.storage.session.remove("captureState");
  }
});

async function handleMessage(message) {
  switch (message.type) {
    case "start":
      return startCapture(message);
    case "stop":
      return stopCapture("user_stopped");
    case "get_state":
      return chrome.storage.session.get("captureState");
    case "caption":
      await forwardToTab(message.tabId, {
        type: "caption",
        caption: message.caption
      });
      return {};
    case "capture_status":
      await updateCaptureState(message);
      await forwardToTab(message.tabId, {
        type: "status",
        status: message.status,
        detail: message.detail
      });
      return {};
    case "latency_report":
      await chrome.storage.local.set({
        lastLatencyReport: message.report
      });
      return {};
    default:
      throw new Error(`Unknown background message: ${message.type}`);
  }
}

async function startCapture(message) {
  const gatewayUrl = String(message.gatewayUrl || "").trim();
  const token = String(message.token || "");

  if (!gatewayUrl || !token.trim()) {
    throw new Error("Enter both the gateway URL and token.");
  }

  const [tab] = await chrome.tabs.query({
    active: true,
    currentWindow: true
  });

  if (!tab?.id || !/^https?:/i.test(tab.url || "")) {
    throw new Error("Open an HTTP or HTTPS page before starting captions.");
  }

  const previous = await chrome.storage.session.get("captureState");
  const previousState = previous.captureState;

  if (previousState?.tabId === tab.id &&
      (previousState.status === "connecting" ||
       previousState.status === "listening")) {
    return { tabId: tab.id, alreadyRunning: true };
  }

  await ensureContentScript(tab.id);
  await ensureOffscreenDocument();

  if (previousState?.status && previousState.status !== "stopped") {
    await stopCapture("replaced");
  }

  const streamId = await chrome.tabCapture.getMediaStreamId({
    targetTabId: tab.id
  });

  await chrome.storage.session.set({
    captureState: {
      tabId: tab.id,
      status: "connecting",
      detail: "Connecting to the GPU gateway…"
    }
  });

  await forwardToTab(tab.id, {
    type: "status",
    status: "connecting",
    detail: "Connecting to the GPU gateway…"
  });

  const response = await sendToOffscreen({
    type: "start",
    tabId: tab.id,
    streamId,
    gatewayUrl,
    token
  });

  if (!response?.ok) {
    throw new Error(response?.error || "The offscreen capture did not start.");
  }

  return { tabId: tab.id };
}

async function stopCapture(reason) {
  const state = await chrome.storage.session.get("captureState");
  const tabId = state.captureState?.tabId;

  const response = await sendToOffscreen({
    type: "stop",
    reason
  }).catch((error) => ({ ok: false, error: error.message }));

  await chrome.storage.session.set({
    captureState: {
      tabId,
      status: "stopped",
      detail: "Captions stopped."
    }
  });

  if (tabId) {
    await forwardToTab(tabId, { type: "clear" });
  }

  if (!response?.ok) {
    throw new Error(response?.error || "Unable to stop tab capture cleanly.");
  }

  return {};
}

async function updateCaptureState(message) {
  await chrome.storage.session.set({
    captureState: {
      tabId: message.tabId,
      status: message.status,
      detail: message.detail || ""
    }
  });
}

async function ensureOffscreenDocument() {
  if (await chrome.offscreen.hasDocument()) {
    return;
  }

  await chrome.offscreen.createDocument({
    url: OFFSCREEN_DOCUMENT,
    reasons: ["USER_MEDIA"],
    justification:
      "Capture the user-selected tab audio for live transcription."
  });
}

async function ensureContentScript(tabId) {
  try {
    await chrome.tabs.sendMessage(tabId, { type: "ping" });
  } catch {
    await chrome.scripting.executeScript({
      target: { tabId },
      files: ["latency.js", "caption-state.js", "content.js"]
    });
  }
}

async function sendToOffscreen(message) {
  return chrome.runtime.sendMessage({
    target: "offscreen",
    ...message
  });
}

async function forwardToTab(tabId, message) {
  if (!tabId) {
    return;
  }

  try {
    await chrome.tabs.sendMessage(tabId, message);
  } catch {
    // The tab may be navigating or already closed.
  }
}
