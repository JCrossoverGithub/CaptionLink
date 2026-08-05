from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import soxr


@dataclass(frozen=True)
class ModelAudioFrame:
    samples: np.ndarray
    valid_length: int


class StreamingAudioPreprocessor:
    """Converts streaming PCM16 audio into 16 kHz float frames."""

    def __init__(
        self,
        input_sample_rate: int,
        output_sample_rate: int = 16_000,
        frame_duration_seconds: float = 0.16,
    ) -> None:
        if input_sample_rate <= 0:
            raise ValueError("Input sample rate must be positive.")

        if output_sample_rate <= 0:
            raise ValueError("Output sample rate must be positive.")

        if frame_duration_seconds <= 0:
            raise ValueError("Frame duration must be positive.")

        self.input_sample_rate = input_sample_rate
        self.output_sample_rate = output_sample_rate

        self.frame_sample_count = round(
            output_sample_rate * frame_duration_seconds
        )

        if self.frame_sample_count <= 0:
            raise ValueError("Frame size must contain at least one sample.")

        self._resampler = (
            None
            if input_sample_rate == output_sample_rate
            else soxr.ResampleStream(
                in_rate=input_sample_rate,
                out_rate=output_sample_rate,
                num_channels=1,
                dtype="float32",
                quality="HQ",
            )
        )

        self._buffer = np.empty(
            0,
            dtype=np.float32,
        )

    @property
    def buffered_sample_count(self) -> int:
        return int(self._buffer.size)

    def push_pcm16(
        self,
        pcm16: bytes,
    ) -> list[ModelAudioFrame]:
        if not pcm16:
            return []

        if len(pcm16) % 2 != 0:
            raise ValueError(
                "PCM16 data contained an incomplete sample."
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
            output_samples = self._resampler.resample_chunk(
                input_samples,
                last=False,
            )

        self._append(output_samples)

        return self._take_complete_frames()

    def flush(self) -> list[ModelAudioFrame]:
        if self._resampler is not None:
            final_samples = self._resampler.resample_chunk(
                np.empty(0, dtype=np.float32),
                last=True,
            )

            self._append(final_samples)

        frames = self._take_complete_frames()

        if self._buffer.size > 0:
            valid_length = int(self._buffer.size)

            padded = np.zeros(
                self.frame_sample_count,
                dtype=np.float32,
            )

            padded[:valid_length] = self._buffer

            frames.append(
                ModelAudioFrame(
                    samples=padded,
                    valid_length=valid_length,
                )
            )

            self._buffer = np.empty(
                0,
                dtype=np.float32,
            )

        return frames

    def _append(
        self,
        samples: np.ndarray,
    ) -> None:
        if samples.size == 0:
            return

        normalized = np.asarray(
            samples,
            dtype=np.float32,
        ).reshape(-1)

        self._buffer = np.concatenate(
            (
                self._buffer,
                normalized,
            )
        )

    def _take_complete_frames(
        self,
    ) -> list[ModelAudioFrame]:
        frames: list[ModelAudioFrame] = []

        while self._buffer.size >= self.frame_sample_count:
            samples = self._buffer[
                : self.frame_sample_count
            ].copy()

            self._buffer = self._buffer[
                self.frame_sample_count :
            ]

            frames.append(
                ModelAudioFrame(
                    samples=samples,
                    valid_length=self.frame_sample_count,
                )
            )

        return frames
