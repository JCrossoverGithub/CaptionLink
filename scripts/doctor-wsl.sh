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
