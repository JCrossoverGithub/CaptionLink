from __future__ import annotations

import asyncio
import json
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager
from typing import AsyncIterator

import torch
from fastapi import FastAPI, WebSocket, WebSocketDisconnect

from sortformer_streaming import (
    CHUNK_LENGTH,
    CHUNK_RIGHT_CONTEXT,
    FIFO_LENGTH,
    MAXIMUM_SPEAKERS,
    MODEL_NAME,
    MODEL_SAMPLE_RATE,
    PREDICTION_FRAME_DURATION_SECONDS,
    SPEAKER_CACHE_LENGTH,
    SPEAKER_CACHE_UPDATE_PERIOD,
    build_sortformer_model,
)


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    if not torch.cuda.is_available():
        raise RuntimeError(
            "CUDA is not available. Sortformer requires the NVIDIA GPU."
        )

    executor = ThreadPoolExecutor(
        max_workers=1,
        thread_name_prefix="sortformer-diarization",
    )

    event_loop = asyncio.get_running_loop()

    print(
        f"Loading {MODEL_NAME} streaming diarization model...",
        flush=True,
    )

    try:
        model = await event_loop.run_in_executor(
            executor,
            build_sortformer_model,
        )
    except Exception:
        executor.shutdown(
            wait=False,
            cancel_futures=True,
        )
        raise

    app.state.model = model
    app.state.diarization_executor = executor
    app.state.session_lock = asyncio.Lock()
    app.state.gpu_name = torch.cuda.get_device_name(0)

    print(
        "Sortformer streaming diarization model is loaded and ready.",
        flush=True,
    )
    print(
        f"GPU: {app.state.gpu_name}",
        flush=True,
    )

    try:
        yield
    finally:
        app.state.model = None

        executor.shutdown(
            wait=True,
            cancel_futures=True,
        )

        torch.cuda.empty_cache()


app = FastAPI(
    title="TransGo Local Diarization",
    lifespan=lifespan,
)


def get_cuda_memory_stats() -> dict[str, object]:
    if not torch.cuda.is_available():
        return {
            "cuda_memory_allocated_mib": None,
            "cuda_memory_reserved_mib": None,
            "cuda_max_memory_allocated_mib": None,
            "cuda_max_memory_reserved_mib": None,
        }

    try:
        bytes_per_mib = 1024 * 1024

        return {
            "cuda_memory_allocated_mib": round(
                torch.cuda.memory_allocated()
                / bytes_per_mib,
                2,
            ),
            "cuda_memory_reserved_mib": round(
                torch.cuda.memory_reserved()
                / bytes_per_mib,
                2,
            ),
            "cuda_max_memory_allocated_mib": round(
                torch.cuda.max_memory_allocated()
                / bytes_per_mib,
                2,
            ),
            "cuda_max_memory_reserved_mib": round(
                torch.cuda.max_memory_reserved()
                / bytes_per_mib,
                2,
            ),
        }
    except Exception as exception:
        return {
            "cuda_memory_allocated_mib": None,
            "cuda_memory_reserved_mib": None,
            "cuda_max_memory_allocated_mib": None,
            "cuda_max_memory_reserved_mib": None,
            "cuda_memory_error": str(exception),
        }


@app.get("/health")
async def health() -> dict[str, object]:
    model = getattr(
        app.state,
        "model",
        None,
    )

    health_data: dict[str, object] = {
        "status": (
            "ready"
            if model is not None
            else "starting"
        ),
        "model_loaded": model is not None,
        "model": MODEL_NAME,
        "cuda_available": torch.cuda.is_available(),
        "gpu": getattr(
            app.state,
            "gpu_name",
            None,
        ),
        "streaming": True,
        "model_sample_rate": MODEL_SAMPLE_RATE,
        "maximum_speakers": MAXIMUM_SPEAKERS,
        "prediction_frame_duration_seconds": (
            PREDICTION_FRAME_DURATION_SECONDS
        ),
        "chunk_length": CHUNK_LENGTH,
        "chunk_right_context": CHUNK_RIGHT_CONTEXT,
        "fifo_length": FIFO_LENGTH,
        "speaker_cache_update_period": (
            SPEAKER_CACHE_UPDATE_PERIOD
        ),
        "speaker_cache_length": SPEAKER_CACHE_LENGTH,
    }

    health_data.update(
        get_cuda_memory_stats()
    )

    return health_data


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


