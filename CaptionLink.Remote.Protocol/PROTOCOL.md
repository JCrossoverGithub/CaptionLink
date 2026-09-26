# CaptionLink Remote-Session Protocol v1

## Purpose

This protocol allows a CaptionLink client to stream captured audio to a remote
GPU gateway and receive real-time transcription results.

## Transport

- WebSocket endpoint: `/v1/transcription`
- Control messages: UTF-8 JSON WebSocket text messages
- Audio chunks: WebSocket binary messages
- Authentication: `Authorization: Bearer <token>` during the WebSocket handshake
- One transcription session per WebSocket connection
- Protocol version: `1`

## Required audio format

- Encoding: `pcm_s16le`
- Sample rate: 16,000 Hz
- Channels: 1
- Bits per sample: 16
- Byte order: little-endian
- Recommended chunk duration: 100 milliseconds
- Maximum chunk payload: 65,536 bytes

The client must convert captured audio into this format before transmitting it.

## Session lifecycle

1. Client opens an authenticated WebSocket connection.
2. Client sends `start_session`.
3. Gateway validates the request and returns `session_started`.
4. Client sends ordered binary audio chunks.
5. Gateway returns `caption` messages.
6. Client sends `stop_session`.
7. Gateway flushes remaining audio and returns `session_ended`.
8. Gateway closes the WebSocket normally.

Audio must not be sent before `session_started`.

## Caption timing

Caption messages may include an additive `latency` object:

```json
{
  "type": "caption",
  "sessionId": "4d4a...",
  "sequence": 7,
  "segmentId": "parakeet-000002",
  "text": "Latency is measured.",
  "isFinal": false,
  "startTimeMilliseconds": 1000,
  "endTimeMilliseconds": 1500,
  "emittedAtUtc": "2026-08-09T15:00:00Z",
  "latency": {
    "audioChunkSequence": 14,
    "audioEndTimeMilliseconds": 1500,
    "gatewayReceiveToResultMilliseconds": 425.5,
    "gatewayDispatchToResultMilliseconds": 420.0,
    "engineProcessingMilliseconds": 97.25
  }
}
```

All latency values are durations measured with monotonic clocks on one device
or process. `emittedAtUtc` is diagnostic metadata and must not be subtracted
from a client timestamp to calculate latency unless the clocks have been
independently synchronized.

## Control messages

### `start_session`

Sent by the client as its first WebSocket message.

```json
{
  "protocolVersion": 1,
  "requestId": "request-001",
  "client": {
    "name": "CaptionLink Test Client",
    "version": "1.0.0",
    "platform": "Windows"
  },
  "audio": {
    "encoding": "pcm_s16le",
    "sampleRateHz": 16000,
    "channels": 1,
    "bitsPerSample": 16
  },
  "options": {
    "language": "en-US",
    "enableInterimResults": true
  },
  "type": "start_session"
}
