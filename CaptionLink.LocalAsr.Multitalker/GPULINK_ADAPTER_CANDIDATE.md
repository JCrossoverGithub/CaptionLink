# CaptionLink Multitalker → GPULink: adapter implementation candidate

**Status: experimental / offline-protocol tests only. Not GPU-accepted, not deployed.**

This add-only candidate adapts the *existing* CaptionLink `multitalker_service.py` model implementation to GPULink's operator-approved, subprocess GLIP v1 IPC path. The model source remains unchanged, and GPULink Core is not modified.

## Responsibilities

- GPULink Core: authenticated session, worker/GPU placement, trusted catalog, GLIP supervisor, opaque message relay.
- This adapter: strict GLIP HELLO/READY, application START, ASR1 PCM16LE sequence validation, actual post-load model-ready event, speaker-tagged transcript/activity forwarding, explicit END/exit acknowledgment.
- CaptionLink: existing audio capture, speaker-aware captions and UX; a new GPULink-backed provider is still required.
- Operator: approve and pin both Python files, all dependencies, model assets and GPU environment before enabling worker catalog entry.

## Application message draft (not public stable protocol)

After GLIP READY, the application sends a text message:

```json
{"type":"captionlink.multitalker.start.v0","sample_rate":16000,"channels":1,"bits_per_sample":16}
```

Only after the actual models load and `LiveMultitalkerSession` initializes does the adapter emit:

```json
{"type":"captionlink.multitalker.model_ready.v0","sample_rate":16000,"max_speakers":4}
```

Subsequent binary application messages contain `ASR1` + little-endian sequence, sample offset, sample count, and mono 16-kHz PCM16LE, as in GPULink's accepted ASR framing. The adapter preserves original CaptionLink `transcript` and `speaker_activity` message fields, revisions and overlapping speaker time ranges, without changing GPULink Core. The adapter accepts GLIP END only as a genuine terminal signal and returns GLIP END only after `session.finish()` completes. With `explicit-text-v1` enabled, the GPULink bridge translates the reserved client text `{"type":"gpulink.session.end.v1"}` into GLIP END and issues its own completion acknowledgment after clean exit.

## Critical limitations / integration blockers

1. GPULink's current operator-approved subprocess supervisor defaults to a 30-second session deadline (maximum 300 seconds) and cumulative output quotas. This does **not** yet support the proposed 10-minute streaming acceptance test. The lifetime/backpressure/cleanup policy must be improved generically, with tests, before a sustained integration is accepted.
2. The current Adapter SDK packer bundles **one Python entry file**, but this adapter loads its approved sibling `multitalker_service.py` by fixed path at runtime. The operator must separately pin and install the sibling asset and all underlying model dependencies; the example package recipe by itself **is not dependency-complete**. `-I -B` invocation is supported by the fixed-path import. This is an explicit packaging gap, not a reason to duplicate the model implementation.
3. The worker's subprocess environment is minimal and does not supply a user-chosen `HOME`, model cache, or Python dependency locations. A reviewed Python/NeMo runtime and pinned/offline model artifacts must be proven compatible; model download/update during admitted sessions must not be assumed safe.
4. This candidate does **not** emit authoritative GPULink ASR consumption credits: the existing model buffers audio and processes in larger frames. Credit semantics must be proven at the actual inference boundary before connecting a credit-based ASR coordinator. The generic opaque bridge does not reinterpret these messages.
5. No new C# GPULink transcription provider is included. CaptionLink's current remote provider must remain untouched until a speaker-aware GPULink provider is verified.
6. Worker catalog activation, physical CUDA/NeMo inference, model artifact checksums, performance, cancellation under GPU load, resource cleanup, full GPULink CI, and inter-machine CaptionLink UX **have not been tested** by this candidate.
7. This adapter uses one model instance per approved session. An operator-controlled warm model service may be useful later, but adding it prematurely would create an unnecessary lifecycle and authorization system.

## Verification (offline, no dependencies installed)

From the CaptionLink repository root after staging on a feature branch:

```bash
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s tests -p 'test_gpulink_multitalker_adapter.py' -v
```

The tests use a deliberately fake local model module, checking GLIP framing, two simultaneously active speaker segments, final revisions, strict audio sequencing, explicit completion, invalid START and missing GPU assignment. These are not evidence of real Multitalker inference.

Do **not** adopt the example catalog/recipe on a real worker without separately verifying every imported artifact, model license, offline weights and runtime security limits. Do not push, merge or deploy on the strength of these tests.
