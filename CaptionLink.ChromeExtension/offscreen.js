"use strict";

const protocol = globalThis.CaptionLinkProtocol;
const latency = globalThis.CaptionLinkLatency;
const sessionStartTimeoutMilliseconds = 120000;

let capture = null;
let operation = Promise.resolve();

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.target !== "offscreen") {
    return false;
  }

  if (message.type === "latency_sample") {
    recordDisplayLatency(message);
    sendResponse({ ok: true });
    return false;
  }

  operation = operation
    .catch(() => undefined)
    .then(() => handleMessage(message));

  operation
    .then((value) => sendResponse({ ok: true, ...value }))
    .catch((error) => sendResponse({
      ok: false,
      error: error instanceof Error ? error.message : String(error)
    }));

  return true;
});

async function handleMessage(message) {
  switch (message.type) {
    case "start":
      await startCapture(message);
      return {};
    case "stop":
      await stopCapture(message.reason || "client_stopped");
      return {};
    default:
      throw new Error(`Unknown offscreen message: ${message.type}`);
  }
}

async function startCapture({ tabId, streamId, gatewayUrl, token }) {
  await stopCapture("replaced");

  const session = {
    tabId,
    stream: null,
    audioContext: null,
    source: null,
    worklet: null,
    silentGain: null,
    socket: null,
    sessionId: null,
    requestId: crypto.randomUUID().replaceAll("-", ""),
    sequence: 0,
    firstSentChunkTimestampMilliseconds: null,
    audioTimings: new Map(),
    receivedLatencySamples: [],
    displayLatencySamples: [],
    startError: null,
    stopping: false,
    sessionEnded: null,
    resolveSessionEnded: null
  };

  session.sessionEnded = new Promise((resolve) => {
    session.resolveSessionEnded = resolve;
  });

  capture = session;

  try {
    await notifyStatus(session, "connecting", "Capturing tab audio…");

    session.stream = await navigator.mediaDevices.getUserMedia({
      audio: {
        mandatory: {
          chromeMediaSource: "tab",
          chromeMediaSourceId: streamId
        }
      },
      video: false
    });

    const [audioTrack] = session.stream.getAudioTracks();

    if (!audioTrack) {
      throw new Error("The selected tab did not provide an audio track.");
    }

    audioTrack.addEventListener("ended", () => {
      if (!session.stopping && capture === session) {
        operation = operation
          .catch(() => undefined)
          .then(() => stopCapture("capture_ended"));
      }
    }, { once: true });

    session.audioContext = new AudioContext();
    await session.audioContext.audioWorklet.addModule("audio-processor.js");

    session.source =
      session.audioContext.createMediaStreamSource(session.stream);
    session.worklet = new AudioWorkletNode(
      session.audioContext,
      "captionlink-pcm-processor");
    session.silentGain = session.audioContext.createGain();
    session.silentGain.gain.value = 0;

    session.source.connect(session.audioContext.destination);
    session.source.connect(session.worklet);
    session.worklet.connect(session.silentGain);
    session.silentGain.connect(session.audioContext.destination);

    session.worklet.port.onmessage = (event) => {
      sendAudioChunk(session, event.data);
    };

    if (session.audioContext.state === "suspended") {
      await session.audioContext.resume();
    }

    const websocketUrl = protocol.normalizeGatewayUrl(gatewayUrl);
    const tokenProtocol = protocol.encodeTokenSubprotocol(token);

    session.socket = new WebSocket(websocketUrl, [
      protocol.BROWSER_SUBPROTOCOL,
      tokenProtocol
    ]);
    session.socket.binaryType = "arraybuffer";

    session.socket.addEventListener("message", (event) => {
      handleGatewayMessage(session, event.data);
    });

    session.socket.addEventListener("close", (event) => {
      if (!session.stopping && capture === session) {
        const detail = event.reason
          ? `Gateway disconnected: ${event.reason}`
          : "The GPU gateway disconnected.";
        if (!session.sessionId) {
          session.startError = new Error(detail);
        }
        notifyStatus(session, "error", detail);
      }
    });

    session.socket.addEventListener("error", () => {
      if (!session.stopping && capture === session) {
        if (!session.sessionId) {
          session.startError = new Error(
            "Could not connect to the GPU gateway. Check Tailscale, the URL, and the token.");
        }
        notifyStatus(
          session,
          "error",
          "Could not connect to the GPU gateway. Check Tailscale, the URL, and the token.");
      }
    });

    await waitForWebSocketOpen(session.socket, 10000);

    if (session.socket.protocol !== protocol.BROWSER_SUBPROTOCOL) {
      throw new Error("The gateway did not accept the CaptionLink browser protocol.");
    }

    session.socket.send(JSON.stringify(
      protocol.createStartSessionMessage(session.requestId)));

    await notifyStatus(
      session,
      "connecting",
      "Starting Parakeet on the GPU…");

    await waitForSessionStarted(
      session,
      sessionStartTimeoutMilliseconds);
  } catch (error) {
    await notifyStatus(
      session,
      "error",
      error instanceof Error ? error.message : String(error));
    await disposeSession(session);
    throw error;
  }
}

