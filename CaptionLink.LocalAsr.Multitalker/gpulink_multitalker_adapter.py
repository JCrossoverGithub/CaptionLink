#!/usr/bin/env python3
"""Operator-approved GPULink GLIP v1 adapter for CaptionLink Multitalker.

Candidate, not production-accepted. Importing the actual NeMo inference service is
DEFERRED until an application START frame, after the GLIP READY handshake.
Application data uses PCM16LE/ASR1 frames and CaptionLink's existing speaker
transcript/activity events. GPULink Core never interprets those events.

The operator must approve this script, the imported service, Python environment,
model weights and GPU device. This is NOT an untrusted-code sandbox.
"""
from __future__ import annotations

import importlib.util
import hashlib
import hmac
import json
import math
from pathlib import Path
import os
import re
import struct
import sys

MAGIC = b"GLIP"
FRAME_LIMIT = 65_536
HELLO, READY, BINARY, TEXT, END, ERROR, CANCEL = range(1, 8)
START_TYPE = "captionlink.multitalker.start.v0"
MODEL_READY_TYPE = "captionlink.multitalker.model_ready.v0"
MAX_AUDIO_SECONDS = 600
SAMPLE_RATE = 16_000
MAX_AUDIO_SAMPLES = MAX_AUDIO_SECONDS * SAMPLE_RATE
SESSION_ID_RE = re.compile(r"[A-Za-z0-9_-]{1,128}\Z")
GPU_UUID_RE = re.compile(r"GPU-[A-Fa-f0-9-]{32,64}\Z")


def exact_read(stream, count: int) -> bytes:
    buf = bytearray()
    while len(buf) < count:
        part = stream.read(count - len(buf))
        if not part:
            raise EOFError("truncated GLIP frame")
        buf.extend(part)
    return bytes(buf)


def read_frame(stream):
    lead = stream.read(9)
    if not lead:
        return None
    if len(lead) < 9:
        lead += exact_read(stream, 9 - len(lead))
    if lead[:4] != MAGIC:
        raise ValueError("invalid GLIP magic")
    kind, size = struct.unpack(">BI", lead[4:])
    if kind not in (HELLO, READY, BINARY, TEXT, END, ERROR, CANCEL) or size > FRAME_LIMIT:
        raise ValueError("invalid GLIP frame")
    return kind, exact_read(stream, size)


def write_frame(wire, kind: int, payload: bytes = b"") -> None:
    if len(payload) > FRAME_LIMIT:
        raise ValueError("GLIP output frame too large")
    wire.write(MAGIC + struct.pack(">BI", kind, len(payload)) + payload)
    wire.flush()


def emit(wire, obj: dict) -> None:
    data = json.dumps(obj, separators=(",", ":"), allow_nan=False).encode("utf-8")
    write_frame(wire, TEXT, data)


def parse_json(data: bytes) -> dict:
    result = json.loads(data.decode("utf-8", errors="strict"))
    if not isinstance(result, dict):
        raise ValueError("object required")
    return result


def verify_hello(data: bytes) -> None:
    hello = parse_json(data)
    if (set(hello) != {"protocol", "version", "sessionId"} or
            hello["protocol"] != "gpulink.adapter.ipc" or
            type(hello["version"]) is not int or hello["version"] != 1 or
            not isinstance(hello["sessionId"], str) or
            not SESSION_ID_RE.fullmatch(hello["sessionId"])):
        raise ValueError("invalid GLIP HELLO")


def verify_start(data: bytes) -> None:
    start = parse_json(data)
    if (set(start) != {"type", "sample_rate", "channels", "bits_per_sample"} or
            start["type"] != START_TYPE or
            type(start["sample_rate"]) is not int or start["sample_rate"] != SAMPLE_RATE or
            type(start["channels"]) is not int or start["channels"] != 1 or
            type(start["bits_per_sample"]) is not int or start["bits_per_sample"] != 16):
        raise ValueError("invalid multitalker start")


