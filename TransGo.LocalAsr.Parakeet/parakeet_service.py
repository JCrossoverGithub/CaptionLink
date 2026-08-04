from __future__ import annotations

import asyncio
import json
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager
from functools import partial
from typing import AsyncIterator

import torch
from fastapi import FastAPI, WebSocket, WebSocketDisconnect

from parakeet_audio import StreamingAudioPreprocessor
from parakeet_streaming import (
    MODEL_FRAME_DURATION_SECONDS,
    MODEL_FRAME_SAMPLE_COUNT,
    MODEL_NAME,
    MODEL_SAMPLE_RATE,
    ParakeetPipelineResult,
    ParakeetStreamingSession,
    build_parakeet_pipeline,
)


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    if not torch.cuda.is_available():
        raise RuntimeError(
            "CUDA is not available. Parakeet requires the NVIDIA GPU."
        )

    executor = ThreadPoolExecutor(
        max_workers=1,
        thread_name_prefix="parakeet-asr",
    )

    event_loop = asyncio.get_running_loop()

    print(f"Loading {MODEL_NAME} streaming pipeline...")

    try:
        pipeline = await event_loop.run_in_executor(
            executor,
            build_parakeet_pipeline,
        )
    except Exception:
        executor.shutdown(wait=False, cancel_futures=True)
        raise

    torch.cuda.synchronize()

    app.state.pipeline = pipeline
    app.state.asr_executor = executor
    app.state.session_lock = asyncio.Lock()
    app.state.gpu_name = torch.cuda.get_device_name(0)

    print("Parakeet streaming pipeline is loaded and ready.")
    print(f"GPU: {app.state.gpu_name}")

    try:
        yield
    finally:
        app.state.pipeline = None

        executor.shutdown(
            wait=True,
            cancel_futures=True,
        )

        torch.cuda.empty_cache()


app = FastAPI(
    title="TransGo Local ASR",
    lifespan=lifespan,
)


@app.get("/health")
async def health() -> dict[str, object]:
    pipeline = getattr(app.state, "pipeline", None)

    return {
        "status": "ready" if pipeline is not None else "starting",
        "model_loaded": pipeline is not None,
        "model": MODEL_NAME,
        "cuda_available": torch.cuda.is_available(),
        "gpu": getattr(app.state, "gpu_name", None),
        "streaming": True,
        "model_sample_rate": MODEL_SAMPLE_RATE,
        "model_frame_duration_seconds": (
            MODEL_FRAME_DURATION_SECONDS
        ),
        "model_samples_per_frame": (
            MODEL_FRAME_SAMPLE_COUNT
        ),
    }

def normalize_transcript_text(
    text: str | None,
) -> str:
    if not text:
        return ""

    return " ".join(
        text.split()
    )


def contains_spoken_content(
    text: str,
) -> bool:
    return any(
        character.isalnum()
        for character in text
    )

