(function initializeCaptionLinkProtocol(globalScope) {
  "use strict";

  const CURRENT_VERSION = 1;
  const WEBSOCKET_PATH = "/v1/transcription";
  const SAMPLE_RATE_HZ = 16000;
  const AUDIO_CHUNK_MILLISECONDS = 100;
  const AUDIO_CHUNK_SAMPLES =
    SAMPLE_RATE_HZ * AUDIO_CHUNK_MILLISECONDS / 1000;
  const AUDIO_HEADER_BYTES = 24;
  const BROWSER_SUBPROTOCOL = "captionlink-v1";
  const TOKEN_SUBPROTOCOL_PREFIX = "captionlink-token.";

  function normalizeGatewayUrl(value) {
    const trimmed = String(value || "").trim();

    if (!trimmed) {
      throw new Error("Enter the CaptionLink gateway URL.");
    }

    const url = new URL(trimmed);

    switch (url.protocol) {
      case "https:":
        url.protocol = "wss:";
        break;
      case "http:":
        url.protocol = "ws:";
        break;
      case "wss:":
      case "ws:":
        break;
      default:
        throw new Error("The gateway URL must use HTTPS, HTTP, WSS, or WS.");
    }

    if (url.username || url.password) {
      throw new Error("Do not put credentials in the gateway URL.");
    }

    url.search = "";
    url.hash = "";

    const path = url.pathname.replace(/\/+$/, "");

    if (!path) {
      url.pathname = WEBSOCKET_PATH;
    } else if (!path.endsWith(WEBSOCKET_PATH)) {
      url.pathname = `${path}${WEBSOCKET_PATH}`;
    } else {
      url.pathname = path;
    }

    return url.toString();
  }

  function encodeTokenSubprotocol(token) {
    const value = String(token || "");

    if (!value.trim()) {
      throw new Error("Enter the CaptionLink gateway token.");
    }

    const bytes = new TextEncoder().encode(value);

    if (bytes.length > 4096) {
      throw new Error("The gateway token is too long.");
    }

    let binary = "";

    for (const byte of bytes) {
      binary += String.fromCharCode(byte);
    }

    const base64Url = btoa(binary)
      .replace(/\+/g, "-")
      .replace(/\//g, "_")
      .replace(/=+$/, "");

    return `${TOKEN_SUBPROTOCOL_PREFIX}${base64Url}`;
  }

  function createStartSessionMessage(requestId) {
    return {
      type: "start_session",
      protocolVersion: CURRENT_VERSION,
      requestId,
      client: {
        name: "CaptionLink Chrome Extension",
        version: typeof chrome.runtime.getManifest === "function"
          ? chrome.runtime.getManifest().version
          : "0.1.0",
        platform: navigator.userAgent
      },
      audio: {
        encoding: "pcm_s16le",
        sampleRateHz: SAMPLE_RATE_HZ,
        channels: 1,
        bitsPerSample: 16
      },
      options: {
        language: "en-US",
        enableInterimResults: true
      }
    };
  }

  function createAudioPacket(sequence, timestampMilliseconds, pcmBuffer) {
    const pcm = new Uint8Array(pcmBuffer);
    const packet = new ArrayBuffer(AUDIO_HEADER_BYTES + pcm.byteLength);
    const view = new DataView(packet);

    view.setUint8(0, "T".charCodeAt(0));
    view.setUint8(1, "G".charCodeAt(0));
    view.setUint8(2, "A".charCodeAt(0));
    view.setUint8(3, "C".charCodeAt(0));
    view.setUint8(4, CURRENT_VERSION);
    view.setUint8(5, 0);
    view.setUint16(6, AUDIO_HEADER_BYTES, true);
    view.setBigInt64(8, BigInt(sequence), true);
    view.setBigInt64(16, BigInt(timestampMilliseconds), true);
    new Uint8Array(packet, AUDIO_HEADER_BYTES).set(pcm);

    return packet;
  }

  globalScope.CaptionLinkProtocol = Object.freeze({
    CURRENT_VERSION,
    WEBSOCKET_PATH,
    SAMPLE_RATE_HZ,
    AUDIO_CHUNK_MILLISECONDS,
    AUDIO_CHUNK_SAMPLES,
    AUDIO_HEADER_BYTES,
    BROWSER_SUBPROTOCOL,
    normalizeGatewayUrl,
    encodeTokenSubprotocol,
    createStartSessionMessage,
    createAudioPacket
  });
})(globalThis);
