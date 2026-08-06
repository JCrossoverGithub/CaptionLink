from __future__ import annotations

import asyncio
import json
import os
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager
from functools import partial
from typing import AsyncIterator

import torch
from fastapi import FastAPI, WebSocket, WebSocketDisconnect

from sortformer_activity import (
    SortformerSpeakerActivityTracker,
    SpeakerActivityUpdate,
)
from sortformer_audio import (
    StreamingSortformerAudioPreprocessor,
)
from sortformer_streaming import (
    CHUNK_LENGTH,
    CHUNK_RIGHT_CONTEXT,
    FIFO_LENGTH,
    MAXIMUM_SPEAKERS,
    MODEL_INPUT_WINDOW_DURATION_SECONDS,
    MODEL_INPUT_WINDOW_SAMPLE_COUNT,
    MODEL_NAME,
    MODEL_PREDICTION_HOP_DURATION_SECONDS,
    MODEL_PREDICTION_HOP_SAMPLE_COUNT,
    MODEL_SAMPLE_RATE,
    PREDICTION_FRAME_DURATION_SECONDS,
    SPEAKER_CACHE_LENGTH,
    SPEAKER_CACHE_UPDATE_PERIOD,
    SortformerPredictionBatch,
    SortformerStreamingSession,
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
        "prediction_hop_duration_seconds": (
            MODEL_PREDICTION_HOP_DURATION_SECONDS
        ),
        "prediction_hop_sample_count": (
            MODEL_PREDICTION_HOP_SAMPLE_COUNT
        ),
        "chunk_length": CHUNK_LENGTH,
        "chunk_right_context": CHUNK_RIGHT_CONTEXT,
        "fifo_length": FIFO_LENGTH,
        "speaker_cache_update_period": (
            SPEAKER_CACHE_UPDATE_PERIOD
        ),
        "speaker_cache_length": SPEAKER_CACHE_LENGTH,
        "model_input_window_duration_seconds": (
            MODEL_INPUT_WINDOW_DURATION_SECONDS
        ),
        "model_input_window_sample_count": (
            MODEL_INPUT_WINDOW_SAMPLE_COUNT
        ),
        "speaker_activity_start_threshold": 0.50,
        "speaker_activity_stop_threshold": 0.35,
    }

    health_data.update(
        get_cuda_memory_stats()
    )

    return health_data