@app.websocket("/stream")
async def stream_audio(websocket: WebSocket) -> None:
    await websocket.accept()

    session_lock: asyncio.Lock = app.state.session_lock

    if session_lock.locked():
        await websocket.send_json(
            {
                "type": "error",
                "message": (
                    "The local Parakeet service already has an "
                    "active transcription session."
                ),
            }
        )

        await websocket.close(code=1013)
        return

    await session_lock.acquire()

    event_loop = asyncio.get_running_loop()
    executor: ThreadPoolExecutor = app.state.asr_executor

    streaming_session = ParakeetStreamingSession(
        app.state.pipeline
    )

    session_started = False
    session_closed = False

    sample_rate = 0
    channels = 0
    bits_per_sample = 0

    received_bytes = 0
    chunk_count = 0

    model_frame_count = 0
    model_sample_count = 0

    result_sequence = 0
    segment_number = 1
    last_partial_text = ""

    audio_preprocessor: StreamingAudioPreprocessor | None = None

    await websocket.send_json(
        {
            "type": "connected",
            "message": "TransGo audio stream connected.",
        }
    )

    async def run_session_method(
        function,
        *arguments,
        **keyword_arguments,
    ):
        operation = partial(
            function,
            *arguments,
            **keyword_arguments,
        )

        return await event_loop.run_in_executor(
            executor,
            operation,
        )

    async def publish_pipeline_result(
        result: ParakeetPipelineResult,
    ) -> None:
        nonlocal result_sequence
        nonlocal segment_number
        nonlocal last_partial_text

        final_text = normalize_transcript_text(
            result.final_text
        )

        partial_text = normalize_transcript_text(
            result.partial_text
        )

        if (
            final_text
            and contains_spoken_content(
                final_text
            )
        ):
            result_sequence += 1

            await websocket.send_json(
                {
                    "type": "transcript",
                    "segment_id": (
                        f"parakeet-{segment_number:06d}"
                    ),
                    "sequence": result_sequence,
                    "text": final_text,
                    "is_final": True,
                }
            )

            segment_number += 1
            last_partial_text = ""

        if (
            partial_text
            and contains_spoken_content(
                partial_text
            )
            and partial_text
            != last_partial_text
        ):
            result_sequence += 1

            await websocket.send_json(
                {
                    "type": "transcript",
                    "segment_id": (
                        f"parakeet-{segment_number:06d}"
                    ),
                    "sequence": result_sequence,
                    "text": partial_text,
                    "is_final": False,
                }
            )

            last_partial_text = (
                partial_text
            )

    async def transcribe_model_frame(
        frame,
        *,
        is_last: bool,
    ) -> None:
        result = await run_session_method(
            streaming_session.transcribe,
            frame,
            is_last=is_last,
        )

        await publish_pipeline_result(result)

    try:
        while True:
            message = await websocket.receive()

            if message["type"] == "websocket.disconnect":
                break

            text_data = message.get("text")
            binary_data = message.get("bytes")

            if text_data is not None:
                try:
                    command = json.loads(text_data)
                except json.JSONDecodeError:
                    await websocket.send_json(
                        {
                            "type": "error",
                            "message": "Invalid JSON control message.",
                        }
                    )
                    continue

                command_type = command.get("type")

                if command_type == "start":
                    if session_started:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": (
                                    "The transcription session "
                                    "has already started."
                                ),
                            }
                        )
                        continue

                    sample_rate = int(
                        command.get("sample_rate", 0)
                    )

                    channels = int(
                        command.get("channels", 0)
                    )

                    bits_per_sample = int(
                        command.get("bits_per_sample", 0)
                    )

                    if sample_rate <= 0:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": "Sample rate must be positive.",
                            }
                        )
                        continue

                    if channels != 1:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": "Audio must be mono.",
                            }
                        )
                        continue

                    if bits_per_sample != 16:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": "Audio must be PCM16.",
                            }
                        )
                        continue

                    audio_preprocessor = StreamingAudioPreprocessor(
                        input_sample_rate=sample_rate,
                        output_sample_rate=MODEL_SAMPLE_RATE,
                        frame_duration_seconds=(
                            MODEL_FRAME_DURATION_SECONDS
                        ),
                    )

                    await run_session_method(
                        streaming_session.open
                    )

                    received_bytes = 0
                    chunk_count = 0
                    model_frame_count = 0
                    model_sample_count = 0

                    result_sequence = 0
                    segment_number = 1
                    last_partial_text = ""

                    session_started = True

                    await websocket.send_json(
                        {
                            "type": "started",
                            "sample_rate": sample_rate,
                            "channels": channels,
                            "bits_per_sample": bits_per_sample,
                            "model_sample_rate": MODEL_SAMPLE_RATE,
                            "model_frame_duration_seconds": (
                                MODEL_FRAME_DURATION_SECONDS
                            ),
                            "model_samples_per_frame": (
                                MODEL_FRAME_SAMPLE_COUNT
                            ),
                        }
                    )

                elif command_type == "stop":
                    if not session_started:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": (
                                    "The transcription session "
                                    "has not started."
                                ),
                            }
                        )
                        continue

                    if audio_preprocessor is not None:
                        tail_frames = audio_preprocessor.flush()

                        for frame_index, frame in enumerate(
                            tail_frames
                        ):
                            is_last = (
                                frame_index
                                == len(tail_frames) - 1
                            )

                            model_frame_count += 1
                            model_sample_count += frame.valid_length

                            await transcribe_model_frame(
                                frame,
                                is_last=is_last,
                            )

                    await run_session_method(
                        streaming_session.close
                    )

                    session_closed = True

                    input_duration_seconds = calculate_duration(
                        byte_count=received_bytes,
                        sample_rate=sample_rate,
                        channels=channels,
                        bits_per_sample=bits_per_sample,
                    )

                    model_duration_seconds = (
                        model_sample_count / MODEL_SAMPLE_RATE
                        if model_sample_count > 0
                        else 0.0
                    )

                    await websocket.send_json(
                        {
                            "type": "stopped",
                            "chunks_received": chunk_count,
                            "bytes_received": received_bytes,
                            "audio_duration_seconds": round(
                                input_duration_seconds,
                                3,
                            ),
                            "model_frames_processed": (
                                model_frame_count
                            ),
                            "model_samples_processed": (
                                model_sample_count
                            ),
                            "model_audio_duration_seconds": round(
                                model_duration_seconds,
                                3,
                            ),
                        }
                    )

                    await websocket.close(code=1000)
                    break

                else:
                    await websocket.send_json(
                        {
                            "type": "error",
                            "message": (
                                f"Unknown control message: "
                                f"{command_type}"
                            ),
                        }
                    )

                continue

            if binary_data is not None:
                if not session_started:
                    await websocket.send_json(
                        {
                            "type": "error",
                            "message": (
                                "Send a start control message "
                                "before sending audio."
                            ),
                        }
                    )
                    continue

                if audio_preprocessor is None:
                    await websocket.send_json(
                        {
                            "type": "error",
                            "message": (
                                "The audio preprocessor was "
                                "not initialized."
                            ),
                        }
                    )
                    continue

                if len(binary_data) % 2 != 0:
                    await websocket.send_json(
                        {
                            "type": "error",
                            "message": (
                                "PCM16 audio contained an "
                                "incomplete sample."
                            ),
                        }
                    )
                    continue

                received_bytes += len(binary_data)
                chunk_count += 1

                model_frames = audio_preprocessor.push_pcm16(
                    binary_data
                )

                for frame in model_frames:
                    model_frame_count += 1
                    model_sample_count += frame.valid_length

                    await transcribe_model_frame(
                        frame,
                        is_last=False,
                    )

                if chunk_count % 10 == 0:
                    input_duration_seconds = calculate_duration(
                        byte_count=received_bytes,
                        sample_rate=sample_rate,
                        channels=channels,
                        bits_per_sample=bits_per_sample,
                    )

                    model_duration_seconds = (
                        model_sample_count / MODEL_SAMPLE_RATE
                        if model_sample_count > 0
                        else 0.0
                    )

                    await websocket.send_json(
                        {
                            "type": "audio_received",
                            "chunks_received": chunk_count,
                            "bytes_received": received_bytes,
                            "audio_duration_seconds": round(
                                input_duration_seconds,
                                3,
                            ),
                            "model_frames_processed": (
                                model_frame_count
                            ),
                            "model_samples_processed": (
                                model_sample_count
                            ),
                            "model_audio_duration_seconds": round(
                                model_duration_seconds,
                                3,
                            ),
                            "model_buffered_samples": (
                                audio_preprocessor
                                .buffered_sample_count
                            ),
                        }
                    )

    except WebSocketDisconnect:
        print("TransGo WebSocket client disconnected.")

    except Exception as exception:
        print(f"WebSocket session failed: {exception}")

        try:
            await websocket.send_json(
                {
                    "type": "error",
                    "message": str(exception),
                }
            )
        except Exception:
            pass

    finally:
        if session_started and not session_closed:
            try:
                await run_session_method(
                    streaming_session.close
                )
            except Exception as exception:
                print(
                    "Failed to close the Parakeet session: "
                    f"{exception}"
                )

        if session_lock.locked():
            session_lock.release()


def calculate_duration(
    byte_count: int,
    sample_rate: int,
    channels: int,
    bits_per_sample: int,
) -> float:
    if (
        byte_count <= 0
        or sample_rate <= 0
        or channels <= 0
        or bits_per_sample <= 0
    ):
        return 0.0

    bytes_per_second = (
        sample_rate
        * channels
        * (bits_per_sample // 8)
    )

    return byte_count / bytes_per_second
