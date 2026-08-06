from __future__ import annotations

import asyncio
import json
import wave

import websockets


SERVICE_URI = "ws://127.0.0.1:8766/stream"

AUDIO_PATH = (
    "/mnt/c/Users/thede/Documents/TransGo-Test-Data/"
    "AMI/ES2013a.test-300-420s.wav"
)

TEST_DURATION_SECONDS = 30
CHUNK_DURATION_SECONDS = 0.1


async def receive_messages(
    websocket,
    activity_messages,
    stopped_holder,
) -> None:
    while True:
        raw_message = await websocket.recv()

        if not isinstance(
            raw_message,
            str,
        ):
            continue

        message = json.loads(
            raw_message
        )

        message_type = message.get(
            "type"
        )

        if message_type == "speaker_activity":
            activity_messages.append(
                message
            )
        elif message_type == "stopped":
            stopped_holder.append(
                message
            )
            return
        elif message_type == "error":
            raise RuntimeError(
                str(
                    message.get(
                        "message",
                        "Unknown Sortformer service error.",
                    )
                )
            )


async def main() -> None:
    with wave.open(
        AUDIO_PATH,
        "rb",
    ) as audio_file:
        channels = (
            audio_file.getnchannels()
        )
        sample_width = (
            audio_file.getsampwidth()
        )
        sample_rate = (
            audio_file.getframerate()
        )

        if channels != 1:
            raise RuntimeError(
                "The AMI test clip must be mono."
            )

        if sample_width != 2:
            raise RuntimeError(
                "The AMI test clip must be PCM16."
            )

        frames_to_read = (
            sample_rate
            * TEST_DURATION_SECONDS
        )

        audio_bytes = audio_file.readframes(
            frames_to_read
        )

    bytes_per_chunk = int(
        sample_rate
        * CHUNK_DURATION_SECONDS
        * sample_width
    )

    activity_messages = []
    stopped_holder = []

    async with websockets.connect(
        SERVICE_URI,
        max_size=None,
    ) as websocket:
        connected = json.loads(
            await websocket.recv()
        )

        if connected.get("type") != "connected":
            raise RuntimeError(
                "The service did not send connected."
            )

        await websocket.send(
            json.dumps(
                {
                    "type": "start",
                    "sample_rate": sample_rate,
                    "channels": channels,
                    "bits_per_sample": 16,
                    "maximum_speakers": 4,
                }
            )
        )

        started = json.loads(
            await websocket.recv()
        )

        if started.get("type") != "started":
            raise RuntimeError(
                "The service did not start."
            )

        receive_task = asyncio.create_task(
            receive_messages(
                websocket,
                activity_messages,
                stopped_holder,
            )
        )

        for offset in range(
            0,
            len(audio_bytes),
            bytes_per_chunk,
        ):
            await websocket.send(
                audio_bytes[
                    offset:
                    offset + bytes_per_chunk
                ]
            )

        await websocket.send(
            json.dumps(
                {
                    "type": "stop",
                }
            )
        )

        await receive_task

    if not stopped_holder:
        raise RuntimeError(
            "The service did not report a stopped summary."
        )

    stopped = stopped_holder[0]

    speaker_ids = sorted(
        {
            str(
                message["speaker_id"]
            )
            for message in activity_messages
        }
    )

    final_messages = [
        message
        for message in activity_messages
        if bool(
            message["is_final"]
        )
    ]

    sequences = [
        int(
            message["sequence"]
        )
        for message in activity_messages
    ]

    print("Sortformer activity service test")
    print("--------------------------------")
    print(
        "Audio duration:",
        stopped.get(
            "audio_duration_seconds"
        ),
    )
    print(
        "Model windows:",
        stopped.get(
            "model_windows_processed"
        ),
    )
    print(
        "Prediction frames:",
        stopped.get(
            "prediction_frames"
        ),
    )
    print(
        "Activity messages:",
        len(activity_messages),
    )
    print(
        "Final activities:",
        len(final_messages),
    )
    print(
        "Detected speaker IDs:",
        speaker_ids,
    )

    print()
    print("First final activities")
    print("----------------------")

    for message in final_messages[:12]:
        print(
            f"{message['speaker_id']}: "
            f"{message['start_time_seconds']:.2f}-"
            f"{message['end_time_seconds']:.2f}, "
            f"confidence="
            f"{message['confidence']:.3f}"
        )

    if len(speaker_ids) < 2:
        raise RuntimeError(
            "Expected at least two speakers in the AMI test clip."
        )

    if not final_messages:
        raise RuntimeError(
            "Expected finalized speaker activities."
        )

    if sequences != sorted(sequences):
        raise RuntimeError(
            "Speaker-activity sequences were not ordered."
        )

    if len(sequences) != len(
        set(sequences)
    ):
        raise RuntimeError(
            "Speaker-activity sequences were not unique."
        )

    for message in activity_messages:
        start = float(
            message["start_time_seconds"]
        )
        end = float(
            message["end_time_seconds"]
        )

        if end <= start:
            raise RuntimeError(
                "A speaker activity had an invalid interval."
            )

    if (
        stopped.get(
            "speaker_activity_messages"
        )
        != len(activity_messages)
    ):
        raise RuntimeError(
            "The stopped summary reported the wrong "
            "speaker-activity message count."
        )

    print()
    print(
        "Sortformer speaker-activity WebSocket integration passed."
    )


if __name__ == "__main__":
    asyncio.run(main())
