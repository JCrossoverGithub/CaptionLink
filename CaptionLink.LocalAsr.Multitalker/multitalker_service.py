from __future__ import annotations

import asyncio
import os
import time
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager
from functools import partial
from typing import AsyncIterator

import numpy as np
import soxr
import torch
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from omegaconf import OmegaConf

import nemo.collections.asr as nemo_asr
from nemo.collections.asr.models.sortformer_diar_models import (
    SortformerEncLabelModel,
)
from nemo.collections.asr.parts.utils.multispk_transcribe_utils import (
    SpeakerTaggedASR,
    configure_diar_streaming,
    validate_feature_frame_strides,
)
from nemo.collections.asr.parts.utils.streaming_utils import (
    CacheAwareStreamingAudioBuffer,
)


ASR_MODEL_NAME = (
    "nvidia/multitalker-parakeet-streaming-0.6b-v1"
)
DIAR_MODEL_NAME = "nvidia/Nemotron-3-Diarization"

MODEL_SAMPLE_RATE = 16_000
MAX_SPEAKERS = 4

FINAL_SILENCE_SECONDS = 2.0
SPEAKER_ACTIVITY_THRESHOLD = 0.5


def normalize_text(text: object) -> str:
    if text is None:
        return ""

    return " ".join(str(text).split())


def to_transgo_speaker_id(
    speaker: object,
) -> str | None:
    value = str(speaker)

    prefix = "speaker_"

    if not value.startswith(prefix):
        return None

    try:
        zero_based = int(value[len(prefix):])
    except ValueError:
        return None

    if zero_based < 0:
        return None

    return f"speaker-{zero_based + 1}"


def is_fatal_cuda_error(
    exception: Exception,
) -> bool:
    message = str(exception).casefold()

    return (
        "device-side assert" in message
        or "cudaerrorassert" in message
    )


class StreamingPcmResampler:
    def __init__(
        self,
        input_sample_rate: int,
    ) -> None:
        if input_sample_rate <= 0:
            raise ValueError(
                "Input sample rate must be positive."
            )

        self._input_sample_rate = input_sample_rate

        self._stream = (
            None
            if input_sample_rate == MODEL_SAMPLE_RATE
            else soxr.ResampleStream(
                input_sample_rate,
                MODEL_SAMPLE_RATE,
                1,
                dtype="float32",
                quality="HQ",
            )
        )

    def push_pcm16(
        self,
        pcm_bytes: bytes,
    ) -> np.ndarray:
        if len(pcm_bytes) % 2 != 0:
            raise ValueError(
                "PCM16 audio contained an incomplete sample."
            )

        samples = np.frombuffer(
            pcm_bytes,
            dtype="<i2",
        ).astype(
            np.float32,
            copy=False,
        )

        samples /= 32768.0

        if self._stream is None:
            return samples

        return self._stream.resample_chunk(
            samples,
            last=False,
        )

    def flush(self) -> np.ndarray:
        if self._stream is None:
            return np.zeros(
                0,
                dtype=np.float32,
            )

        return self._stream.resample_chunk(
            np.zeros(
                0,
                dtype=np.float32,
            ),
            last=True,
        )


def build_models():
    if not torch.cuda.is_available():
        raise RuntimeError(
            "CUDA is not available. "
            "Multitalker Parakeet requires the NVIDIA GPU."
        )

    torch.set_float32_matmul_precision(
        "highest"
    )

    device = torch.device("cuda:0")

    print(
        f"Loading {ASR_MODEL_NAME}...",
        flush=True,
    )

    asr_model = (
        nemo_asr.models.ASRModel
        .from_pretrained(
            model_name=ASR_MODEL_NAME
        )
        .to(device)
        .eval()
    )

    print(
        f"Loading {DIAR_MODEL_NAME}...",
        flush=True,
    )

    diar_model = (
        SortformerEncLabelModel
        .from_pretrained(
            model_name=DIAR_MODEL_NAME
        )
    )

    diar_dtype = (
        torch.bfloat16
        if torch.cuda.is_bf16_supported()
        else torch.float32
    )

    diar_model = (
        diar_model
        .to(
            device=device,
            dtype=diar_dtype,
        )
        .eval()
    )

    asr_model.encoder.set_default_att_context_size(
        att_context_size=[70, 13]
    )

    validate_feature_frame_strides(
        asr_model=asr_model,
        diar_model=diar_model,
    )

    torch.cuda.synchronize()

    print(
        "Multitalker models loaded.",
        flush=True,
    )
    print(
        f"GPU: {torch.cuda.get_device_name(0)}",
        flush=True,
    )

    return (
        asr_model,
        diar_model,
    )


