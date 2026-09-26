#!/usr/bin/env bash

set -euo pipefail

export PATH="$HOME/.local/bin:$PATH"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLCHAIN_FILE="$REPO_ROOT/config/nemo-toolchain.conf"

if [[ ! -f "$TOOLCHAIN_FILE" ]]; then
    echo "Missing toolchain configuration: $TOOLCHAIN_FILE" >&2
    exit 1
fi

# shellcheck disable=SC1090
source "$TOOLCHAIN_FILE"

TRANSGO_DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/transgo"
NEMO_DIR="$TRANSGO_DATA_DIR/nemo-speech"
HF_TRANSFORMERS_DIR="$TRANSGO_DATA_DIR/transformers-nemotron-main"
HF_DEPS_DIR="$TRANSGO_DATA_DIR/transformers-nemotron-deps"
HF_OVERLAY_REQUIREMENTS="$REPO_ROOT/config/hf-nemotron-overlay-requirements.txt"

echo "CaptionLink WSL bootstrap"
echo "====================="
echo
echo "Data directory: $TRANSGO_DATA_DIR"
echo "NeMo directory: $NEMO_DIR"
echo "NeMo commit:    $NEMO_SPEECH_COMMIT"
echo "Transformers commit: $HF_TRANSFORMERS_COMMIT"
echo "HF overlay:     $HF_DEPS_DIR"
echo

for command_name in git curl python3 uv nvidia-smi; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        exit 1
    fi
done

mkdir -p "$TRANSGO_DATA_DIR"

if [[ ! -f "$HF_OVERLAY_REQUIREMENTS" ]]; then
    echo "Missing HF overlay requirements: $HF_OVERLAY_REQUIREMENTS" >&2
    exit 1
fi

if [[ ! -d "$NEMO_DIR/.git" ]]; then
    echo "Cloning NVIDIA NeMo Speech..."
    git clone "$NEMO_SPEECH_REPOSITORY" "$NEMO_DIR"
fi

if [[ ! -d "$HF_TRANSFORMERS_DIR/.git" ]]; then
    echo "Cloning Hugging Face Transformers..."
    git clone "$HF_TRANSFORMERS_REPOSITORY" "$HF_TRANSFORMERS_DIR"
fi

cd "$NEMO_DIR"

echo
echo "Fetching pinned NeMo commit..."
git fetch origin "$NEMO_SPEECH_COMMIT"
git checkout --detach "$NEMO_SPEECH_COMMIT"

echo
echo "Synchronizing pinned Python/CUDA environment..."
uv sync \
    --locked \
    --python "$NEMO_PYTHON_VERSION" \
    --extra "$NEMO_COLLECTION_EXTRA" \
    --extra "$NEMO_CUDA_EXTRA"

PYTHON="$NEMO_DIR/.venv/bin/python"
SERVICE_REQUIREMENTS="$REPO_ROOT/config/service-runtime-requirements.txt"

if [[ ! -f "$SERVICE_REQUIREMENTS" ]]; then
    echo "Missing service requirements: $SERVICE_REQUIREMENTS" >&2
    exit 1
fi

echo
echo "Installing CaptionLink service runtime dependencies..."
uv pip install \
    --python "$PYTHON" \
    --requirement "$SERVICE_REQUIREMENTS"

echo
echo "Fetching pinned Transformers commit..."
git -C "$HF_TRANSFORMERS_DIR" fetch origin
git -C "$HF_TRANSFORMERS_DIR" checkout --detach "$HF_TRANSFORMERS_COMMIT"

ACTUAL_HF_COMMIT="$(git -C "$HF_TRANSFORMERS_DIR" rev-parse HEAD)"

if [[ "$ACTUAL_HF_COMMIT" != "$HF_TRANSFORMERS_COMMIT" ]]; then
    echo "Transformers checkout does not match pinned commit." >&2
    echo "Expected: $HF_TRANSFORMERS_COMMIT" >&2
    echo "Actual:   $ACTUAL_HF_COMMIT" >&2
    exit 1
fi

echo
echo "Building pinned Nemotron HF dependency overlay..."

rm -rf "$HF_DEPS_DIR"
mkdir -p "$HF_DEPS_DIR"

uv pip install \
    --python "$PYTHON" \
    --target "$HF_DEPS_DIR" \
    --no-deps \
    --requirement "$HF_OVERLAY_REQUIREMENTS" \
    "$HF_TRANSFORMERS_DIR"

if [[ -d "$HF_DEPS_DIR/numpy" || -d "$HF_DEPS_DIR/numpy.libs" ]]; then
    echo "HF overlay unexpectedly contains NumPy." >&2
    echo "NumPy must come from the NeMo environment." >&2
    exit 1
fi

echo
echo "Validating environment..."

PYTHONNOUSERSITE=1 \
PYTHONPATH="$HF_DEPS_DIR" \
"$PYTHON" - <<'PY'
import sys

from packaging.version import Version

import fastapi
import numpy
import tokenizers
import transformers
import torch
import uvicorn

from nemo.collections.asr.models import ASRModel, SortformerEncLabelModel

from transformers import (
    AutoModelForAudioFrameClassification,
    AutoProcessor,
    Nemotron3DiarizationProcessor,
)

MODEL = "nvidia/Nemotron-3-Diarization"

print("Python:", sys.version.split()[0])
print("PyTorch:", torch.__version__)
print("CUDA runtime:", torch.version.cuda)
print("FastAPI:", fastapi.__version__)
print("Uvicorn:", uvicorn.__version__)
print("NumPy:", numpy.__version__)
print("NumPy path:", numpy.__file__)
print("Transformers:", transformers.__version__)
print("Transformers path:", transformers.__file__)
print("Tokenizers:", tokenizers.__version__)
print("CUDA available:", torch.cuda.is_available())

if not torch.cuda.is_available():
    raise SystemExit("CUDA is not available to PyTorch.")

if Version(numpy.__version__) >= Version("2.5"):
    raise SystemExit(
        "NumPy must remain below 2.5 for this runtime."
    )

if "transformers-nemotron-deps" in numpy.__file__:
    raise SystemExit(
        "NumPy is incorrectly loading from the HF overlay."
    )

if tokenizers.__version__ != "0.23.2":
    raise SystemExit(
        "Unexpected tokenizers version: "
        + tokenizers.__version__
    )

print("GPU:", torch.cuda.get_device_name(0))
print("NeMo ASR imports: OK")

processor = AutoProcessor.from_pretrained(MODEL)

if not isinstance(
    processor,
    Nemotron3DiarizationProcessor,
):
    raise SystemExit(
        "Unexpected Nemotron processor type."
    )

processor.set_streaming_mode("low_latency")

model = AutoModelForAudioFrameClassification.from_pretrained(MODEL)
model.eval()
model = model.cuda()
torch.cuda.synchronize()

print("Nemotron processor:", type(processor).__name__)
print("Nemotron model:", type(model).__name__)
print("Nemotron latency:", processor.streaming_latency_ms, "ms")
print("Nemotron first chunk:", processor.num_samples_first_audio_chunk, "samples")
print("Nemotron streaming chunk:", processor.num_samples_per_audio_chunk, "samples")
print("HF Nemotron CUDA runtime: OK")

del model
torch.cuda.empty_cache()
PY

echo
echo "Bootstrap complete."
echo
echo "Python runtime:"
echo "$PYTHON"
echo
echo "HF Transformers source:"
echo "$HF_TRANSFORMERS_DIR"
echo
echo "HF dependency overlay:"
echo "$HF_DEPS_DIR"
