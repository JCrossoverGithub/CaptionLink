from __future__ import annotations

import numpy as np

from sortformer_activity import (
    SortformerSpeakerActivityTracker,
)
from sortformer_streaming import (
    SortformerPredictionBatch,
)


tracker = SortformerSpeakerActivityTracker(
    maximum_speakers=4,
)

first_probabilities = np.zeros(
    (6, 4),
    dtype=np.float32,
)

# Speaker 1: frames 0-5.
first_probabilities[:, 0] = 0.90

# Speaker 2 begins at frame 4, overlapping speaker 1.
first_probabilities[4:, 1] = 0.85

second_probabilities = np.zeros(
    (6, 4),
    dtype=np.float32,
)

# Speaker 2 continues through global frame 9.
second_probabilities[:4, 1] = 0.88

# Speaker 3 begins at global frame 10 and remains active
# through the end, so flush() must finalize it.
second_probabilities[4:, 2] = 0.92

first_updates = tracker.consume(
    SortformerPredictionBatch(
        start_frame_index=0,
        probabilities=first_probabilities,
    )
)

second_updates = tracker.consume(
    SortformerPredictionBatch(
        start_frame_index=6,
        probabilities=second_probabilities,
    )
)

flush_updates = tracker.flush()

all_updates = (
    first_updates
    + second_updates
    + flush_updates
)

print("Speaker activity updates")
print("------------------------")

for update in all_updates:
    print(
        f"sequence={update.sequence}, "
        f"id={update.activity_id}, "
        f"speaker={update.speaker_id}, "
        f"start={update.start_time_seconds:.2f}, "
        f"end={update.end_time_seconds:.2f}, "
        f"final={update.is_final}, "
        f"confidence={update.confidence:.3f}"
    )

final_updates = [
    update
    for update in all_updates
    if update.is_final
]

expected_finals = [
    (
        "speaker-1",
        0.00,
        0.48,
    ),
    (
        "speaker-2",
        0.32,
        0.80,
    ),
    (
        "speaker-3",
        0.80,
        0.96,
    ),
]

actual_finals = [
    (
        update.speaker_id,
        update.start_time_seconds,
        update.end_time_seconds,
    )
    for update in final_updates
]

if actual_finals != expected_finals:
    raise RuntimeError(
        "Final speaker activities did not match "
        f"the expected intervals: {actual_finals}"
    )

speaker_1_updates = [
    update
    for update in all_updates
    if update.speaker_id == "speaker-1"
]

if len(speaker_1_updates) != 2:
    raise RuntimeError(
        "Expected one interim and one final "
        "speaker-1 update."
    )

if (
    speaker_1_updates[0].activity_id
    != speaker_1_updates[1].activity_id
):
    raise RuntimeError(
        "Speaker-1 revisions did not preserve "
        "their activity ID."
    )

if [
    update.sequence
    for update in all_updates
] != list(
    range(
        1,
        len(all_updates) + 1,
    )
):
    raise RuntimeError(
        "Activity sequences were not continuous."
    )

print()
print(
    "Sortformer probability-to-activity tracking passed."
)
