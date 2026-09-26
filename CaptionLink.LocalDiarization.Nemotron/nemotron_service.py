from __future__ import annotations

import asyncio
import json
import os
import traceback
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager
from functools import partial
from typing import AsyncIterator

import torch
from fastapi import FastAPI, WebSocket, WebSocketDisconnect

from nemotron_activity import (
    NemotronSpeakerActivityTracker,
    SpeakerActivityUpdate,
)
from nemotron_hf_streaming import (
    MAXIMUM_SPEAKERS,
    MODEL_NAME,
    MODEL_SAMPLE_RATE,
    PREDICTION_FRAME_DURATION_SECONDS,
    NemotronHfStreamingSession,
    NemotronPredictionBatch,
    build_nemotron_hf_runtime,
)


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    if not torch.cuda.is_available():
        raise RuntimeError(
            "CUDA is not available. Nemotron requires the NVIDIA GPU."
        )

    executor = ThreadPoolExecutor(
        max_workers=1,
        thread_name_prefix="nemotron-diarization",
    )

    event_loop = asyncio.get_running_loop()

    print(
        f"Loading {MODEL_NAME} streaming diarization model...",
        flush=True,
    )

    try:
        processor, model = await event_loop.run_in_executor(
            executor,
            build_nemotron_hf_runtime,
        )
    except Exception:
        executor.shutdown(
            wait=False,
            cancel_futures=True,
        )
        raise

    app.state.processor = processor
    app.state.model = model
    app.state.diarization_executor = executor
    app.state.session_lock = asyncio.Lock()
    app.state.gpu_name = torch.cuda.get_device_name(0)

    print(
        "Nemotron streaming diarization model is loaded and ready.",
        flush=True,
    )
    print(
        f"GPU: {app.state.gpu_name}",
        flush=True,
    )

    try:
        yield
    finally:
        app.state.processor = None
        app.state.model = None

        executor.shutdown(
            wait=True,
            cancel_futures=True,
        )

        torch.cuda.empty_cache()


app = FastAPI(
    title="CaptionLink Local Diarization",
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
    processor = getattr(
        app.state,
        "processor",
        None,
    )

    model = getattr(
        app.state,
        "model",
        None,
    )

    ready = (
        processor is not None
        and model is not None
    )

    prediction_hop_duration_seconds = (
        processor.num_mel_frames_per_step
        * PREDICTION_FRAME_DURATION_SECONDS
        if processor is not None
        else 0.72
    )

    model_input_window_duration_seconds = (
        processor.streaming_latency_ms / 1000.0
        if processor is not None
        else 1.04
    )

    model_input_window_sample_count = (
        processor.num_samples_first_audio_chunk
        if processor is not None
        else 16_680
    )

    health_data: dict[str, object] = {
        "status": (
            "ready"
            if ready
            else "starting"
        ),
        "model_loaded": ready,
        "model": MODEL_NAME,
        "cuda_available": torch.cuda.is_available(),
        "gpu": getattr(
            app.state,
            "gpu_name",
            None,
        ),
        "streaming": True,
        "streaming_backend": "huggingface",
        "streaming_mode": "low_latency",
        "model_sample_rate": MODEL_SAMPLE_RATE,
        "maximum_speakers": MAXIMUM_SPEAKERS,
        "prediction_frame_duration_seconds": (
            PREDICTION_FRAME_DURATION_SECONDS
        ),
        "prediction_hop_duration_seconds": (
            prediction_hop_duration_seconds
        ),
        "prediction_hop_sample_count": round(
            prediction_hop_duration_seconds
            * MODEL_SAMPLE_RATE
        ),
        "model_input_window_duration_seconds": (
            model_input_window_duration_seconds
        ),
        "model_input_window_sample_count": (
            model_input_window_sample_count
        ),
        "streaming_chunk_sample_count": (
            processor.num_samples_per_audio_chunk
            if processor is not None
            else 17_040
        ),
        "speaker_activity_start_threshold": 0.50,
        "speaker_activity_stop_threshold": 0.50,
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
                    "The local Nemotron service already "
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
        NemotronHfStreamingSession(
            app.state.processor,
            app.state.model,
        )
    )

    activity_tracker: (
        NemotronSpeakerActivityTracker
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
    publish_speaker_probabilities = False

    await websocket.send_json(
        {
            "type": "connected",
            "message": (
                "CaptionLink diarization audio stream connected."
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
        batch: NemotronPredictionBatch,
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

        if publish_speaker_probabilities:
            await websocket.send_json(
                {
                    "type": "speaker_probabilities",
                    "start_frame_index": (
                        batch.start_frame_index
                    ),
                    "frame_count": batch.frame_count,
                    "frame_duration_seconds": (
                        PREDICTION_FRAME_DURATION_SECONDS
                    ),
                    "probabilities": (
                        batch.probabilities.tolist()
                    ),
                }
            )

        updates = activity_tracker.consume(
            batch
        )

        for update in updates:
            await publish_activity_update(
                update
            )

    async def process_prediction_batches(
        batches,
    ) -> None:
        nonlocal model_window_count

        for batch in batches:
            model_window_count += 1

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
                    publish_speaker_probabilities = bool(
                        command.get(
                            "publish_speaker_probabilities",
                            False,
                        )
                    )
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

                    activity_tracker = (
                        NemotronSpeakerActivityTracker(
                            maximum_speakers=(
                                maximum_speakers
                            ),
                        )
                    )

                    await run_session_method(
                        streaming_session.open,
                        sample_rate,
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
                                app.state.processor
                                .num_mel_frames_per_step
                                * PREDICTION_FRAME_DURATION_SECONDS
                            ),
                            "model_input_window_duration_seconds": (
                                app.state.processor
                                .streaming_latency_ms
                                / 1000.0
                            ),
                            "model_input_window_sample_count": (
                                app.state.processor
                                .num_samples_first_audio_chunk
                            ),
                            "speaker_activity_start_threshold": 0.50,
                            "speaker_activity_stop_threshold": 0.50,
                            "publishing_speaker_probabilities": (
                                publish_speaker_probabilities
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

                    final_batches = await run_session_method(
                        streaming_session.flush
                    )

                    await process_prediction_batches(
                        final_batches
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

                batches = await run_session_method(
                    streaming_session.push_pcm16,
                    binary_data,
                )

                await process_prediction_batches(
                    batches
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
                                streaming_session
                                .buffered_sample_count
                            ),
                        }
                    )

    except WebSocketDisconnect:
        print(
            "CaptionLink diarization WebSocket "
            "client disconnected.",
            flush=True,
        )
    except Exception as exception:
        fatal_cuda_error = (
            is_fatal_cuda_error(
                exception
            )
        )

        error_message = (
            f"{type(exception).__name__}: {exception}"
        )

        print(
            "Nemotron WebSocket session "
            f"failed: {error_message}",
            flush=True,
        )
        traceback.print_exc()

        try:
            await websocket.send_json(
                {
                    "type": "error",
                    "message": error_message,
                }
            )
        except Exception:
            pass

        try:
            await websocket.close(
                code=1011,
                reason=error_message[:123],
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
                    "Failed to close the Nemotron "
                    f"session: {exception}",
                    flush=True,
                )

        if session_lock.locked():
            session_lock.release()

        if fatal_cuda_error:
            print(
                "Fatal CUDA error detected. "
                "Terminating the Nemotron service so "
                "CaptionLink can launch a clean process.",
                flush=True,
            )

            os._exit(70)
