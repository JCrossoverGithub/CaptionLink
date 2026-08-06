(function initializeTransGoOverlay() {
  "use strict";

  if (globalThis.__transGoOverlayLoaded) {
    return;
  }

  globalThis.__transGoOverlayLoaded = true;

  const host = document.createElement("div");
  host.id = "transgo-caption-host";
  const shadow = host.attachShadow({ mode: "closed" });

  const style = document.createElement("style");
  style.textContent = `
    :host {
      all: initial;
      position: fixed;
      z-index: 2147483647;
      left: 50%;
      bottom: max(5vh, 28px);
      transform: translateX(-50%);
      width: min(900px, calc(100vw - 32px));
      pointer-events: none;
      font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }

    .panel {
      display: none;
      box-sizing: border-box;
      margin: 0 auto;
      width: 100%;
      max-width: 100%;
      min-height: calc(2.7em + 20px);
      padding: 10px 16px;
      border-radius: 10px;
      color: #fff;
      background: rgba(10, 12, 16, 0.88);
      box-shadow: 0 8px 30px rgba(0, 0, 0, 0.34);
      font-size: clamp(18px, 2.2vw, 30px);
      font-weight: 600;
      line-height: 1.35;
      text-align: center;
      text-wrap: pretty;
      white-space: pre-wrap;
    }

    .panel.visible {
      display: block;
    }

    .panel.status {
      color: #d8e4ff;
      font-size: 15px;
      font-weight: 500;
    }

    .panel.error {
      color: #ffd4d4;
      border: 1px solid rgba(255, 110, 110, 0.55);
    }

    .interim {
      color: #d5d9e1;
    }
  `;

  const panel = document.createElement("div");
  panel.className = "panel";
  panel.setAttribute("role", "status");
  panel.setAttribute("aria-live", "polite");

  shadow.append(style, panel);

  const interimRenderIntervalMilliseconds = 250;
  let latestCaption = null;
  let lastSequence = -1;
  let lastRenderedText = "";
  let interimRenderTimer = null;

  function mountHost() {
    const target = document.fullscreenElement || document.documentElement;

    if (target && host.parentNode !== target) {
      target.appendChild(host);
    }
  }

  function showStatus(detail, isError = false) {
    resetCaptionState();
    panel.textContent = detail;
    panel.className = `panel visible status${isError ? " error" : ""}`;
    mountHost();
  }

  function renderCaptions() {
    interimRenderTimer = null;

    const text = getReadableCaption(latestCaption?.text || "");

    if (!text || text === lastRenderedText) {
      return;
    }

    lastRenderedText = text;
    panel.textContent = text;
    panel.className = latestCaption?.isFinal
      ? "panel visible"
      : "panel visible interim";
    mountHost();
  }

  function addCaption(caption) {
    const sequence = Number(caption.sequence);

    if (Number.isFinite(sequence)) {
      if (sequence <= lastSequence) {
        return;
      }

      lastSequence = sequence;
    }

    latestCaption = {
      text: String(caption.text || "").trim(),
      isFinal: Boolean(caption.isFinal)
    };

    if (latestCaption.isFinal) {
      if (interimRenderTimer !== null) {
        clearTimeout(interimRenderTimer);
      }

      renderCaptions();
      return;
    }

    if (interimRenderTimer === null) {
      interimRenderTimer = setTimeout(
        renderCaptions,
        interimRenderIntervalMilliseconds);
    }
  }

  function getReadableCaption(text, maximumWords = 18) {
    const trimmedText = String(text || "").trim();

    if (!trimmedText) {
      return "";
    }

    let searchIndex = trimmedText.length - 1;

    while (
      searchIndex >= 0 &&
      (/\s|[.!?]/u).test(trimmedText[searchIndex])) {
      searchIndex -= 1;
    }

    let previousSentenceBoundary = -1;

    for (let index = searchIndex; index >= 0; index -= 1) {
      if (/[.!?]/u.test(trimmedText[index])) {
        previousSentenceBoundary = index;
        break;
      }
    }

    const currentSentence = (
      previousSentenceBoundary >= 0
        ? trimmedText.slice(previousSentenceBoundary + 1)
        : trimmedText).trim();

    const words = currentSentence.split(/\s+/u);

    return words.length <= maximumWords
      ? currentSentence
      : words.slice(-maximumWords).join(" ");
  }

  function resetCaptionState() {
    if (interimRenderTimer !== null) {
      clearTimeout(interimRenderTimer);
      interimRenderTimer = null;
    }

    latestCaption = null;
    lastSequence = -1;
    lastRenderedText = "";
  }

  function clear() {
    resetCaptionState();
    panel.replaceChildren();
    panel.className = "panel";
  }

  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    switch (message?.type) {
      case "ping":
        sendResponse({ ok: true });
        break;
      case "caption":
        addCaption(message.caption || {});
        break;
      case "status":
        if (message.status === "listening") {
          clear();
        } else if (message.status !== "stopped") {
          showStatus(
            message.detail || message.status,
            message.status === "error");
        }
        break;
      case "clear":
        clear();
        break;
      default:
        return false;
    }

    return false;
  });

  document.addEventListener("fullscreenchange", mountHost);

  if (document.documentElement) {
    mountHost();
  } else {
    document.addEventListener("DOMContentLoaded", mountHost, { once: true });
  }
})();