def is_fatal_cuda_error(
    exception: Exception,
) -> bool:
    message = str(exception).casefold()

    return (
        "device-side assert" in message
        or "cudaerrorassert" in message
    )


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

    event_loop = asyncio.get_running_loop()
    executor: ThreadPoolExecutor = (
        app.state.diarization_executor
    )

    streaming_session = (
        SortformerStreamingSession(
            app.state.model
        )
    )

    activity_tracker: (
        SortformerSpeakerActivityTracker
        | None
    ) = None

    session_started = False
    session_closed = False

    sample_rate = 0
    channels = 0
    bits_per_sample = 0
    maximum_speakers = MAXIMUM_SPEAKERS

    received_bytes = 0
    chunk_count = 0
    model_window_count = 0
    probability_batch_count = 0
    prediction_frame_count = 0
    activity_message_count = 0

    audio_preprocessor: (
        StreamingSortformerAudioPreprocessor
        | None
    ) = None

    await websocket.send_json(
        {
            "type": "connected",
            "message": (
                "TransGo diarization audio stream connected."
            ),
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

    async def publish_activity_update(
        update: SpeakerActivityUpdate,
    ) -> None:
        nonlocal activity_message_count

        activity_message_count += 1

        await websocket.send_json(
            {
                "type": "speaker_activity",
                "activity_id": update.activity_id,
                "sequence": update.sequence,
                "speaker_id": update.speaker_id,
                "start_time_seconds": (
                    update.start_time_seconds
                ),
                "end_time_seconds": (
                    update.end_time_seconds
                ),
                "is_final": update.is_final,
                "confidence": update.confidence,
            }
        )

    async def process_prediction_batch(
        batch: SortformerPredictionBatch,
    ) -> None:
        nonlocal probability_batch_count
        nonlocal prediction_frame_count

        if batch.frame_count <= 0:
            return

        if activity_tracker is None:
            raise RuntimeError(
                "The speaker-activity tracker is unavailable."
            )

        probability_batch_count += 1
        prediction_frame_count += (
            batch.frame_count
        )

        updates = activity_tracker.consume(
            batch
        )

        for update in updates:
            await publish_activity_update(
                update
            )

    async def process_windows(
        windows,
    ) -> None:
        nonlocal model_window_count

        for window in windows:
            model_window_count += 1

            batch = await run_session_method(
                streaming_session.process,
                window,
            )

            await process_prediction_batch(
                batch
            )

    fatal_cuda_error = False

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

                    audio_preprocessor = (
                        StreamingSortformerAudioPreprocessor(
                            input_sample_rate=sample_rate,
                            output_sample_rate=(
                                MODEL_SAMPLE_RATE
                            ),
                            window_sample_count=(
                                MODEL_INPUT_WINDOW_SAMPLE_COUNT
                            ),
                            hop_sample_count=(
                                MODEL_PREDICTION_HOP_SAMPLE_COUNT
                            ),
                        )
                    )

                    activity_tracker = (
                        SortformerSpeakerActivityTracker(
                            maximum_speakers=(
                                maximum_speakers
                            ),
                        )
                    )

                    await run_session_method(
                        streaming_session.open,
                        maximum_speakers,
                    )

                    received_bytes = 0
                    chunk_count = 0
                    model_window_count = 0
                    probability_batch_count = 0
                    prediction_frame_count = 0
                    activity_message_count = 0
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
                            "prediction_frame_duration_seconds": (
                                PREDICTION_FRAME_DURATION_SECONDS
                            ),
                            "prediction_hop_duration_seconds": (
                                MODEL_PREDICTION_HOP_DURATION_SECONDS
                            ),
                            "model_input_window_duration_seconds": (
                                MODEL_INPUT_WINDOW_DURATION_SECONDS
                            ),
                            "model_input_window_sample_count": (
                                MODEL_INPUT_WINDOW_SAMPLE_COUNT
                            ),
                            "speaker_activity_start_threshold": 0.50,
                            "speaker_activity_stop_threshold": 0.35,
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

                    if audio_preprocessor is not None:
                        await process_windows(
                            audio_preprocessor.flush()
                        )

                    if activity_tracker is not None:
                        for update in (
                            activity_tracker.flush()
                        ):
                            await publish_activity_update(
                                update
                            )

                    await run_session_method(
                        streaming_session.close
                    )

                    session_closed = True

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

                    model_duration_seconds = (
                        prediction_frame_count
                        * PREDICTION_FRAME_DURATION_SECONDS
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
                            "model_windows_processed": (
                                model_window_count
                            ),
                            "probability_batches": (
                                probability_batch_count
                            ),
                            "prediction_frames": (
                                prediction_frame_count
                            ),
                            "speaker_activity_messages": (
                                activity_message_count
                            ),
                            "model_audio_duration_seconds": round(
                                model_duration_seconds,
                                3,
                            ),
                            "model_buffered_samples": 0,
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

                received_bytes += len(
                    binary_data
                )
                chunk_count += 1

                windows = (
                    audio_preprocessor.push_pcm16(
                        binary_data
                    )
                )

                await process_windows(
                    windows
                )

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

                    model_duration_seconds = (
                        prediction_frame_count
                        * PREDICTION_FRAME_DURATION_SECONDS
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
                            "model_windows_processed": (
                                model_window_count
                            ),
                            "probability_batches": (
                                probability_batch_count
                            ),
                            "prediction_frames": (
                                prediction_frame_count
                            ),
                            "speaker_activity_messages": (
                                activity_message_count
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
        print(
            "TransGo diarization WebSocket "
            "client disconnected.",
            flush=True,
        )
    except Exception as exception:
        fatal_cuda_error = (
            is_fatal_cuda_error(
                exception
            )
        )

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
        if (
            session_started
            and not session_closed
            and not fatal_cuda_error
        ):
            try:
                await run_session_method(
                    streaming_session.close
                )
            except Exception as exception:
                if is_fatal_cuda_error(
                    exception
                ):
                    fatal_cuda_error = True

                print(
                    "Failed to close the Sortformer "
                    f"session: {exception}",
                    flush=True,
                )

        if session_lock.locked():
            session_lock.release()

        if fatal_cuda_error:
            print(
                "Fatal CUDA error detected. "
                "Terminating the Sortformer service so "
                "TransGo can launch a clean process.",
                flush=True,
            )

            os._exit(70)
