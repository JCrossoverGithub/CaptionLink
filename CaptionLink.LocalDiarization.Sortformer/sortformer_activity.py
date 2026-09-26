from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from sortformer_streaming import (
    PREDICTION_FRAME_DURATION_SECONDS,
    SortformerPredictionBatch,
)


@dataclass(frozen=True)
class SpeakerActivityUpdate:
    activity_id: str
    sequence: int
    speaker_id: str
    start_time_seconds: float
    end_time_seconds: float
    is_final: bool
    confidence: float


@dataclass
class _OpenSpeakerActivity:
    activity_id: str
    speaker_index: int
    start_frame_index: int
    end_frame_index: int
    confidence_sum: float
    confidence_frame_count: int

    def add_frame(
        self,
        frame_index: int,
        confidence: float,
    ) -> None:
        if frame_index != self.end_frame_index:
            raise RuntimeError(
                "Speaker-activity frames must be contiguous."
            )

        self.end_frame_index += 1
        self.confidence_sum += confidence
        self.confidence_frame_count += 1

    @property
    def average_confidence(self) -> float:
        if self.confidence_frame_count <= 0:
            return 0.0

        return (
            self.confidence_sum
            / self.confidence_frame_count
        )


class SortformerSpeakerActivityTracker:
    """
    Converts Sortformer probability frames into stable speaker
    activity intervals.

    Each speaker channel is tracked independently, so overlapping
    speakers can be active at the same time.

    start_threshold begins a new interval. stop_threshold is lower
    than start_threshold to reduce rapid on/off flicker.
    """

    def __init__(
        self,
        maximum_speakers: int,
        start_threshold: float = 0.50,
        stop_threshold: float = 0.35,
    ) -> None:
        if maximum_speakers <= 0:
            raise ValueError(
                "Maximum speakers must be positive."
            )

        if not (
            0.0
            <= stop_threshold
            <= start_threshold
            <= 1.0
        ):
            raise ValueError(
                "Thresholds must satisfy "
                "0 <= stop <= start <= 1."
            )

        self._maximum_speakers = maximum_speakers
        self._start_threshold = start_threshold
        self._stop_threshold = stop_threshold

        self._open_activities: dict[
            int,
            _OpenSpeakerActivity,
        ] = {}

        self._activity_numbers = [
            0
            for _ in range(maximum_speakers)
        ]

        self._next_expected_frame_index = 0
        self._sequence = 0
        self._closed = False

    @property
    def next_expected_frame_index(self) -> int:
        return self._next_expected_frame_index

    def consume(
        self,
        batch: SortformerPredictionBatch,
    ) -> list[SpeakerActivityUpdate]:
        if self._closed:
            raise RuntimeError(
                "Cannot consume predictions after flush."
            )

        probabilities = np.asarray(
            batch.probabilities,
            dtype=np.float32,
        )

        if probabilities.ndim != 2:
            raise ValueError(
                "Sortformer probabilities must be two-dimensional."
            )

        if probabilities.shape[1] != self._maximum_speakers:
            raise ValueError(
                "The probability speaker count does not match "
                "the tracker configuration."
            )

        if (
            batch.start_frame_index
            != self._next_expected_frame_index
        ):
            raise ValueError(
                "Sortformer prediction batches must be continuous."
            )

        updates: list[SpeakerActivityUpdate] = []

        for local_frame_index in range(
            probabilities.shape[0]
        ):
            frame_index = (
                batch.start_frame_index
                + local_frame_index
            )

            for speaker_index in range(
                self._maximum_speakers
            ):
                probability = float(
                    np.clip(
                        probabilities[
                            local_frame_index,
                            speaker_index,
                        ],
                        0.0,
                        1.0,
                    )
                )

                open_activity = (
                    self._open_activities.get(
                        speaker_index
                    )
                )

                threshold = (
                    self._stop_threshold
                    if open_activity is not None
                    else self._start_threshold
                )

                is_active = (
                    probability >= threshold
                )

                if is_active:
                    if open_activity is None:
                        open_activity = (
                            self._start_activity(
                                speaker_index=(
                                    speaker_index
                                ),
                                frame_index=frame_index,
                                confidence=probability,
                            )
                        )

                        self._open_activities[
                            speaker_index
                        ] = open_activity
                    else:
                        open_activity.add_frame(
                            frame_index=frame_index,
                            confidence=probability,
                        )

                elif open_activity is not None:
                    updates.append(
                        self._create_update(
                            open_activity,
                            is_final=True,
                        )
                    )

                    del self._open_activities[
                        speaker_index
                    ]

        self._next_expected_frame_index += (
            probabilities.shape[0]
        )

        # Publish one revision for each interval still active at
        # the end of this prediction batch.
        for speaker_index in sorted(
            self._open_activities
        ):
            updates.append(
                self._create_update(
                    self._open_activities[
                        speaker_index
                    ],
                    is_final=False,
                )
            )

        return updates

    def flush(
        self,
    ) -> list[SpeakerActivityUpdate]:
        if self._closed:
            return []

        self._closed = True

        updates = [
            self._create_update(
                self._open_activities[
                    speaker_index
                ],
                is_final=True,
            )
            for speaker_index in sorted(
                self._open_activities
            )
        ]

        self._open_activities.clear()

        return updates

    def _start_activity(
        self,
        speaker_index: int,
        frame_index: int,
        confidence: float,
    ) -> _OpenSpeakerActivity:
        self._activity_numbers[
            speaker_index
        ] += 1

        activity_number = (
            self._activity_numbers[
                speaker_index
            ]
        )

        return _OpenSpeakerActivity(
            activity_id=(
                "sortformer-"
                f"speaker-{speaker_index + 1}-"
                f"{activity_number:06d}"
            ),
            speaker_index=speaker_index,
            start_frame_index=frame_index,
            end_frame_index=frame_index + 1,
            confidence_sum=confidence,
            confidence_frame_count=1,
        )

    def _create_update(
        self,
        activity: _OpenSpeakerActivity,
        is_final: bool,
    ) -> SpeakerActivityUpdate:
        self._sequence += 1

        return SpeakerActivityUpdate(
            activity_id=activity.activity_id,
            sequence=self._sequence,
            speaker_id=(
                f"speaker-{activity.speaker_index + 1}"
            ),
            start_time_seconds=round(
                activity.start_frame_index
                * PREDICTION_FRAME_DURATION_SECONDS,
                3,
            ),
            end_time_seconds=round(
                activity.end_frame_index
                * PREDICTION_FRAME_DURATION_SECONDS,
                3,
            ),
            is_final=is_final,
            confidence=round(
                activity.average_confidence,
                6,
            ),
        )