class LiveMultitalkerSession:
    def __init__(
        self,
        asr_model,
        diar_model,
    ) -> None:
        self.cfg = OmegaConf.create(
            {
                "device": str(asr_model.device),
                "sample_rate": MODEL_SAMPLE_RATE,
                "deploy_mode": True,
                "streaming_mode": True,

                "max_num_of_spks": MAX_SPEAKERS,
                "batch_size": 1,

                "parallel_speaker_strategy": True,
                "masked_asr": False,
                "mask_preencode": False,
                "single_speaker_mode": False,

                "cache_gating": True,
                "cache_gating_buffer_size": 2,
                "binary_diar_preds": True,

                "spkcache_len": None,
                "spkcache_update_period": 222,
                "fifo_len": 264,
                "diar_right_context": 0,

                "att_context_size": [70, 13],

                "use_amp": True,
                "precision": "bf16",

                "online_normalization": False,
                "pad_and_drop_preencoded": False,

                "feat_len_sec": 0.01,
                "discarded_frames": 8,

                "word_window": 50,
                "sent_break_sec": 1.0,
                "fix_prev_words_count": 5,
                "update_prev_words_sentence": 5,

                "left_frame_shift": -1,
                "right_frame_shift": 0,
                "min_sigmoid_val": 1e-2,

                "ignored_initial_frame_steps": 5,

                "generate_realtime_scripts": False,
                "print_sample_indices": [0],
                "colored_text": False,
                "verbose": False,
                "print_time": False,
                "log": False,

                "spk_supervision": "diar",
            }
        )

        self.asr_model = asr_model
        self.diar_model = diar_model

        streaming_cfg = (
            asr_model.encoder.streaming_cfg
        )

        diar_chunk_len = (
            streaming_cfg.valid_out_len
            + streaming_cfg.cache_drop_size
        )

        configure_diar_streaming(
            diar_model=diar_model,
            cfg=self.cfg,
            output_subsampling_factor=(
                asr_model.encoder
                .subsampling_factor
            ),
            diar_chunk_len=diar_chunk_len,
        )

        self.cfg.spkcache_len = int(
            diar_model
            .sortformer_modules
            .spkcache_len
        )

        self.streamer = SpeakerTaggedASR(
            self.cfg,
            asr_model,
            diar_model,
        )

        self.audio_buffer = (
            CacheAwareStreamingAudioBuffer(
                model=asr_model,
                online_normalization=False,
            )
        )

        feature_stride = float(
            asr_model
            .cfg
            .preprocessor
            .window_stride
        )

        hop_feature_frames = (
            streaming_cfg.valid_out_len
            * asr_model.encoder.subsampling_factor
        )

        self.hop_samples = round(
            hop_feature_frames
            * feature_stride
            * MODEL_SAMPLE_RATE
        )

        cache_frames = (
            streaming_cfg
            .pre_encode_cache_size
        )

        if isinstance(
            cache_frames,
            (list, tuple),
        ):
            cache_frames = cache_frames[-1]

        self.cache_samples = round(
            cache_frames
            * feature_stride
            * MODEL_SAMPLE_RATE
        )

        self.frame_samples = (
            self.hop_samples
            + self.cache_samples
        )

        self.pending_audio = np.zeros(
            self.cache_samples,
            dtype=np.float32,
        )

        self.step_num = 0
        self.result_sequence = 0

        self.activity_sequence = 0
        self._activity_counter = 0

        self._open_speaker_activities: dict[
            str,
            dict[str, object],
        ] = {}

        self._last_emitted: dict[
            str,
            tuple[object, ...],
        ] = {}

        self._finalized: set[str] = set()

        self._use_bf16 = (
            torch.cuda.is_bf16_supported()
        )

        print(
            "Live Multitalker session geometry: "
            f"hop={self.hop_samples} samples "
            f"({self.hop_samples / MODEL_SAMPLE_RATE:.3f}s), "
            f"frame={self.frame_samples} samples "
            f"({self.frame_samples / MODEL_SAMPLE_RATE:.3f}s)",
            flush=True,
        )

    @property
    def processed_audio_seconds(self) -> float:
        return (
            self.step_num
            * self.hop_samples
            / MODEL_SAMPLE_RATE
        )

    def _collect_speaker_activity_updates(
        self,
        *,
        finalize_all: bool = False,
    ) -> list[dict[str, object]]:
        updates: list[
            dict[str, object]
        ] = []

        def emit_activity(
            speaker_id: str,
            state: dict[str, object],
            *,
            is_final: bool,
        ) -> None:
            self.activity_sequence += 1

            confidence_sum = float(
                state["confidence_sum"]
            )

            confidence_count = int(
                state["confidence_count"]
            )

            confidence = (
                confidence_sum
                / confidence_count
                if confidence_count > 0
                else 0.0
            )

            updates.append(
                {
                    "type": "speaker_activity",
                    "activity_id": (
                        state["activity_id"]
                    ),
                    "sequence": (
                        self.activity_sequence
                    ),
                    "speaker_id": speaker_id,
                    "start_time_seconds": round(
                        float(
                            state[
                                "start_time_seconds"
                            ]
                        ),
                        3,
                    ),
                    "end_time_seconds": round(
                        float(
                            state[
                                "end_time_seconds"
                            ]
                        ),
                        3,
                    ),
                    "is_final": is_final,
                    "confidence": round(
                        confidence,
                        4,
                    ),
                }
            )

        if finalize_all:
            for speaker_id in sorted(
                self._open_speaker_activities
            ):
                emit_activity(
                    speaker_id,
                    self._open_speaker_activities[
                        speaker_id
                    ],
                    is_final=True,
                )

            self._open_speaker_activities.clear()

            return updates

        diar_states = (
            self.streamer
            .instance_manager
            .diar_states
        )

        if (
            diar_states is None
            or diar_states.previous_chunk_preds
            is None
        ):
            return updates

        predictions = (
            diar_states.previous_chunk_preds
        )

        if (
            predictions.ndim != 3
            or predictions.shape[0] == 0
            or predictions.shape[1] == 0
        ):
            return updates

        fresh_frame_count = min(
            int(
                self.streamer
                ._frame_hop_length
            ),
            int(predictions.shape[1]),
        )

        if fresh_frame_count <= 0:
            return updates

        fresh_predictions = (
            predictions[
                0,
                -fresh_frame_count:,
                :MAX_SPEAKERS,
            ]
            .detach()
            .float()
            .cpu()
        )

        hop_end_time = (
            self.processed_audio_seconds
        )

        hop_duration = (
            self.hop_samples
            / MODEL_SAMPLE_RATE
        )

        frame_seconds = (
            hop_duration
            / fresh_frame_count
        )

        hop_start_time = max(
            0.0,
            hop_end_time - hop_duration,
        )

        speaker_count = int(
            fresh_predictions.shape[1]
        )

        for speaker_index in range(
            speaker_count
        ):
            speaker_id = (
                f"speaker-{speaker_index + 1}"
            )

            state = (
                self._open_speaker_activities
                .get(speaker_id)
            )

            for frame_index in range(
                fresh_frame_count
            ):
                confidence = float(
                    fresh_predictions[
                        frame_index,
                        speaker_index,
                    ].item()
                )

                frame_start_time = (
                    hop_start_time
                    + frame_index
                    * frame_seconds
                )

                frame_end_time = min(
                    hop_end_time,
                    frame_start_time
                    + frame_seconds,
                )

                if (
                    confidence
                    > SPEAKER_ACTIVITY_THRESHOLD
                ):
                    if state is None:
                        self._activity_counter += 1

                        state = {
                            "activity_id": (
                                "multitalker-"
                                f"{speaker_id}-"
                                f"{self._activity_counter:06d}"
                            ),
                            "start_time_seconds": (
                                frame_start_time
                            ),
                            "end_time_seconds": (
                                frame_end_time
                            ),
                            "confidence_sum": (
                                confidence
                            ),
                            "confidence_count": 1,
                        }

                        self._open_speaker_activities[
                            speaker_id
                        ] = state
                    else:
                        state[
                            "end_time_seconds"
                        ] = frame_end_time

                        state[
                            "confidence_sum"
                        ] = (
                            float(
                                state[
                                    "confidence_sum"
                                ]
                            )
                            + confidence
                        )

                        state[
                            "confidence_count"
                        ] = (
                            int(
                                state[
                                    "confidence_count"
                                ]
                            )
                            + 1
                        )

                elif state is not None:
                    emit_activity(
                        speaker_id,
                        state,
                        is_final=True,
                    )

                    self._open_speaker_activities.pop(
                        speaker_id,
                        None,
                    )

                    state = None

            if state is not None:
                emit_activity(
                    speaker_id,
                    state,
                    is_final=False,
                )

        return updates

    def _collect_updates(
        self,
        *,
        finalize_all: bool = False,
        processing_milliseconds: float = 0.0,
    ) -> list[dict[str, object]]:
        states = (
            self.streamer
            .instance_manager
            .batch_asr_states
        )

        if not states:
            return []

        segments = [
            dict(segment)
            for segment in states[0].seglsts
            if normalize_text(
                segment.get("words")
            )
        ]

        by_speaker: dict[
            str,
            list[dict[str, object]],
        ] = {}

        for segment in segments:
            speaker = str(
                segment.get(
                    "speaker",
                    "",
                )
            )

            if not speaker:
                continue

            by_speaker.setdefault(
                speaker,
                [],
            ).append(segment)

        annotated: list[
            tuple[
                float,
                str,
                int,
                int,
                dict[str, object],
            ]
        ] = []

        for speaker, speaker_segments in (
            by_speaker.items()
        ):
            ordered = sorted(
                speaker_segments,
                key=lambda item: float(
                    item.get(
                        "start_time",
                        0.0,
                    )
                ),
            )

            for index, segment in enumerate(
                ordered
            ):
                annotated.append(
                    (
                        float(
                            segment.get(
                                "start_time",
                                0.0,
                            )
                        ),
                        speaker,
                        index,
                        len(ordered),
                        segment,
                    )
                )

        annotated.sort(
            key=lambda item: item[0]
        )

        updates: list[
            dict[str, object]
        ] = []

        for (
            _,
            speaker,
            index,
            speaker_segment_count,
            segment,
        ) in annotated:
            transgo_speaker_id = (
                to_transgo_speaker_id(
                    speaker
                )
            )

            if transgo_speaker_id is None:
                continue

            key = (
                f"{speaker}:{index}"
            )

            if key in self._finalized:
                continue

            text = normalize_text(
                segment.get("words")
            )

            if not text:
                continue

            start_time = float(
                segment.get(
                    "start_time",
                    0.0,
                )
            )

            end_time = float(
                segment.get(
                    "end_time",
                    start_time,
                )
            )

            is_not_current_segment = (
                index
                < speaker_segment_count - 1
            )

            silent_long_enough = (
                self.processed_audio_seconds
                - end_time
                >= FINAL_SILENCE_SECONDS
            )

            is_final = (
                finalize_all
                or is_not_current_segment
                or silent_long_enough
            )

            fingerprint = (
                text,
                round(start_time, 3),
                round(end_time, 3),
                is_final,
            )

            if (
                self._last_emitted.get(key)
                == fingerprint
            ):
                continue

            self._last_emitted[key] = (
                fingerprint
            )

            if is_final:
                self._finalized.add(key)

            self.result_sequence += 1

            updates.append(
                {
                    "type": "transcript",
                    "segment_id": (
                        "multitalker-"
                        f"{transgo_speaker_id}-"
                        f"{index + 1:06d}"
                    ),
                    "sequence": (
                        self.result_sequence
                    ),
                    "speaker_id": (
                        transgo_speaker_id
                    ),
                    "text": text,
                    "is_final": is_final,
                    "result_start_time_seconds": (
                        round(
                            start_time,
                            3,
                        )
                    ),
                    "result_end_time_seconds": (
                        round(
                            end_time,
                            3,
                        )
                    ),
                    "processing_duration_milliseconds": (
                        round(
                            processing_milliseconds,
                            3,
                        )
                    ),
                }
            )

        return updates

    @torch.inference_mode()
    def _process_frame(
        self,
        frame: np.ndarray,
        *,
        is_buffer_empty: bool,
    ) -> list[dict[str, object]]:
        (
            chunk_audio,
            chunk_lengths,
        ) = self.audio_buffer.preprocess_audio(
            frame
        )

        valid_feature_count = int(
            chunk_lengths[0].item()
        )

        chunk_audio = (
            chunk_audio[
                :,
                :,
                :valid_feature_count,
            ]
        )

        drop_extra_pre_encoded = (
            0
            if self.step_num == 0
            else (
                self.asr_model
                .encoder
                .streaming_cfg
                .drop_extra_pre_encoded
            )
        )

        started = time.perf_counter()

        with torch.amp.autocast(
            device_type="cuda",
            dtype=torch.bfloat16,
            enabled=self._use_bf16,
        ):
            self.streamer.perform_parallel_streaming_stt_spk(
                step_num=self.step_num,
                chunk_audio=chunk_audio,
                chunk_lengths=chunk_lengths,
                is_buffer_empty=(
                    is_buffer_empty
                ),
                drop_extra_pre_encoded=(
                    drop_extra_pre_encoded
                ),
            )

        processing_milliseconds = (
            time.perf_counter()
            - started
        ) * 1000.0

        self.step_num += 1

        updates = (
            self._collect_speaker_activity_updates()
        )

        updates.extend(
            self._collect_updates(
                processing_milliseconds=(
                    processing_milliseconds
                )
            )
        )

        return updates

    def accept_audio(
        self,
        samples: np.ndarray,
    ) -> list[dict[str, object]]:
        if samples.size == 0:
            return []

        samples = np.asarray(
            samples,
            dtype=np.float32,
        ).reshape(-1)

        self.pending_audio = (
            np.concatenate(
                [
                    self.pending_audio,
                    samples,
                ]
            )
        )

        updates: list[
            dict[str, object]
        ] = []

        while (
            len(self.pending_audio)
            >= self.frame_samples
        ):
            frame = (
                self.pending_audio[
                    :self.frame_samples
                ].copy()
            )

            self.pending_audio = (
                self.pending_audio[
                    self.hop_samples:
                ]
            )

            updates.extend(
                self._process_frame(
                    frame,
                    is_buffer_empty=False,
                )
            )

        return updates

    def finish(
        self,
    ) -> list[dict[str, object]]:
        updates: list[
            dict[str, object]
        ] = []

        if (
            len(self.pending_audio)
            > self.cache_samples
        ):
            frame = np.zeros(
                self.frame_samples,
                dtype=np.float32,
            )

            usable = min(
                len(self.pending_audio),
                self.frame_samples,
            )

            frame[:usable] = (
                self.pending_audio[:usable]
            )

            updates.extend(
                self._process_frame(
                    frame,
                    is_buffer_empty=True,
                )
            )

        updates.extend(
            self._collect_speaker_activity_updates(
                finalize_all=True,
            )
        )

        updates.extend(
            self._collect_updates(
                finalize_all=True,
            )
        )

        return updates


