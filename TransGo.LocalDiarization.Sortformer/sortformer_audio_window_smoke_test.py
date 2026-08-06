from __future__ import annotations

from sortformer_audio import (
    StreamingSortformerAudioPreprocessor,
)


INPUT_SAMPLE_RATE = 48_000
OUTPUT_SAMPLE_RATE = 16_000

CHUNK_DURATION_SECONDS = 0.1
CHUNK_COUNT = 20

WINDOW_SAMPLE_COUNT = 16_640
HOP_SAMPLE_COUNT = 7_680

SAMPLES_PER_INPUT_CHUNK = int(
    INPUT_SAMPLE_RATE
    * CHUNK_DURATION_SECONDS
)

PCM16_SILENCE_CHUNK = bytes(
    SAMPLES_PER_INPUT_CHUNK * 2
)


preprocessor = (
    StreamingSortformerAudioPreprocessor(
        input_sample_rate=INPUT_SAMPLE_RATE,
        output_sample_rate=OUTPUT_SAMPLE_RATE,
        window_sample_count=WINDOW_SAMPLE_COUNT,
        hop_sample_count=HOP_SAMPLE_COUNT,
    )
)

windows = []

for _ in range(CHUNK_COUNT):
    windows.extend(
        preprocessor.push_pcm16(
            PCM16_SILENCE_CHUNK
        )
    )

windows.extend(
    preprocessor.flush()
)

prediction_samples = sum(
    window.prediction_sample_count
    for window in windows
)

prediction_frames = sum(
    window.prediction_frame_count
    for window in windows
)

print("Window count:", len(windows))

for index, window in enumerate(
    windows,
    start=1,
):
    print(
        f"Window {index}: "
        f"valid_length={window.valid_length}, "
        f"prediction_samples="
        f"{window.prediction_sample_count}, "
        f"prediction_frames="
        f"{window.prediction_frame_count}"
    )

print(
    "Total resampled samples:",
    preprocessor.total_output_sample_count,
)
print(
    "Committed prediction samples:",
    prediction_samples,
)
print(
    "Prediction frames:",
    prediction_frames,
)
print(
    "Buffered samples after flush:",
    preprocessor.buffered_sample_count,
)

expected_output_samples = 32_000
expected_prediction_frames = 25

if (
    preprocessor.total_output_sample_count
    != expected_output_samples
):
    raise RuntimeError(
        "Expected exactly two seconds of 16 kHz audio."
    )

if prediction_samples != expected_output_samples:
    raise RuntimeError(
        "Prediction hops did not cover all resampled audio."
    )

if prediction_frames != expected_prediction_frames:
    raise RuntimeError(
        "Expected 25 prediction frames for two seconds."
    )

if preprocessor.buffered_sample_count != 0:
    raise RuntimeError(
        "Expected an empty buffer after flush."
    )

print(
    "Overlapping Sortformer windowing passed."
)
