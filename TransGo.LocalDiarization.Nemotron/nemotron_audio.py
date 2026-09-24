from __future__ import annotations

from dataclasses import dataclass
import math

import numpy as np
import soxr


PREDICTION_FRAME_SAMPLE_COUNT = 1_280


@dataclass(frozen=True)
class NemotronAudioWindow:
    """
    One overlapping Nemotron input window.

    prediction_sample_count is the amount of new audio represented
    by this window. The remaining samples provide right context.
    """

    samples: np.ndarray
    valid_length: int
    prediction_sample_count: int

    @property
    def prediction_frame_count(self) -> int:
        if self.prediction_sample_count <= 0:
            return 0

        return math.ceil(
            self.prediction_sample_count
            / PREDICTION_FRAME_SAMPLE_COUNT
        )


class StreamingNemotronAudioPreprocessor:
    """
    Converts streaming mono PCM16 audio to overlapping 16 kHz
    Nemotron windows.

    With TransGo's low-latency profile:
      - input window: 1.04 seconds / 16,640 samples
      - prediction hop: 0.48 seconds / 7,680 samples
      - retained right context: 0.56 seconds / 8,960 samples
    """

    def __init__(
        self,
        input_sample_rate: int,
        output_sample_rate: int = 16_000,
        window_sample_count: int = 16_640,
        hop_sample_count: int = 7_680,
    ) -> None:
        if input_sample_rate <= 0:
            raise ValueError(
                "Input sample rate must be positive."
            )

        if output_sample_rate <= 0:
            raise ValueError(
                "Output sample rate must be positive."
            )

        if window_sample_count <= 0:
            raise ValueError(
                "Window size must be positive."
            )

        if hop_sample_count <= 0:
            raise ValueError(
                "Hop size must be positive."
            )

        if hop_sample_count > window_sample_count:
            raise ValueError(
                "Hop size cannot exceed window size."
            )

        self.input_sample_rate = input_sample_rate
        self.output_sample_rate = output_sample_rate
        self.window_sample_count = window_sample_count
        self.hop_sample_count = hop_sample_count

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

        self._total_output_sample_count = 0
        self._committed_sample_count = 0
        self._flushed = False

    @property
    def buffered_sample_count(self) -> int:
        return int(self._buffer.size)

    @property
    def total_output_sample_count(self) -> int:
        return self._total_output_sample_count

    @property
    def committed_sample_count(self) -> int:
        return self._committed_sample_count

    def push_pcm16(
        self,
        pcm16: bytes,
    ) -> list[NemotronAudioWindow]:
        if self._flushed:
            raise RuntimeError(
                "Cannot push audio after the preprocessor is flushed."
            )

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
            output_samples = (
                self._resampler.resample_chunk(
                    input_samples,
                    last=False,
                )
            )

        self._append(
            output_samples
        )

        return self._take_complete_windows()

    def flush(
        self,
    ) -> list[NemotronAudioWindow]:
        if self._flushed:
            return []

        self._flushed = True

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

            self._append(
                final_samples
            )

        windows = self._take_complete_windows()

        remaining_new_samples = (
            self._total_output_sample_count
            - self._committed_sample_count
        )

        while remaining_new_samples > 0:
            valid_length = min(
                int(self._buffer.size),
                self.window_sample_count,
            )

            padded = np.zeros(
                self.window_sample_count,
                dtype=np.float32,
            )

            if valid_length > 0:
                padded[:valid_length] = self._buffer[
                    :valid_length
                ]

            prediction_sample_count = min(
                self.hop_sample_count,
                remaining_new_samples,
            )

            windows.append(
                NemotronAudioWindow(
                    samples=padded,
                    valid_length=valid_length,
                    prediction_sample_count=(
                        prediction_sample_count
                    ),
                )
            )

            self._committed_sample_count += (
                prediction_sample_count
            )

            self._advance_buffer()

            remaining_new_samples = (
                self._total_output_sample_count
                - self._committed_sample_count
            )

        self._buffer = np.empty(
            0,
            dtype=np.float32,
        )

        return windows

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

        self._total_output_sample_count += int(
            normalized.size
        )

    def _take_complete_windows(
        self,
    ) -> list[NemotronAudioWindow]:
        windows: list[NemotronAudioWindow] = []

        while (
            self._buffer.size
            >= self.window_sample_count
        ):
            samples = self._buffer[
                : self.window_sample_count
            ].copy()

            windows.append(
                NemotronAudioWindow(
                    samples=samples,
                    valid_length=(
                        self.window_sample_count
                    ),
                    prediction_sample_count=(
                        self.hop_sample_count
                    ),
                )
            )

            self._committed_sample_count += (
                self.hop_sample_count
            )

            self._advance_buffer()

        return windows

    def _advance_buffer(
        self,
    ) -> None:
        if self._buffer.size <= self.hop_sample_count:
            self._buffer = np.empty(
                0,
                dtype=np.float32,
            )
            return

        self._buffer = self._buffer[
            self.hop_sample_count :
        ]
