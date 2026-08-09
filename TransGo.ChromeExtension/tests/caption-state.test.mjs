import assert from "node:assert/strict";
import test from "node:test";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const captions = require("../caption-state.js");

test("combines finalized text with the next interim segment", () => {
  const state = captions.createState();

  captions.applyCaption(state, {
    sessionId: "session-1",
    sequence: 1,
    segmentId: "segment-1",
    text: "Nvidia technology into their AI frameworks.",
    isFinal: true
  });

  const update = captions.applyCaption(state, {
    sessionId: "session-1",
    sequence: 2,
    segmentId: "segment-2",
    text: "And then Christian and his team",
    isFinal: false
  });

  assert.equal(
    update.text,
    "Nvidia technology into their AI frameworks. And then Christian and his team");
});

test("replaces one interim without returning to older caption text", () => {
  const state = captions.createState();

  captions.applyCaption(state, {
    sessionId: "session-1",
    sequence: 1,
    segmentId: "segment-1",
    text: "Christian and his team at",
    isFinal: false
  });

  const update = captions.applyCaption(state, {
    sessionId: "session-1",
    sequence: 2,
    segmentId: "segment-1",
    text: "Christian and his team at SAP",
    isFinal: false
  });

  assert.equal(update.text, "Christian and his team at SAP");
  assert.equal(state.finalizedSegments.length, 0);
});

test("ignores stale caption sequences", () => {
  const state = captions.createState();

  captions.applyCaption(state, {
    sessionId: "session-1",
    sequence: 4,
    segmentId: "segment-1",
    text: "The current caption",
    isFinal: false
  });

  const stale = captions.applyCaption(state, {
    sessionId: "session-1",
    sequence: 3,
    segmentId: "segment-1",
    text: "An older caption",
    isFinal: false
  });

  assert.equal(stale.accepted, false);
  assert.equal(stale.text, "The current caption");
});

test("deduplicates overlap between final and interim text", () => {
  const state = captions.createState();

  captions.applyCaption(state, {
    sessionId: "session-1",
    sequence: 1,
    segmentId: "segment-1",
    text: "Bill McDermott and his team",
    isFinal: true
  });

  const update = captions.applyCaption(state, {
    sessionId: "session-1",
    sequence: 2,
    segmentId: "segment-2",
    text: "his team at ServiceNow",
    isFinal: false
  });

  assert.equal(
    update.text,
    "Bill McDermott and his team at ServiceNow");
});

test("resets accumulated text for a new session", () => {
  const state = captions.createState();

  captions.applyCaption(state, {
    sessionId: "session-1",
    sequence: 8,
    segmentId: "segment-1",
    text: "Old session text",
    isFinal: true
  });

  const update = captions.applyCaption(state, {
    sessionId: "session-2",
    sequence: 0,
    segmentId: "segment-1",
    text: "New session text",
    isFinal: false
  });

  assert.equal(update.text, "New session text");
  assert.equal(state.lastSequence, 0);
});
