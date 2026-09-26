(function initializeCaptionLinkLatency() {
  "use strict";

  const maximumSamplesPerReport = 5000;

  function nowMilliseconds() {
    return performance.timeOrigin + performance.now();
  }

  function toFiniteNumber(value) {
    const number = Number(value);
    return Number.isFinite(number) ? number : null;
  }

  function nonNegativeDifference(end, start) {
    const endNumber = toFiniteNumber(end);
    const startNumber = toFiniteNumber(start);

    if (endNumber === null || startNumber === null) {
      return null;
    }

    return Math.max(0, endNumber - startNumber);
  }

  function decorateReceivedCaption(caption, audioTiming, receivedAt) {
    const latency = caption?.latency || {};
    const receivedAtMilliseconds =
      toFiniteNumber(receivedAt) ?? nowMilliseconds();
    const captureCompletedAtMilliseconds =
      toFiniteNumber(audioTiming?.captureCompletedAtMilliseconds);

    return {
      ...caption,
      clientLatency: {
        audioChunkSequence:
          toFiniteNumber(latency.audioChunkSequence) ??
          toFiniteNumber(audioTiming?.sequence),
        audioEndTimeMilliseconds:
          toFiniteNumber(latency.audioEndTimeMilliseconds) ??
          toFiniteNumber(audioTiming?.endTimeMilliseconds),
        captureCompletedAtMilliseconds,
        clientReceivedAtMilliseconds: receivedAtMilliseconds,
        captureToClientReceiveMilliseconds:
          nonNegativeDifference(
            receivedAtMilliseconds,
            captureCompletedAtMilliseconds),
        gatewayReceiveToResultMilliseconds:
          toFiniteNumber(
            latency.gatewayReceiveToResultMilliseconds),
        gatewayDispatchToResultMilliseconds:
          toFiniteNumber(
            latency.gatewayDispatchToResultMilliseconds),
        engineProcessingMilliseconds:
          toFiniteNumber(latency.engineProcessingMilliseconds)
      }
    };
  }

  function createDisplaySample(caption, displayedAt) {
    const latency = caption?.clientLatency;

    if (!latency) {
      return null;
    }

    const displayedAtMilliseconds =
      toFiniteNumber(displayedAt) ?? nowMilliseconds();

    return {
      sessionId: String(caption.sessionId || ""),
      captionSequence: toFiniteNumber(caption.sequence),
      segmentId: String(caption.segmentId || ""),
      isFinal: Boolean(caption.isFinal),
      audioChunkSequence:
        toFiniteNumber(latency.audioChunkSequence),
      audioEndTimeMilliseconds:
        toFiniteNumber(latency.audioEndTimeMilliseconds),
      captureToClientReceiveMilliseconds:
        toFiniteNumber(latency.captureToClientReceiveMilliseconds),
      gatewayReceiveToResultMilliseconds:
        toFiniteNumber(latency.gatewayReceiveToResultMilliseconds),
      gatewayDispatchToResultMilliseconds:
        toFiniteNumber(latency.gatewayDispatchToResultMilliseconds),
      engineProcessingMilliseconds:
        toFiniteNumber(latency.engineProcessingMilliseconds),
      clientReceiveToDisplayMilliseconds:
        nonNegativeDifference(
          displayedAtMilliseconds,
          latency.clientReceivedAtMilliseconds),
      captureToDisplayMilliseconds:
        nonNegativeDifference(
          displayedAtMilliseconds,
          latency.captureCompletedAtMilliseconds),
      displayedAtMilliseconds
    };
  }

  function percentile(values, percentileValue) {
    const sorted = values
      .map(toFiniteNumber)
      .filter((value) => value !== null && value >= 0)
      .sort((left, right) => left - right);

    if (sorted.length === 0) {
      return null;
    }

    const index = (sorted.length - 1) * percentileValue;
    const lowerIndex = Math.floor(index);
    const upperIndex = Math.ceil(index);
    const fraction = index - lowerIndex;
    const value = sorted[lowerIndex] +
      ((sorted[upperIndex] - sorted[lowerIndex]) * fraction);

    return Math.round(value * 10) / 10;
  }

  function summarizeMetric(samples, propertyName) {
    const values = samples
      .map((sample) => sample?.[propertyName])
      .filter((value) => toFiniteNumber(value) !== null);

    if (values.length === 0) {
      return null;
    }

    return {
      count: values.length,
      p50: percentile(values, 0.50),
      p95: percentile(values, 0.95),
      p99: percentile(values, 0.99)
    };
  }

  function summarizeSession(sessionId, receivedSamples, displaySamples) {
    const received = receivedSamples.slice(-maximumSamplesPerReport);
    const displayed = displaySamples.slice(-maximumSamplesPerReport);

    return {
      schemaVersion: 1,
      sessionId: String(sessionId || ""),
      generatedAtUtc: new Date().toISOString(),
      receivedCaptionCount: received.length,
      displayedCaptionCount: displayed.length,
      displayedFinalCaptionCount:
        displayed.filter((sample) => sample.isFinal).length,
      metrics: {
        captureToClientReceiveMilliseconds:
          summarizeMetric(
            received,
            "captureToClientReceiveMilliseconds"),
        gatewayReceiveToResultMilliseconds:
          summarizeMetric(
            received,
            "gatewayReceiveToResultMilliseconds"),
        gatewayDispatchToResultMilliseconds:
          summarizeMetric(
            received,
            "gatewayDispatchToResultMilliseconds"),
        engineProcessingMilliseconds:
          summarizeMetric(
            received,
            "engineProcessingMilliseconds"),
        clientReceiveToDisplayMilliseconds:
          summarizeMetric(
            displayed,
            "clientReceiveToDisplayMilliseconds"),
        captureToDisplayMilliseconds:
          summarizeMetric(
            displayed,
            "captureToDisplayMilliseconds")
      }
    };
  }

  globalThis.CaptionLinkLatency = Object.freeze({
    createDisplaySample,
    decorateReceivedCaption,
    nowMilliseconds,
    percentile,
    summarizeSession
  });
})();