def parse_asr1(data: bytes, expected_sequence: int, next_offset: int) -> bytes:
    if len(data) < 18 or data[:4] != b"ASR1":
        raise ValueError("invalid audio header")
    sequence, offset, samples = struct.unpack_from("<III", data, 4)
    if (samples < 1 or samples > (FRAME_LIMIT - 16) // 2 or
            len(data) != 16 + samples * 2 or
            sequence != expected_sequence or offset != next_offset or
            offset + samples > MAX_AUDIO_SAMPLES or
            sequence == 0xFFFFFFFF):
        raise ValueError("invalid audio sequence/length")
    return data[16:]


def validate_update(update: dict) -> None:
    if not isinstance(update, dict):
        raise ValueError("invalid multitalker output")
    if update.get("type") not in ("transcript", "speaker_activity"):
        raise ValueError("unknown multitalker output")
    if not isinstance(update.get("speaker_id"), str) or not re.fullmatch(r"speaker-[1-4]", update["speaker_id"]):
        raise ValueError("invalid speaker ID")
    if type(update.get("sequence")) is not int or update["sequence"] <= 0:
        raise ValueError("invalid output sequence")
    if type(update.get("is_final")) is not bool:
        raise ValueError("invalid final flag")
    if update["type"] == "transcript":
        if (not isinstance(update.get("segment_id"), str) or len(update["segment_id"]) > 160 or
                not isinstance(update.get("text"), str) or len(update["text"]) > 8192):
            raise ValueError("invalid transcript")
        start = update.get("result_start_time_seconds")
        end = update.get("result_end_time_seconds")
    else:
        if (not isinstance(update.get("activity_id"), str) or len(update["activity_id"]) > 160):
            raise ValueError("invalid activity")
        start = update.get("start_time_seconds")
        end = update.get("end_time_seconds")
    if (type(start) not in (int, float) or type(end) not in (int, float) or
            not math.isfinite(start) or not math.isfinite(end) or start < 0 or end < start):
        raise ValueError("invalid event timestamps")


class OutputTracker:
    """Preserve per-stream revisions; overlapping speakers are explicitly legal."""

    def __init__(self):
        self.last_sequence = {"transcript": 0, "speaker_activity": 0}
        self.segments = {}
        self.activities = {}

    def check(self, event: dict) -> None:
        validate_update(event)
        typ = event["type"]
        if event["sequence"] <= self.last_sequence[typ]:
            raise ValueError("non-monotonic output sequence")
        key = event["segment_id"] if typ == "transcript" else event["activity_id"]
        seen = self.segments if typ == "transcript" else self.activities
        state = seen.get(key)
        if state is not None and (state[0] != event["speaker_id"] or state[1]):
            raise ValueError("speaker identity changed or final event revised")
        seen[key] = (event["speaker_id"], event["is_final"])
        self.last_sequence[typ] = event["sequence"]


def publish(wire, tracker: OutputTracker, updates: list[dict]) -> None:
    for update in updates:
        tracker.check(update)
        emit(wire, update)


def verified_local_checkpoint(path_key: str, hash_key: str) -> str:
    # These are operator-approved values, not application payloads.
    filename = os.environ.get(path_key, "")
    expected = os.environ.get(hash_key, "")
    if (not filename or not Path(filename).is_absolute() or
            not re.fullmatch(r"[a-f0-9]{64}", expected)):
        raise ValueError("missing approved checkpoint lock")
    local = Path(filename).resolve(strict=True)
    if not local.is_file():
        raise ValueError("approved checkpoint is not a regular file")
    digest = hashlib.sha256()
    with local.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    if not hmac.compare_digest(digest.hexdigest(), expected):
        raise ValueError("approved checkpoint checksum mismatch")
    return str(local)


