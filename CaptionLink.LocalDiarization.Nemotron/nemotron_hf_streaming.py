from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import soxr
import torch
from transformers import (
    AutoModelForAudioFrameClassification,
    AutoProcessor,
)


MODEL_NAME = "nvidia/Nemotron-3-Diarization"
MODEL_SAMPLE_RATE = 16_000
MAXIMUM_SPEAKERS = 8
STREAMING_MODE = "low_latency"

PREDICTION_FRAME_DURATION_SECONDS = 0.01
PREDICTION_FRAME_SAMPLE_COUNT = 160


@dataclass(frozen=True)
class NemotronPredictionBatch:
    start_frame_index: int
    probabilities: np.ndarray

    @property
    def frame_count(self) -> int:
        return int(self.probabilities.shape[0])


class NemotronHfStreamingSession:
    """
    Stateful raw-audio streaming wrapper around the official
    Hugging Face Nemotron 3 Diarization implementation.

    Incoming PCM16 may arrive in arbitrary packet sizes.
    Audio is resampled to 16 kHz, buffered until the official
    processor's required chunk boundaries are available, and
    passed through the model with one persistent speaker cache.
    """

    def __init__(
        self,
        processor,
        model,
    ) -> None:
        self._processor = processor
        self._model = model

        self._maximum_speakers = MAXIMUM_SPEAKERS
        self._input_sample_rate = MODEL_SAMPLE_RATE
        self._resampler = None

        self._audio_buffer = np.empty(
            0,
            dtype=np.float32,
        )
        self._buffer_start_sample = 0
        self._total_output_sample_count = 0

        self._speaker_cache = None
        self._next_prediction_frame_index = 0

        self._next_mel_frame_index = 0
        self._next_chunk_start_sample = 0
        self._is_first_model_chunk = True

        self._is_open = False
        self._is_flushed = False

    @property
    def is_open(self) -> bool:
        return self._is_open

    @property
    def prediction_frame_count(self) -> int:
        return self._next_prediction_frame_index

    @property
    def buffered_sample_count(self) -> int:
        return int(self._audio_buffer.size)

    @property
    def total_output_sample_count(self) -> int:
        return self._total_output_sample_count

    def open(
        self,
        input_sample_rate: int,
        maximum_speakers: int = MAXIMUM_SPEAKERS,
    ) -> None:
        if self._is_open:
            raise RuntimeError(
                "The Nemotron HF streaming session is already open."
            )

        if input_sample_rate <= 0:
            raise ValueError(
                "Input sample rate must be positive."
            )

        if not (
            1
            <= maximum_speakers
            <= MAXIMUM_SPEAKERS
        ):
            raise ValueError(
                "Maximum speakers must be between "
                f"1 and {MAXIMUM_SPEAKERS}."
            )

        self._input_sample_rate = input_sample_rate
        self._maximum_speakers = maximum_speakers

        self._resampler = (
            None
            if input_sample_rate == MODEL_SAMPLE_RATE
            else soxr.ResampleStream(
                in_rate=input_sample_rate,
                out_rate=MODEL_SAMPLE_RATE,
                num_channels=1,
                dtype="float32",
                quality="HQ",
            )
        )

        self._audio_buffer = np.empty(
            0,
            dtype=np.float32,
        )
        self._buffer_start_sample = 0
        self._total_output_sample_count = 0

        self._speaker_cache = None
        self._next_prediction_frame_index = 0

        self._next_mel_frame_index = 0
        self._next_chunk_start_sample = 0
        self._is_first_model_chunk = True

        self._is_flushed = False
        self._is_open = True

    def push_pcm16(
        self,
        pcm16: bytes,
    ) -> list[NemotronPredictionBatch]:
        if not self._is_open:
            raise RuntimeError(
                "The Nemotron HF streaming session is not open."
            )

        if self._is_flushed:
            raise RuntimeError(
                "Cannot push audio after the session is flushed."
            )

        if not pcm16:
            return []

        if len(pcm16) % 2 != 0:
            raise ValueError(
                "PCM16 audio contained an incomplete sample."
            )

        input_samples = (
            np.frombuffer(
                pcm16,
                dtype="<i2",
            )
            .astype(np.float32)
            / 32768.0
        )

        if self._resampler is None:
            output_samples = input_samples
        else:
            output_samples = (
                self._resampler.resample_chunk(
                    input_samples,
                    last=False,
                )
            )

        self._append_audio(
            output_samples
        )

        return self._process_complete_chunks()

    def flush(
        self,
    ) -> list[NemotronPredictionBatch]:
        if not self._is_open:
            raise RuntimeError(
                "The Nemotron HF streaming session is not open."
            )

        if self._is_flushed:
            return []

        self._is_flushed = True

        if self._resampler is not None:
            final_samples = (
                self._resampler.resample_chunk(
                    np.empty(
                        0,
                        dtype=np.float32,
                    ),
                    last=True,
                )
            )

            self._append_audio(
                final_samples
            )

        batches = self._process_complete_chunks()

        remaining = (
            self._total_output_sample_count
            - self._next_chunk_start_sample
        )

        if remaining > 0:
            audio = self._slice_audio(
                self._next_chunk_start_sample,
                self._total_output_sample_count,
            )

            batches.append(
                self._process_model_chunk(
                    audio,
                    is_first=self._is_first_model_chunk,
                    is_last=True,
                )
            )

            self._next_chunk_start_sample = (
                self._total_output_sample_count
            )

        self._trim_buffer(
            self._total_output_sample_count
        )

        return batches

    def close(self) -> None:
        self._resampler = None

        self._audio_buffer = np.empty(
            0,
            dtype=np.float32,
        )

        self._buffer_start_sample = 0
        self._total_output_sample_count = 0

        self._speaker_cache = None
        self._next_prediction_frame_index = 0

        self._next_mel_frame_index = 0
        self._next_chunk_start_sample = 0
        self._is_first_model_chunk = True

        self._is_flushed = False
        self._is_open = False

    def _append_audio(
        self,
        samples: np.ndarray,
    ) -> None:
        if samples.size == 0:
            return

        normalized = np.asarray(
            samples,
            dtype=np.float32,
        ).reshape(-1)

        self._audio_buffer = np.concatenate(
            (
                self._audio_buffer,
                normalized,
            )
        )

        self._total_output_sample_count += int(
            normalized.size
        )

    def _process_complete_chunks(
        self,
    ) -> list[NemotronPredictionBatch]:
        batches: list[NemotronPredictionBatch] = []

        while True:
            required_samples = (
                self._processor.num_samples_first_audio_chunk
                if self._is_first_model_chunk
                else self._processor.num_samples_per_audio_chunk
            )

            chunk_end_sample = (
                self._next_chunk_start_sample
                + required_samples
            )

            if (
                chunk_end_sample
                > self._total_output_sample_count
            ):
                break

            audio = self._slice_audio(
                self._next_chunk_start_sample,
                chunk_end_sample,
            )

            batches.append(
                self._process_model_chunk(
                    audio,
                    is_first=self._is_first_model_chunk,
                    is_last=False,
                )
            )

            self._is_first_model_chunk = False

            self._next_mel_frame_index += (
                self._processor.num_mel_frames_per_step
            )

            self._next_chunk_start_sample = (
                self._processor.audio_chunk_start(
                    self._next_mel_frame_index
                )
            )

            self._trim_buffer(
                self._next_chunk_start_sample
            )

        return batches

    def _process_model_chunk(
        self,
        audio: np.ndarray,
        *,
        is_first: bool,
        is_last: bool,
    ) -> NemotronPredictionBatch:
        inputs = self._processor(
            audio,
            sampling_rate=MODEL_SAMPLE_RATE,
            is_streaming=True,
            is_first_audio_chunk=is_first,
            is_last_audio_chunk=is_last,
        )

        inputs = inputs.to(
            self._model.device,
            dtype=self._model.dtype,
        )

        with torch.inference_mode():
            outputs = self._model(
                **inputs,
                speaker_cache=self._speaker_cache,
            )

        self._speaker_cache = (
            outputs.speaker_cache
        )

        probabilities = (
            outputs.logits
            .sigmoid()[0, :, : self._maximum_speakers]
            .detach()
            .to(
                device="cpu",
                dtype=torch.float32,
            )
            .numpy()
            .copy()
        )

        start_frame_index = (
            self._next_prediction_frame_index
        )

        self._next_prediction_frame_index += int(
            probabilities.shape[0]
        )

        return NemotronPredictionBatch(
            start_frame_index=start_frame_index,
            probabilities=probabilities,
        )

    def _slice_audio(
        self,
        start_sample: int,
        end_sample: int,
    ) -> np.ndarray:
        local_start = (
            start_sample
            - self._buffer_start_sample
        )

        local_end = (
            end_sample
            - self._buffer_start_sample
        )

        if (
            local_start < 0
            or local_end > self._audio_buffer.size
            or local_end < local_start
        ):
            raise RuntimeError(
                "Nemotron HF audio buffer does not contain "
                "the requested streaming chunk."
            )

        return self._audio_buffer[
            local_start:local_end
        ].copy()

    def _trim_buffer(
        self,
        absolute_sample: int,
    ) -> None:
        if absolute_sample <= self._buffer_start_sample:
            return

        trim_count = (
            absolute_sample
            - self._buffer_start_sample
        )

        if trim_count >= self._audio_buffer.size:
            self._audio_buffer = np.empty(
                0,
                dtype=np.float32,
            )
        else:
            self._audio_buffer = self._audio_buffer[
                trim_count:
            ].copy()

        self._buffer_start_sample = absolute_sample


def build_nemotron_hf_runtime():
    if not torch.cuda.is_available():
        raise RuntimeError(
            "CUDA is not available. Nemotron requires the NVIDIA GPU."
        )

    processor = AutoProcessor.from_pretrained(
        MODEL_NAME
    )

    processor.set_streaming_mode(
        STREAMING_MODE
    )

    model = (
        AutoModelForAudioFrameClassification
        .from_pretrained(
            MODEL_NAME
        )
    )

    model.eval()
    model = model.cuda()

    torch.cuda.synchronize()

    return processor, model
