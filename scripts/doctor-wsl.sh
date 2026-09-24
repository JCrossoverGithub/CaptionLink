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