@app.websocket("/stream")
async def stream_audio(
    websocket: WebSocket,
) -> None:
    await websocket.accept()

    session_lock: asyncio.Lock = (
        app.state.session_lock
    )

    if session_lock.locked():
        await websocket.send_json(
            {
                "type": "error",
                "message": (
                    "The local Sortformer service already "
                    "has an active diarization session."
                ),
            }
        )

        await websocket.close(code=1013)
        return

    await session_lock.acquire()

    session_started = False

    sample_rate = 0
    channels = 0
    bits_per_sample = 0

    received_bytes = 0
    chunk_count = 0

    await websocket.send_json(
        {
            "type": "connected",
            "message": (
                "TransGo diarization audio stream connected."
            ),
        }
    )

    try:
        while True:
            message = await websocket.receive()

            if (
                message["type"]
                == "websocket.disconnect"
            ):
                break

            text_data = message.get("text")
            binary_data = message.get("bytes")

            if text_data is not None:
                try:
                    command = json.loads(
                        text_data
                    )
                except json.JSONDecodeError:
                    await websocket.send_json(
                        {
                            "type": "error",
                            "message": (
                                "Invalid JSON control message."
                            ),
                        }
                    )
                    continue

                command_type = command.get(
                    "type"
                )

                if command_type == "start":
                    if session_started:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": (
                                    "The diarization session "
                                    "has already started."
                                ),
                            }
                        )
                        continue

                    sample_rate = int(
                        command.get(
                            "sample_rate",
                            0,
                        )
                    )

                    channels = int(
                        command.get(
                            "channels",
                            0,
                        )
                    )

                    bits_per_sample = int(
                        command.get(
                            "bits_per_sample",
                            0,
                        )
                    )

                    maximum_speakers = int(
                        command.get(
                            "maximum_speakers",
                            MAXIMUM_SPEAKERS,
                        )
                    )

                    if sample_rate <= 0:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": (
                                    "Sample rate must be positive."
                                ),
                            }
                        )
                        continue

                    if channels != 1:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": (
                                    "Audio must be mono."
                                ),
                            }
                        )
                        continue

                    if bits_per_sample != 16:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": (
                                    "Audio must be PCM16."
                                ),
                            }
                        )
                        continue

                    if not (
                        1
                        <= maximum_speakers
                        <= MAXIMUM_SPEAKERS
                    ):
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": (
                                    "Maximum speakers must be "
                                    f"between 1 and "
                                    f"{MAXIMUM_SPEAKERS}."
                                ),
                            }
                        )
                        continue

                    received_bytes = 0
                    chunk_count = 0
                    session_started = True

                    await websocket.send_json(
                        {
                            "type": "started",
                            "sample_rate": sample_rate,
                            "channels": channels,
                            "bits_per_sample": (
                                bits_per_sample
                            ),
                            "maximum_speakers": (
                                maximum_speakers
                            ),
                            "model_sample_rate": (
                                MODEL_SAMPLE_RATE
                            ),
                        }
                    )

                elif command_type == "stop":
                    if not session_started:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": (
                                    "The diarization session "
                                    "has not started."
                                ),
                            }
                        )
                        continue

                    duration_seconds = (
                        calculate_duration(
                            byte_count=received_bytes,
                            sample_rate=sample_rate,
                            channels=channels,
                            bits_per_sample=(
                                bits_per_sample
                            ),
                        )
                    )

                    await websocket.send_json(
                        {
                            "type": "stopped",
                            "chunks_received": (
                                chunk_count
                            ),
                            "bytes_received": (
                                received_bytes
                            ),
                            "audio_duration_seconds": round(
                                duration_seconds,
                                3,
                            ),
                        }
                    )

                    await websocket.close(
                        code=1000
                    )
                    break

                else:
                    await websocket.send_json(
                        {
                            "type": "error",
                            "message": (
                                "Unknown control message: "
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

                received_bytes += len(
                    binary_data
                )
                chunk_count += 1

                if chunk_count % 10 == 0:
                    duration_seconds = (
                        calculate_duration(
                            byte_count=received_bytes,
                            sample_rate=sample_rate,
                            channels=channels,
                            bits_per_sample=(
                                bits_per_sample
                            ),
                        )
                    )

                    await websocket.send_json(
                        {
                            "type": (
                                "audio_received"
                            ),
                            "chunks_received": (
                                chunk_count
                            ),
                            "bytes_received": (
                                received_bytes
                            ),
                            "audio_duration_seconds": round(
                                duration_seconds,
                                3,
                            ),
                        }
                    )

    except WebSocketDisconnect:
        print(
            "TransGo diarization WebSocket "
            "client disconnected.",
            flush=True,
        )
    except Exception as exception:
        print(
            "Sortformer WebSocket session "
            f"failed: {exception}",
            flush=True,
        )

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
        if session_lock.locked():
            session_lock.release()