@asynccontextmanager
async def lifespan(
    app: FastAPI,
) -> AsyncIterator[None]:
    executor = ThreadPoolExecutor(
        max_workers=1,
        thread_name_prefix=(
            "multitalker-asr"
        ),
    )

    event_loop = (
        asyncio.get_running_loop()
    )

    try:
        (
            asr_model,
            diar_model,
        ) = await event_loop.run_in_executor(
            executor,
            build_models,
        )
    except Exception:
        executor.shutdown(
            wait=False,
            cancel_futures=True,
        )
        raise

    app.state.executor = executor
    app.state.asr_model = asr_model
    app.state.diar_model = diar_model
    app.state.session_lock = asyncio.Lock()
    app.state.gpu_name = (
        torch.cuda.get_device_name(0)
    )

    try:
        yield
    finally:
        app.state.asr_model = None
        app.state.diar_model = None

        executor.shutdown(
            wait=True,
            cancel_futures=True,
        )

        torch.cuda.empty_cache()


app = FastAPI(
    title="TransGo Multitalker ASR",
    lifespan=lifespan,
)


@app.get("/health")
async def health() -> dict[str, object]:
    ready = (
        getattr(
            app.state,
            "asr_model",
            None,
        )
        is not None
        and getattr(
            app.state,
            "diar_model",
            None,
        )
        is not None
    )

    return {
        "status": (
            "ready"
            if ready
            else "starting"
        ),
        "model_loaded": ready,
        "model": ASR_MODEL_NAME,
        "diarization_model": (
            DIAR_MODEL_NAME
        ),
        "profile": "multitalker",
        "streaming": True,
        "model_sample_rate": (
            MODEL_SAMPLE_RATE
        ),
        "max_speakers": MAX_SPEAKERS,
        "cuda_available": (
            torch.cuda.is_available()
        ),
        "gpu": getattr(
            app.state,
            "gpu_name",
            None,
        ),
    }


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
                    "The Multitalker service "
                    "already has an active session."
                ),
            }
        )

        await websocket.close(
            code=1013
        )
        return

    await session_lock.acquire()

    event_loop = (
        asyncio.get_running_loop()
    )

    executor: ThreadPoolExecutor = (
        app.state.executor
    )

    session: (
        LiveMultitalkerSession | None
    ) = None

    resampler: (
        StreamingPcmResampler | None
    ) = None

    session_started = False
    session_closed = False
    received_bytes = 0
    chunk_count = 0

    fatal_cuda_error = False

    async def run_gpu(
        function,
        *arguments,
    ):
        operation = partial(
            function,
            *arguments,
        )

        return await (
            event_loop.run_in_executor(
                executor,
                operation,
            )
        )

    async def publish_updates(
        updates: list[
            dict[str, object]
        ],
    ) -> None:
        for update in updates:
            await websocket.send_json(
                update
            )

    await websocket.send_json(
        {
            "type": "connected",
            "message": (
                "TransGo Multitalker "
                "audio stream connected."
            ),
        }
    )

    try:
        while True:
            message = (
                await websocket.receive()
            )

            if (
                message["type"]
                == "websocket.disconnect"
            ):
                break

            text_data = (
                message.get("text")
            )

            binary_data = (
                message.get("bytes")
            )

            if text_data is not None:
                import json

                try:
                    command = json.loads(
                        text_data
                    )
                except json.JSONDecodeError:
                    await websocket.send_json(
                        {
                            "type": "error",
                            "message": (
                                "Invalid JSON "
                                "control message."
                            ),
                        }
                    )
                    continue

                command_type = (
                    command.get("type")
                )

                if command_type == "start":
                    if session_started:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": (
                                    "The session has "
                                    "already started."
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

                    if sample_rate <= 0:
                        raise ValueError(
                            "Sample rate must "
                            "be positive."
                        )

                    if channels != 1:
                        raise ValueError(
                            "Multitalker audio "
                            "must be mono."
                        )

                    if bits_per_sample != 16:
                        raise ValueError(
                            "Multitalker audio "
                            "must be PCM16."
                        )

                    resampler = (
                        StreamingPcmResampler(
                            sample_rate
                        )
                    )

                    session = await run_gpu(
                        LiveMultitalkerSession,
                        app.state.asr_model,
                        app.state.diar_model,
                    )

                    received_bytes = 0
                    chunk_count = 0

                    session_started = True

                    await websocket.send_json(
                        {
                            "type": "started",
                            "profile": (
                                "multitalker"
                            ),
                            "sample_rate": (
                                sample_rate
                            ),
                            "channels": 1,
                            "bits_per_sample": 16,
                            "model_sample_rate": (
                                MODEL_SAMPLE_RATE
                            ),
                            "max_speakers": (
                                MAX_SPEAKERS
                            ),
                        }
                    )

                elif command_type == "stop":
                    if not session_started:
                        await websocket.send_json(
                            {
                                "type": "error",
                                "message": (
                                    "The session has "
                                    "not started."
                                ),
                            }
                        )
                        continue

                    assert session is not None
                    assert resampler is not None

                    tail = resampler.flush()

                    if tail.size > 0:
                        await publish_updates(
                            await run_gpu(
                                session.accept_audio,
                                tail,
                            )
                        )

                    await publish_updates(
                        await run_gpu(
                            session.finish
                        )
                    )

                    session_closed = True

                    await websocket.send_json(
                        {
                            "type": "stopped",
                            "chunks_received": (
                                chunk_count
                            ),
                            "bytes_received": (
                                received_bytes
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
                                "Unknown control "
                                f"message: {command_type}"
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
                                "Send a start "
                                "message before audio."
                            ),
                        }
                    )
                    continue

                assert session is not None
                assert resampler is not None

                received_bytes += len(
                    binary_data
                )

                chunk_count += 1

                samples = (
                    resampler.push_pcm16(
                        binary_data
                    )
                )

                if samples.size > 0:
                    updates = await run_gpu(
                        session.accept_audio,
                        samples,
                    )

                    await publish_updates(
                        updates
                    )

                if chunk_count % 10 == 0:
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
                        }
                    )

    except WebSocketDisconnect:
        print(
            "Multitalker WebSocket "
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
            "Multitalker session failed: "
            f"{exception}",
            flush=True,
        )

        try:
            await websocket.send_json(
                {
                    "type": "error",
                    "message": str(
                        exception
                    ),
                }
            )
        except Exception:
            pass

    finally:
        session = None
        resampler = None

        if session_lock.locked():
            session_lock.release()

        if fatal_cuda_error:
            print(
                "Fatal CUDA error detected; "
                "terminating Multitalker service.",
                flush=True,
            )

            os._exit(70)
