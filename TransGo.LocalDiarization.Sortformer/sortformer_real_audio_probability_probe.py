from __future__ import annotations

import wave

import numpy as np
import torch

from sortformer_audio import (
    StreamingSortformerAudioPreprocessor,
)
from sortformer_streaming import (
    MAXIMUM_SPEAKERS,
    MODEL_INPUT_WINDOW_SAMPLE_COUNT,
    MODEL_PREDICTION_HOP_SAMPLE_COUNT,
    MODEL_SAMPLE_RATE,
    PREDICTION_FRAME_DURATION_SECONDS,
    SortformerStreamingSession,
    build_sortformer_model,
)


AUDIO_PATH = (
    "/mnt/c/Users/thede/Documents/TransGo-Test-Data/"
    "AMI/ES2013a.test-300-420s.wav"
)

TEST_DURATION_SECONDS = 30
INPUT_CHUNK_DURATION_SECONDS = 0.1


def print_probability_summary(
    probabilities: np.ndarray,
) -> None:
    print()
    print("Streaming probability summary")
    print("-----------------------------")
    print("Shape:", probabilities.shape)
    print("Overall minimum:", float(probabilities.min()))
    print("Overall maximum:", float(probabilities.max()))
    print("Overall mean:", float(probabilities.mean()))

    for speaker_index in range(
        probabilities.shape[1]
    ):
        values = probabilities[:, speaker_index]

        print()
        print(f"Speaker {speaker_index + 1}")
        print(
            "  maximum:",
            round(float(values.max()), 6),
        )
        print(
            "  mean:",
            round(float(values.mean()), 6),
        )
        print(
            "  frames >= 0.50:",
            int(np.count_nonzero(values >= 0.50)),
        )
        print(
            "  frames >= 0.35:",
            int(np.count_nonzero(values >= 0.35)),
        )
        print(
            "  frames >= 0.10:",
            int(np.count_nonzero(values >= 0.10)),
        )

    frame_maximums = probabilities.max(
        axis=1
    )

    top_frame_indices = np.argsort(
        frame_maximums
    )[-20:][::-1]

    print()
    print("Highest-probability streaming frames")
    print("------------------------------------")

    for frame_index in top_frame_indices:
        start_time = (
            frame_index
            * PREDICTION_FRAME_DURATION_SECONDS
        )

        values = ", ".join(
            f"{value:.4f}"
            for value in probabilities[
                frame_index
            ]
        )

        print(
            f"{start_time:6.2f}s: [{values}]"
        )


def describe_offline_outputs(
    outputs,
) -> None:
    print()
    print("Offline tensor output")
    print("---------------------")

    if isinstance(outputs, torch.Tensor):
        tensors = [outputs]
    elif isinstance(outputs, (list, tuple)):
        tensors = [
            value
            for value in outputs
            if isinstance(
                value,
                torch.Tensor,
            )
        ]
    else:
        tensors = []

    if not tensors:
        print(
            "No tensor output was returned."
        )
        return

    for tensor_index, tensor in enumerate(
        tensors
    ):
        values = (
            tensor.detach()
            .to(
                device="cpu",
                dtype=torch.float32,
            )
            .numpy()
        )

        print(
            f"Tensor {tensor_index}: "
            f"shape={values.shape}, "
            f"minimum={values.min():.6f}, "
            f"maximum={values.max():.6f}"
        )

        if (
            values.ndim == 3
            and values.shape[0] == 1
        ):
            values = values[0]

        if values.ndim == 2:
            for speaker_index in range(
                values.shape[1]
            ):
                speaker_values = (
                    values[:, speaker_index]
                )

                print(
                    f"  Speaker {speaker_index + 1}: "
                    f"max={speaker_values.max():.6f}, "
                    f"frames>=0.50="
                    f"{np.count_nonzero(speaker_values >= 0.50)}"
                )


with wave.open(
    AUDIO_PATH,
    "rb",
) as audio_file:
    channels = audio_file.getnchannels()
    sample_width = audio_file.getsampwidth()
    sample_rate = audio_file.getframerate()

    if channels != 1:
        raise RuntimeError(
            "The AMI test clip must be mono."
        )

    if sample_width != 2:
        raise RuntimeError(
            "The AMI test clip must be PCM16."
        )

    frame_count = (
        sample_rate
        * TEST_DURATION_SECONDS
    )

    audio_bytes = audio_file.readframes(
        frame_count
    )

audio_float = (
    np.frombuffer(
        audio_bytes,
        dtype="<i2",
    )
    .astype(np.float32)
    / 32768.0
)

print("Loading Sortformer...")

model = build_sortformer_model()

preprocessor = (
    StreamingSortformerAudioPreprocessor(
        input_sample_rate=sample_rate,
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

bytes_per_chunk = int(
    sample_rate
    * INPUT_CHUNK_DURATION_SECONDS
    * sample_width
)

batches = []

try:
    for offset in range(
        0,
        len(audio_bytes),
        bytes_per_chunk,
    ):
        windows = preprocessor.push_pcm16(
            audio_bytes[
                offset:
                offset + bytes_per_chunk
            ]
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

streaming_probabilities = np.concatenate(
    [
        batch.probabilities
        for batch in batches
        if batch.frame_count > 0
    ],
    axis=0,
)

print()
print("Streaming path")
print("--------------")
print("Batches:", len(batches))
print(
    "Frames:",
    streaming_probabilities.shape[0],
)
print(
    "Represented duration:",
    round(
        streaming_probabilities.shape[0]
        * PREDICTION_FRAME_DURATION_SECONDS,
        3,
    ),
)

print_probability_summary(
    streaming_probabilities
)

print()
print("Running NeMo diarize() on the same 30 seconds...")

try:
    with torch.inference_mode():
        segments, tensor_outputs = model.diarize(
            audio=[audio_float],
            sample_rate=sample_rate,
            batch_size=1,
            include_tensor_outputs=True,
            num_workers=0,
            verbose=False,
        )

    print()
    print("Offline detected segments")
    print("-------------------------")

    if segments and segments[0]:
        for segment in segments[0][:30]:
            print(segment)
    else:
        print("No offline segments detected.")

    describe_offline_outputs(
        tensor_outputs
    )
except Exception as exception:
    print()
    print(
        "Offline comparison failed:",
        repr(exception),
    )
