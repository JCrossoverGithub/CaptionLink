from __future__ import annotations

import asyncio
import json

import websockets


SERVICE_URI = "ws://127.0.0.1:8766/stream"
SAMPLE_RATE = 48_000
CHUNK_DURATION_SECONDS = 0.1
CHUNK_COUNT = 20

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
        SERVICE_URI,
        max_size=None,
    ) as websocket:
        connected = await receive_json(
            websocket
        )

        if connected.get("type") != "connected":
            raise RuntimeError(
                "The service did not send connected."
            )

        await websocket.send(
            json.dumps(
                {
                    "type": "start",
                    "sample_rate": SAMPLE_RATE,
                    "channels": 1,
                    "bits_per_sample": 16,
                    "maximum_speakers": 4,
                    "publish_speaker_probabilities": True,
                }
            )
        )

        started = await receive_json(
            websocket
        )

        if started.get("type") != "started":
            raise RuntimeError(
                "The service did not start."
            )

        for _ in range(CHUNK_COUNT):
            await websocket.send(
                PCM16_SILENCE_CHUNK
            )

        await websocket.send(
            json.dumps(
                {
                    "type": "stop",
                }
            )
        )

        probability_messages = []
        stopped = None

        while stopped is None:
            message = await receive_json(
                websocket
            )

            message_type = message.get(
                "type"
            )

            if (
                message_type
                == "speaker_probabilities"
            ):
                probability_messages.append(
                    message
                )
            elif message_type == "stopped":
                stopped = message
            elif message_type == "error":
                raise RuntimeError(
                    str(
                        message.get(
                            "message",
                            "Unknown service error.",
                        )
                    )
                )

        start_indices = [
            int(
                message["start_frame_index"]
            )
            for message in probability_messages
        ]

        frame_counts = [
            int(
                message["frame_count"]
            )
            for message in probability_messages
        ]

        total_frames = sum(
            frame_counts
        )

        print()
        print("Validation")
        print("----------")
        print(
            "Prediction message count:",
            len(probability_messages),
        )
        print(
            "Start frame indices:",
            start_indices,
        )
        print(
            "Frame counts:",
            frame_counts,
        )
        print(
            "Total prediction frames:",
            total_frames,
        )

        if start_indices != [
            0,
            6,
            12,
            18,
            24,
        ]:
            raise RuntimeError(
                "Prediction frame indices were not continuous."
            )

        if frame_counts != [
            6,
            6,
            6,
            6,
            1,
        ]:
            raise RuntimeError(
                "Unexpected prediction-frame counts."
            )

        if total_frames != 25:
            raise RuntimeError(
                "Expected 25 prediction frames."
            )

        if (
            stopped.get(
                "model_windows_processed"
            )
            != 5
        ):
            raise RuntimeError(
                "Expected five model windows."
            )

        if (
            stopped.get(
                "prediction_frames"
            )
            != 25
        ):
            raise RuntimeError(
                "Stopped summary reported the wrong frame count."
            )

        if (
            stopped.get(
                "model_audio_duration_seconds"
            )
            != 2.0
        ):
            raise RuntimeError(
                "Expected two seconds of model coverage."
            )

        print()
        print(
            "Sortformer WebSocket inference passed."
        )


if __name__ == "__main__":
    asyncio.run(main())
