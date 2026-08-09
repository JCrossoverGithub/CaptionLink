import assert from "node:assert/strict";
import test from "node:test";

globalThis.performance = {
  timeOrigin: 1000,
  now: () => 250
};

await import("../latency.js");

const latency = globalThis.TransGoLatency;

test("decorates a caption with same-device receive latency", () => {
  const caption = latency.decorateReceivedCaption(
    {
      sessionId: "session-1",
      sequence: 8,
      latency: {
        audioChunkSequence: 14,
        audioEndTimeMilliseconds: 1500,
        gatewayReceiveToResultMilliseconds: 425.5,
        gatewayDispatchToResultMilliseconds: 420,
        engineProcessingMilliseconds: 97.25
      }
    },
    {
      sequence: 14,
      endTimeMilliseconds: 1500,
      captureCompletedAtMilliseconds: 10_000
    },
    10_650);

  assert.equal(
    caption.clientLatency.captureToClientReceiveMilliseconds,
    650);
  assert.equal(
    caption.clientLatency.gatewayReceiveToResultMilliseconds,
    425.5);
  assert.equal(
    caption.clientLatency.engineProcessingMilliseconds,
    97.25);
});

test("calculates capture-to-display without cross-device clocks", () => {
  const sample = latency.createDisplaySample(
    {
      sessionId: "session-1",
      sequence: 9,
      segmentId: "segment-1",
      isFinal: true,
      clientLatency: {
        audioChunkSequence: 14,
        audioEndTimeMilliseconds: 1500,
        captureCompletedAtMilliseconds: 10_000,
        clientReceivedAtMilliseconds: 10_650
      }
    },
    10_900);

  assert.equal(sample.captureToDisplayMilliseconds, 900);
  assert.equal(sample.clientReceiveToDisplayMilliseconds, 250);
});

test("summarizes latency distributions with interpolated percentiles", () => {
  const received = [100, 200, 300, 400].map((value) => ({
    captureToClientReceiveMilliseconds: value
  }));
  const displayed = [150, 250, 350, 450].map((value, index) => ({
    captureToDisplayMilliseconds: value,
    clientReceiveToDisplayMilliseconds: 50,
    isFinal: index === 3
  }));

  const report = latency.summarizeSession(
    "session-1",
    received,
    displayed);

  assert.equal(report.receivedCaptionCount, 4);
  assert.equal(report.displayedFinalCaptionCount, 1);
  assert.equal(
    report.metrics.captureToClientReceiveMilliseconds.p50,
    250);
  assert.equal(
    report.metrics.captureToDisplayMilliseconds.p95,
    435);
});
