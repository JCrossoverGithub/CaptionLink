from __future__ import annotations

import torch

from sortformer_audio import (
    StreamingSortformerAudioPreprocessor,
)
from sortformer_streaming import (
    MAXIMUM_SPEAKERS,
    MODEL_INPUT_WINDOW_SAMPLE_COUNT,
    MODEL_PREDICTION_HOP_SAMPLE_COUNT,
    MODEL_SAMPLE_RATE,
    SortformerStreamingSession,
    build_sortformer_model,
)


INPUT_SAMPLE_RATE = 48_000
CHUNK_DURATION_SECONDS = 0.1
CHUNK_COUNT = 20

SAMPLES_PER_INPUT_CHUNK = int(
    INPUT_SAMPLE_RATE
    * CHUNK_DURATION_SECONDS
)

PCM16_SILENCE_CHUNK = bytes(
    SAMPLES_PER_INPUT_CHUNK * 2
)


print("Loading Sortformer...")

model = build_sortformer_model()

preprocessor = (
    StreamingSortformerAudioPreprocessor(
        input_sample_rate=INPUT_SAMPLE_RATE,
        output_sample_rate=MODEL_SAMPLE_RATE,
        window_sample_count=(
            MODEL_INPUT_WINDOW_SAMPLE_COUNT
        ),
        hop_sample_count=(
            MODEL_PREDICTION_HOP_SAMPLE_COUNT
        ),
    )
)

session = SortformerStreamingSession(
    model
)

session.open(
    maximum_speakers=MAXIMUM_SPEAKERS
)

batches = []

try:
    for _ in range(CHUNK_COUNT):
        windows = preprocessor.push_pcm16(
            PCM16_SILENCE_CHUNK
        )

        for window in windows:
            batches.append(
                session.process(
                    window
                )
            )

    for window in preprocessor.flush():
        batches.append(
            session.process(
                window
            )
        )
finally:
    session.close()

print()
print("Prediction batches")
print("------------------")

for index, batch in enumerate(
    batches,
    start=1,
):
    maximum = (
        float(batch.probabilities.max())
        if batch.probabilities.size > 0
        else 0.0
    )

    print(
        f"Batch {index}: "
        f"start_frame={batch.start_frame_index}, "
        f"shape={batch.probabilities.shape}, "
        f"maximum={maximum:.6f}"
    )

total_frames = sum(
    batch.frame_count
    for batch in batches
)

print()
print("Summary")
print("-------")
print("Batch count:", len(batches))
print("Prediction frames:", total_frames)
print(
    "Represented duration:",
    round(
        total_frames * 0.08,
        3,
    ),
)
print(
    "Allocated MiB:",
    round(
        torch.cuda.memory_allocated()
        / 1048576,
        2,
    ),
)
print(
    "Reserved MiB:",
    round(
        torch.cuda.memory_reserved()
        / 1048576,
        2,
    ),
)

if len(batches) != 5:
    raise RuntimeError(
        "Expected five overlapping audio windows."
    )

if total_frames != 25:
    raise RuntimeError(
        "Expected 25 Sortformer prediction frames."
    )

if any(
    batch.probabilities.shape[1]
    != MAXIMUM_SPEAKERS
    for batch in batches
):
    raise RuntimeError(
        "Expected four speaker channels."
    )

if [
    batch.start_frame_index
    for batch in batches
] != [0, 6, 12, 18, 24]:
    raise RuntimeError(
        "Prediction frame indices were not continuous."
    )

print()
print(
    "Persistent Sortformer streaming inference passed."
)
