(function initializeCaptionLinkCaptionState(root, factory) {
  "use strict";

  const api = factory();

  if (typeof module === "object" && module.exports) {
    module.exports = api;
  }

  root.CaptionLinkCaptionState = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function createApi() {
  "use strict";

  function createState() {
    return {
      sessionId: "",
      lastSequence: -1,
      finalizedSegments: [],
      interimSegment: null
    };
  }

  function applyCaption(state, caption) {
    const next = state || createState();
    const sessionId = String(caption?.sessionId || "");
    const sequence = Number(caption?.sequence);
    const segmentId = String(caption?.segmentId || "");
    const text = normalizeText(caption?.text);
    const isFinal = Boolean(caption?.isFinal);

    if (sessionId && next.sessionId && sessionId !== next.sessionId) {
      resetState(next);
    }

    if (sessionId) {
      next.sessionId = sessionId;
    }

    if (Number.isFinite(sequence)) {
      if (sequence <= next.lastSequence) {
        return { accepted: false, text: composeText(next), isFinal };
      }

      next.lastSequence = sequence;
    }

    if (!text) {
      return { accepted: false, text: composeText(next), isFinal };
    }

    if (isFinal) {
      const existingIndex = next.finalizedSegments.findIndex(
        (segment) => segment.id === segmentId);

      const segment = { id: segmentId, text };

      if (existingIndex >= 0) {
        next.finalizedSegments[existingIndex] = segment;
      } else {
        next.finalizedSegments.push(segment);
      }

      if (next.finalizedSegments.length > 12) {
        next.finalizedSegments.splice(
          0,
          next.finalizedSegments.length - 12);
      }

      if (next.interimSegment?.id === segmentId) {
        next.interimSegment = null;
      }
    } else {
      const segmentAlreadyFinal = next.finalizedSegments.some(
        (segment) => segment.id === segmentId);

      if (!segmentAlreadyFinal) {
        next.interimSegment = { id: segmentId, text };
      }
    }

    return {
      accepted: true,
      text: composeText(next),
      isFinal
    };
  }

  function composeText(state) {
    const parts = state.finalizedSegments.map((segment) => segment.text);

    if (state.interimSegment?.text) {
      parts.push(state.interimSegment.text);
    }

    let combined = "";

    for (const part of parts) {
      combined = appendWithoutRepeatedWords(combined, part);
    }

    return combined;
  }

  function appendWithoutRepeatedWords(existingText, incomingText) {
    const existing = normalizeText(existingText);
    const incoming = normalizeText(incomingText);

    if (!existing) {
      return incoming;
    }

    if (!incoming) {
      return existing;
    }

    const existingWords = existing.split(" ");
    const incomingWords = incoming.split(" ");
    const maximumOverlap = Math.min(
      existingWords.length,
      incomingWords.length);

    let overlap = 0;

    for (let length = maximumOverlap; length >= 1; length -= 1) {
      const existingSuffix = existingWords
        .slice(existingWords.length - length)
        .join(" ")
        .toLocaleLowerCase();
      const incomingPrefix = incomingWords
        .slice(0, length)
        .join(" ")
        .toLocaleLowerCase();

      if (existingSuffix === incomingPrefix) {
        overlap = length;
        break;
      }
    }

    const remainder = incomingWords.slice(overlap).join(" ");
    return remainder ? `${existing} ${remainder}` : existing;
  }

  function normalizeText(value) {
    return String(value || "").trim().replace(/\s+/gu, " ");
  }

  function resetState(state) {
    state.sessionId = "";
    state.lastSequence = -1;
    state.finalizedSegments = [];
    state.interimSegment = null;
  }

  return {
    applyCaption,
    appendWithoutRepeatedWords,
    composeText,
    createState,
    resetState
  };
});
