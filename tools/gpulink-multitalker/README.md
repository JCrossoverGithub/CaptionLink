# CaptionLink Multitalker — GPULink operator review #03 candidate

**Status:** review only. Trusted, explicitly operator-approved external adapter. Not an installer, signed release, sandbox, or a production catalog activation.

The existing GPULink Adapter SDK author bundle remains a single Python adapter program. The operator bundle retains a separate pinned sibling service, launcher, and reviewed catalog. GPULink Core stays model-agnostic; the current GPU-worker bounded-session feature branch supports a strictly operator-selected subprocess lifetime without letting clients select execution timeouts.

## Accepted evidence before review #03

- Review #02's exact adapter, service, and model checkpoint SHA-256 values passed the physical-GPU worker gate.
- GPULink feature branch `feature/operator-bounded-adapter-sessions`, based on `48d10e97206ce94b2b6fbc3b750d6a7e6c3cb815`, passed 43 focused tests and a Core run reporting 496 pass / 0 fail / 34 PostgreSQL-dependent skips.
- A temporary derived catalog (review #02 assets, `executionLimits.sessionTimeoutMs=720000`) passed a 42-second paced worker canary and a 600-second paced worker gate: 3022 frames, two speakers, explicit completion, and live checks through 540 seconds. Repeated speech is **not** a natural ten-minute accuracy evaluation.
- These tests used one physical GPU and loopback data plane; no cross-machine or isolation acceptance is implied.

## Generate review #03

Required repository state:

- CaptionLink `feature/gpulink-multitalker-adapter`, HEAD `d467c5b70de4dc6df84ff3014c2523c730ee6951`, one tracked modified service file plus the seven expected untracked adapter/review files.
- GPULink `feature/operator-bounded-adapter-sessions`, HEAD `48d10e97206ce94b2b6fbc3b750d6a7e6c3cb815`, exactly four modified files from the bounded-session candidate, no staged or untracked files.
- Local CUDA/Python model runtime and operator-owned checkpoint files. Choose a fresh output directory.

The packager now requires an explicit operator session timeout. For this accepted Multitalker profile, use 720000 milliseconds (12 minutes), under the platform's 900000-millisecond upper bound. A value below 30000 or above 900000, a fractional value, or a string in the catalog is rejected. Omitting the CLI flag is rejected for review #03.

```bash
CL="$HOME/projects/CaptionLink"
GP="$HOME/projects/gpulink"
HF="$HOME/.cache/huggingface"
PY="$HOME/.local/share/transgo/nemo-speech/.venv/bin/python"
ASR="$HF/hub/models--nvidia--multitalker-parakeet-streaming-0.6b-v1/snapshots/8749fc71fd6e2d88ef230159bbf2aea69b524ee1/multitalker-parakeet-streaming-0.6b-v1.nemo"
DIAR="$HF/hub/models--nvidia--Nemotron-3-Diarization/snapshots/f667ed73aee57d40cc39428eb768b4fd87a0a29e/Nemotron-3-Diarization.nemo"
OUT="$HOME/GPULink_Multitalker_Operator_Review_03"

node "$CL/tools/gpulink-multitalker/prepare-operator-review.mjs" \
  --captionlink "$CL" --gpulink "$GP" --out "$OUT" \
  --python "$PY" --hf-home "$HF" --driver-dir /usr/lib/wsl/lib \
  --asr-checkpoint "$ASR" --diar-checkpoint "$DIAR" \
  --session-timeout-ms 720000
```

The catalog now includes `executionLimits:{sessionTimeoutMs:720000}` inside its single operator-approved adapter entry. The receipt records the value plus SHA-256 digests of GPULink's four modified worker/test files. GPULink validates and applies the bound; it remains its normal 30-second default when a legacy catalog omits executionLimits. This tool requires explicit config only when generating review #03.

This remains a locally generated review artifact referencing operator-machine absolute paths; moving the output invalidates its catalog references. The builder runs SDK `pack`, `inspect`, and the approved catalog loader without installing a catalog, starting a model, or launching a worker. It does not use WSL-specific details in GPULink's public protocol; the launcher is deployment-specific.

**Limitations:** No signed supply chain; Python/CUDA/native transitive dependencies not locked; the .nemo hashes are verified at adapter load and paths pinned to `restore_from()` but protected file-system ownership still matters; no untrusted tenant isolation, cross-machine networking, or real CaptionLink C# provider acceptance. Review #03 itself still requires a physical-GPU execution gate before release/merge.

## Offline checks

```bash
node --test tools/gpulink-multitalker/test/prepare-operator-review.test.mjs
PYTHONDONTWRITEBYTECODE=1 python3 -B -m unittest discover -s tests -p 'test_gpulink_multitalker_adapter.py' -v
```