function sendAudioChunk(session, chunk) {
  if (capture !== session ||
      session.stopping ||
      !session.sessionId ||
      session.socket?.readyState !== WebSocket.OPEN) {
    return;
  }

  try {
    const sequence = session.sequence;
    const sourceTimestampMilliseconds =
      Number(chunk.timestampMilliseconds);

    if (!Number.isFinite(sourceTimestampMilliseconds) ||
        sourceTimestampMilliseconds < 0) {
      throw new Error("The audio worklet returned an invalid timestamp.");
    }

    if (session.firstSentChunkTimestampMilliseconds === null) {
      session.firstSentChunkTimestampMilliseconds =
        sourceTimestampMilliseconds;
    }

    const sessionTimestampMilliseconds = Math.max(
      0,
      sourceTimestampMilliseconds -
        session.firstSentChunkTimestampMilliseconds);
    const durationMilliseconds =
      chunk.pcm.byteLength * 1000 /
      (16000 * 2);

    session.audioTimings.set(sequence, {
      sequence,
      startTimeMilliseconds: sessionTimestampMilliseconds,
      endTimeMilliseconds:
        sessionTimestampMilliseconds + durationMilliseconds,
      captureCompletedAtMilliseconds:
        resolveCaptureCompletedAt(session, chunk)
    });

    if (session.audioTimings.size > 1200) {
      const oldestSequence = session.audioTimings.keys().next().value;
      session.audioTimings.delete(oldestSequence);
    }

    const packet = protocol.createAudioPacket(
      sequence,
      sessionTimestampMilliseconds,
      chunk.pcm);

    session.sequence += 1;
    session.socket.send(packet);
  } catch (error) {
    notifyStatus(
      session,
      "error",
      error instanceof Error ? error.message : String(error));
  }
}

function resolveCaptureCompletedAt(session, chunk) {
  const now = latency.nowMilliseconds();
  const workletContextTime =
    Number(chunk.audioContextTimeMilliseconds);
  const offscreenContextTime =
    Number(session.audioContext?.currentTime) * 1000;

  if (!Number.isFinite(workletContextTime) ||
      !Number.isFinite(offscreenContextTime)) {
    return now;
  }

  const deliveryDelay = Math.max(
    0,
    offscreenContextTime - workletContextTime);

  return now - deliveryDelay;
}

function handleGatewayMessage(session, data) {
  if (typeof data !== "string") {
    notifyStatus(session, "error", "The gateway sent an invalid message.");
    return;
  }

  let message;

  try {
    message = JSON.parse(data);
  } catch {
    notifyStatus(session, "error", "The gateway sent invalid JSON.");
    return;
  }

  switch (message.type) {
    case "session_started":
      if (message.requestId !== session.requestId || !message.sessionId) {
        notifyStatus(session, "error", "The gateway returned an invalid session.");
        return;
      }

      session.sessionId = message.sessionId;
      notifyStatus(session, "listening", "Listening to this tab…");
      break;
    case "caption":
      if (message.sessionId === session.sessionId) {
        const receivedAtMilliseconds =
          latency.nowMilliseconds();
        const audioTiming = findAudioTiming(session, message);
        const caption = latency.decorateReceivedCaption(
          {
            ...message,
            tabId: session.tabId
          },
          audioTiming,
          receivedAtMilliseconds);

        pushBounded(session.receivedLatencySamples, {
          ...caption.clientLatency,
          isFinal: Boolean(caption.isFinal)
        });

        sendToBackground({
          type: "caption",
          tabId: session.tabId,
          caption
        });
      }
      break;
    case "session_ended":
      if (message.sessionId === session.sessionId) {
        session.resolveSessionEnded?.(message);
      }
      break;
    case "error":
      {
        const detail =
          `Gateway error [${message.code || "unknown"}]: ` +
          `${message.message || "Unknown error"}`;

        if (message.isFatal && !session.sessionId) {
          session.startError = new Error(detail);
        }

        notifyStatus(session, "error", detail);
      }
      if (message.isFatal) {
        session.resolveSessionEnded?.(message);
      }
      break;
    default:
      notifyStatus(
        session,
        "error",
        `Unexpected gateway message: ${message.type || "unknown"}`);
  }
}

