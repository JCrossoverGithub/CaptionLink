import assert from "node:assert/strict";
import test from "node:test";

globalThis.chrome = {
  runtime: {
    getManifest: () => ({ version: "0.1.0" })
  }
};
Object.defineProperty(globalThis, "navigator", {
  configurable: true,
  value: { userAgent: "CaptionLink protocol test" }
});

await import("../protocol.js");

const protocol = globalThis.CaptionLinkProtocol;

test("normalizes the Tailscale HTTPS URL", () => {
  assert.equal(
    protocol.normalizeGatewayUrl("https://gpu-gateway.example.ts.net/"),
    "wss://gpu-gateway.example.ts.net/v1/transcription");
});

test("does not duplicate the WebSocket path", () => {
  assert.equal(
    protocol.normalizeGatewayUrl(
      "wss://gpu-gateway.example.ts.net/v1/transcription"),
    "wss://gpu-gateway.example.ts.net/v1/transcription");
});

test("encodes the browser token as Base64URL", () => {
  assert.equal(
    protocol.encodeTokenSubprotocol("test token"),
    "captionlink-token.dGVzdCB0b2tlbg");
});

test("creates the exact 24-byte TGAC header", () => {
  const pcm = Uint8Array.from([0x34, 0x12, 0xcc, 0xff]).buffer;
  const packet = protocol.createAudioPacket(7, 250, pcm);
  const bytes = new Uint8Array(packet);
  const view = new DataView(packet);

  assert.equal(packet.byteLength, 28);
  assert.deepEqual(Array.from(bytes.slice(0, 4)), [84, 71, 65, 67]);
  assert.equal(view.getUint8(4), 1);
  assert.equal(view.getUint8(5), 0);
  assert.equal(view.getUint16(6, true), 24);
  assert.equal(view.getBigInt64(8, true), 7n);
  assert.equal(view.getBigInt64(16, true), 250n);
  assert.deepEqual(Array.from(bytes.slice(24)), [0x34, 0x12, 0xcc, 0xff]);
});

test("creates a compatible start_session message", () => {
  const message = protocol.createStartSessionMessage("request-1");

  assert.equal(message.type, "start_session");
  assert.equal(message.protocolVersion, 1);
  assert.equal(message.requestId, "request-1");
  assert.deepEqual(message.audio, {
    encoding: "pcm_s16le",
    sampleRateHz: 16000,
    channels: 1,
    bitsPerSample: 16
  });
  assert.equal(message.options.enableInterimResults, true);
});
