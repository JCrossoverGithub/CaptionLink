from __future__ import annotations

import asyncio
import json

import websockets


SERVICE_URI = "ws://127.0.0.1:8766/stream"
SAMPLE_RATE = 48000
CHUNK_DURATION_SECONDS = 0.1
CHUNK_COUNT = 10

SAMPLES_PER_CHUNK = int(
    SAMPLE_RATE * CHUNK_DURATION_SECONDS
)

PCM16_SILENCE_CHUNK = bytes(
    SAMPLES_PER_CHUNK * 2
)


async def receive_json(
    websocket,
) -> dict[str, object]:
    message = await websocket.recv()

    if not isinstance(message, str):
        raise RuntimeError(
            "Expected a JSON text message."
        )

    data = json.loads(message)
    print(data)

    return data


async def main() -> None:
    async with websockets.connect(
        SERVICE_URI
    ) as websocket:
        await receive_json(websocket)

        await websocket.send(
            json.dumps(
                {
                    "type": "start",
                    "sample_rate": SAMPLE_RATE,
                    "channels": 1,
                    "bits_per_sample": 16,
                    "maximum_speakers": 4,
                }
            )
        )

        await receive_json(websocket)

        for _ in range(CHUNK_COUNT):
            await websocket.send(
                PCM16_SILENCE_CHUNK
            )

        await receive_json(websocket)

        await websocket.send(
            json.dumps(
                {
                    "type": "stop",
                }
            )
        )

        await receive_json(websocket)


if __name__ == "__main__":
    asyncio.run(main())