function findAudioTiming(session, caption) {
  const sequence = Number(caption.latency?.audioChunkSequence);

  if (Number.isFinite(sequence) && session.audioTimings.has(sequence)) {
    return session.audioTimings.get(sequence);
  }

  const targetEnd = Number(caption.endTimeMilliseconds);
  let nearest = null;

  for (const timing of session.audioTimings.values()) {
    if (!Number.isFinite(targetEnd)) {
      nearest = timing;
      continue;
    }

    if (timing.endTimeMilliseconds <= targetEnd) {
      nearest = timing;
    }
  }

  return nearest;
}

function recordDisplayLatency(message) {
  const session = capture;

  if (!session ||
      session.tabId !== message.tabId ||
      message.sample?.sessionId !== session.sessionId) {
    return;
  }

  pushBounded(session.displayLatencySamples, message.sample);
}

function pushBounded(samples, sample, maximumCount = 5000) {
  samples.push(sample);

  if (samples.length > maximumCount) {
    samples.splice(0, samples.length - maximumCount);
  }
}

async function waitForSessionStarted(session, timeoutMilliseconds) {
  const deadline = Date.now() + timeoutMilliseconds;

  while (!session.sessionId) {
    if (capture !== session || session.stopping) {
      throw new Error("Tab capture stopped while the gateway was starting.");
    }

    if (session.startError) {
      throw session.startError;
    }

    if (Date.now() >= deadline) {
      throw new Error("Timed out while starting the GPU transcription session.");
    }

    await new Promise((resolve) => setTimeout(resolve, 50));
  }
}

function waitForWebSocketOpen(socket, timeoutMilliseconds) {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      cleanup();
      reject(new Error("Timed out while connecting to the GPU gateway."));
    }, timeoutMilliseconds);

    const onOpen = () => {
      cleanup();
      resolve();
    };

    const onClose = (event) => {
      cleanup();
      reject(new Error(
        event.reason ||
        "The GPU gateway rejected the WebSocket connection."));
    };

    const onError = () => {
      cleanup();
      reject(new Error("Could not open the GPU gateway WebSocket."));
    };

    function cleanup() {
      clearTimeout(timeout);
      socket.removeEventListener("open", onOpen);
      socket.removeEventListener("close", onClose);
      socket.removeEventListener("error", onError);
    }

    socket.addEventListener("open", onOpen, { once: true });
    socket.addEventListener("close", onClose, { once: true });
    socket.addEventListener("error", onError, { once: true });
  });
}

async function stopCapture(reason) {
  const session = capture;

  if (!session) {
    return;
  }

  session.stopping = true;

  if (session.worklet) {
    session.worklet.port.onmessage = null;
  }

  if (session.socket?.readyState === WebSocket.OPEN && session.sessionId) {
    session.socket.send(JSON.stringify({
      type: "stop_session",
      sessionId: session.sessionId,
      reason
    }));

    await Promise.race([
      session.sessionEnded,
      new Promise((resolve) => setTimeout(resolve, 5000))
    ]);
  }

  await new Promise((resolve) => setTimeout(resolve, 300));

  await publishLatencyReport(session);

  await disposeSession(session);
  await notifyStatus(session, "stopped", "Captions stopped.");
}

async function publishLatencyReport(session) {
  if (!session.sessionId ||
      (session.receivedLatencySamples.length === 0 &&
       session.displayLatencySamples.length === 0)) {
    return;
  }

  const report = latency.summarizeSession(
    session.sessionId,
    session.receivedLatencySamples,
    session.displayLatencySamples);

  console.info("CaptionLink latency report", report);

  await sendToBackground({
    type: "latency_report",
    tabId: session.tabId,
    report
  }).catch(() => undefined);
}

async function disposeSession(session) {
  session.stopping = true;

  try {
    session.source?.disconnect();
    session.worklet?.disconnect();
    session.silentGain?.disconnect();
  } catch {
    // The audio graph may already be disconnected.
  }

  for (const track of session.stream?.getTracks() || []) {
    track.stop();
  }

  if (session.audioContext && session.audioContext.state !== "closed") {
    await session.audioContext.close().catch(() => undefined);
  }

  if (session.socket &&
      session.socket.readyState !== WebSocket.CLOSED &&
      session.socket.readyState !== WebSocket.CLOSING) {
    session.socket.close(1000, "CaptionLink capture stopped.");
  }

  if (capture === session) {
    capture = null;
  }
}

async function notifyStatus(session, status, detail) {
  await sendToBackground({
    type: "capture_status",
    tabId: session.tabId,
    status,
    detail
  }).catch(() => undefined);
}

function sendToBackground(message) {
  return chrome.runtime.sendMessage({
    target: "background",
    ...message
  });
}
