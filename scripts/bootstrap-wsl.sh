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

echo "TransGo WSL bootstrap"
echo "====================="
echo
echo "Data directory: $TRANSGO_DATA_DIR"
echo "NeMo directory: $NEMO_DIR"
echo "NeMo commit:    $NEMO_SPEECH_COMMIT"
echo

for command_name in git curl python3 uv nvidia-smi; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        exit 1
    fi
done

mkdir -p "$TRANSGO_DATA_DIR"

if [[ ! -d "$NEMO_DIR/.git" ]]; then
    echo "Cloning NVIDIA NeMo Speech..."
    git clone "$NEMO_SPEECH_REPOSITORY" "$NEMO_DIR"
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
echo "Installing TransGo service runtime dependencies..."
uv pip install \
    --python "$PYTHON" \
    --requirement "$SERVICE_REQUIREMENTS"

echo
echo "Validating environment..."

"$PYTHON" - <<'PY'
import sys

import fastapi
import torch
import uvicorn

from nemo.collections.asr.models import ASRModel, SortformerEncLabelModel

print("Python:", sys.version.split()[0])
print("PyTorch:", torch.__version__)
print("CUDA runtime:", torch.version.cuda)
print("FastAPI:", fastapi.__version__)
print("Uvicorn:", uvicorn.__version__)
print("CUDA available:", torch.cuda.is_available())

if not torch.cuda.is_available():
    raise SystemExit("CUDA is not available to PyTorch.")

print("GPU:", torch.cuda.get_device_name(0))
print("NeMo ASR imports: OK")
PY

echo
echo "Bootstrap complete."
echo
echo "Python runtime:"
echo "$PYTHON"
