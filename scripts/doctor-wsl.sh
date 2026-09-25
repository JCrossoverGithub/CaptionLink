#!/usr/bin/env bash

set -euo pipefail

export PATH="$HOME/.local/bin:$PATH"

echo
echo "[Linux]"
grep '^PRETTY_NAME=' /etc/os-release | cut -d= -f2- | tr -d '"'

echo
echo "[Kernel]"
uname -r

echo
echo "[Python]"
python3 --version

echo
echo "[uv]"
if command -v uv >/dev/null 2>&1; then
    uv --version
else
    echo "uv: NOT FOUND"
    exit 1
fi

echo
echo "[NVIDIA / WSL]"
nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader

echo
echo "[Required commands]"
for command_name in git curl ffmpeg python3 uv; do
    if command -v "$command_name" >/dev/null 2>&1; then
        echo "OK  $command_name"
    else
        echo "MISSING  $command_name"
        exit 1
    fi
done

echo
echo "WSL environment OK."

echo
echo "[NeMo runtime]"

NEMO_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/transgo/nemo-speech"
NEMO_PYTHON="$NEMO_DIR/.venv/bin/python"

if [[ -x "$NEMO_PYTHON" ]]; then
    "$NEMO_PYTHON" - <<'PY'
import sys

import fastapi
import torch
import uvicorn

print("Python:", sys.version.split()[0])
print("PyTorch:", torch.__version__)
print("CUDA runtime:", torch.version.cuda)
print("FastAPI:", fastapi.__version__)
print("Uvicorn:", uvicorn.__version__)
print("CUDA available:", torch.cuda.is_available())

if torch.cuda.is_available():
    print("GPU:", torch.cuda.get_device_name(0))
PY
else
    echo "NeMo environment: NOT INSTALLED"
    echo "Run ./scripts/bootstrap-wsl.sh"
    exit 1
fi

echo
echo "[Nemotron HF streaming runtime]"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLCHAIN_FILE="$REPO_ROOT/config/nemo-toolchain.conf"

if [[ ! -f "$TOOLCHAIN_FILE" ]]; then
    echo "Missing toolchain configuration: $TOOLCHAIN_FILE"
    exit 1
fi

# shellcheck disable=SC1090
source "$TOOLCHAIN_FILE"

TRANSGO_DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/transgo"
HF_TRANSFORMERS_DIR="$TRANSGO_DATA_DIR/transformers-nemotron-main"
HF_DEPS_DIR="$TRANSGO_DATA_DIR/transformers-nemotron-deps"

if [[ ! -d "$HF_TRANSFORMERS_DIR/.git" ]]; then
    echo "Transformers source: NOT INSTALLED"
    echo "Run ./scripts/bootstrap-wsl.sh"
    exit 1
fi

if [[ ! -d "$HF_DEPS_DIR/transformers" ]]; then
    echo "HF dependency overlay: NOT INSTALLED"
    echo "Run ./scripts/bootstrap-wsl.sh"
    exit 1
fi

ACTUAL_HF_COMMIT="$(git -C "$HF_TRANSFORMERS_DIR" rev-parse HEAD)"

echo "Expected Transformers commit: $HF_TRANSFORMERS_COMMIT"
echo "Actual Transformers commit:   $ACTUAL_HF_COMMIT"

if [[ "$ACTUAL_HF_COMMIT" != "$HF_TRANSFORMERS_COMMIT" ]]; then
    echo "Transformers checkout does not match pinned commit."
    exit 1
fi

PYTHONNOUSERSITE=1 \
PYTHONPATH="$HF_DEPS_DIR" \
"$NEMO_PYTHON" - <<'PY'
import numpy
import tokenizers
import transformers

from packaging.version import Version
from transformers import (
    AutoProcessor,
    Nemotron3DiarizationProcessor,
)

MODEL = "nvidia/Nemotron-3-Diarization"

print("NumPy:", numpy.__version__)
print("NumPy path:", numpy.__file__)
print("Transformers:", transformers.__version__)
print("Transformers path:", transformers.__file__)
print("Tokenizers:", tokenizers.__version__)

if Version(numpy.__version__) >= Version("2.5"):
    raise SystemExit(
        "NumPy is too new for the pinned runtime."
    )

if "transformers-nemotron-deps" in numpy.__file__:
    raise SystemExit(
        "NumPy must come from the NeMo environment."
    )

if tokenizers.__version__ != "0.23.2":
    raise SystemExit(
        "Unexpected tokenizers version: "
        + tokenizers.__version__
    )

processor = AutoProcessor.from_pretrained(
    MODEL,
    local_files_only=True,
)

if not isinstance(
    processor,
    Nemotron3DiarizationProcessor,
):
    raise SystemExit(
        "Unexpected Nemotron processor type."
    )

processor.set_streaming_mode("low_latency")

print("Processor:", type(processor).__name__)
print("Streaming latency:", processor.streaming_latency_ms, "ms")
print("HF Nemotron runtime: OK")
PY