def load_session():
    # Verify the exact operator-selected checkpoint bytes BEFORE loading NeMo.
    asr_checkpoint = verified_local_checkpoint(
        "GPULINK_ASR_CHECKPOINT", "GPULINK_ASR_SHA256")
    diar_checkpoint = verified_local_checkpoint(
        "GPULINK_DIAR_CHECKPOINT", "GPULINK_DIAR_SHA256")
    # The real service's model builder is the source of truth for CUDA/NeMo.
    # Import AFTER READY: torch/nemo imports can exceed GLIP startup timeout.
    # stdout is redirected at the OS descriptor level by main(), so native
    # library log output cannot corrupt binary GLIP stdout framing.
    # Python -I (used by GPULink's package workflow) intentionally excludes
    # the script directory from sys.path. Load only our fixed operator-approved
    # sibling module, never an application-supplied arbitrary module path.
    service_file = Path(__file__).resolve().with_name("multitalker_service.py")
    spec = importlib.util.spec_from_file_location("gpulink_multitalker_service", service_file)
    if spec is None or spec.loader is None:
        raise ImportError("approved Multitalker service unavailable")
    svc = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(svc)
    asr_model, diar_model = svc.build_models(
        asr_checkpoint=asr_checkpoint, diar_checkpoint=diar_checkpoint)
    session = svc.LiveMultitalkerSession(asr_model, diar_model)
    return svc, session


def run(stdin, wire) -> int:
    hello = read_frame(stdin)
    if hello is None or hello[0] != HELLO:
        raise ValueError("GLIP HELLO required")
    verify_hello(hello[1])
    write_frame(wire, READY, b'{"protocol":"gpulink.adapter.ipc","version":1,"ready":true}')

    incoming = read_frame(stdin)
    if incoming is None or incoming[0] != TEXT:
        raise ValueError("multitalker START required")
    verify_start(incoming[1])
    gpu_uuid = os.environ.get("CUDA_VISIBLE_DEVICES", "")
    if not GPU_UUID_RE.fullmatch(gpu_uuid):
        raise ValueError("operator-assigned CUDA device required")

    svc, session = load_session()
    emit(wire, {"type": MODEL_READY_TYPE, "sample_rate": SAMPLE_RATE,
                "max_speakers": svc.MAX_SPEAKERS})
    sequence = 0
    offset = 0
    tracker = OutputTracker()
    # A session has its own pinned GPU process. The GPULink supervisor owns
    # timeout, cancellation, and process/lease failure policy.
    while True:
        item = read_frame(stdin)
        if item is None:
            raise EOFError("END required for successful completion")
        kind, payload = item
        if kind == CANCEL:
            return 2
        if kind == END:
            if payload:
                raise ValueError("END cannot have a payload")
            publish(wire, tracker, session.finish())
            write_frame(wire, END)
            return 0
        if kind != BINARY:
            raise ValueError("unexpected multitalker message")
        pcm = parse_asr1(payload, sequence, offset)
        count = len(pcm) // 2
        # Model sample rate is fixed at 16 kHz, so no resampler is needed.
        import numpy as np
        audio = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
        publish(wire, tracker, session.accept_audio(audio))
        sequence += 1
        offset += count


def main() -> int:
    # Save the original pipe for protocol output, then redirect process-wide
    # stdout to /dev/null. NeMo/CUDA/native stdout must never corrupt GLIP.
    original = os.dup(sys.stdout.fileno())
    devnull = os.open(os.devnull, os.O_WRONLY)
    os.dup2(devnull, sys.stdout.fileno())
    os.close(devnull)
    with os.fdopen(original, "wb", buffering=0) as wire:
        try:
            return run(sys.stdin.buffer, wire)
        except (EOFError, ValueError, TypeError, KeyError, ImportError, RuntimeError, OSError):
            # No secrets, payloads, paths, or internal exception messages emitted.
            return 2


if __name__ == "__main__":
    sys.exit(main())
