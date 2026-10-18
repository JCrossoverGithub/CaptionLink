"""Offline GLIP framing and overlapping-speaker tests. No CUDA/NeMo required.

These tests use a fake local inference module; they DO NOT certify real model
execution, repository integration, GPU isolation, or performance.
"""
from __future__ import annotations

import importlib.util
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest

CANDIDATE = Path(__file__).resolve().parents[1] / "CaptionLink.LocalAsr.Multitalker" / "gpulink_multitalker_adapter.py"
SPEC = importlib.util.spec_from_file_location("gpulink_multitalker_adapter", CANDIDATE)
adapter = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(adapter)

MOCK_SERVICE = '''
# The subprocess launches with -I; fake inferencing requires no site NumPy.
import struct
import sys
import types
class _FakeInt16Array:
    def __init__(self, pcm): self._pcm = pcm
    def astype(self, dtype):
        if dtype != _offline_numpy.float32: raise ValueError("wrong dtype")
        return self
    def __truediv__(self, divisor):
        return [value[0] / divisor for value in struct.iter_unpack("<h", self._pcm)]
def _offline_frombuffer(pcm, dtype):
    if dtype != "<i2": raise ValueError("wrong PCM dtype")
    return _FakeInt16Array(pcm)
_offline_numpy = types.ModuleType("numpy")
_offline_numpy.float32 = "fake-float32"
_offline_numpy.frombuffer = _offline_frombuffer
sys.modules["numpy"] = _offline_numpy
MAX_SPEAKERS = 4

def build_models(*, asr_checkpoint=None, diar_checkpoint=None):
    if not asr_checkpoint or not diar_checkpoint:
        raise ValueError("mock requires local checkpoints")
    print("native-model-log")
    return (object(), object())

class LiveMultitalkerSession:
    def __init__(self, asr_model, diar_model):
        self.frames = 0

    def accept_audio(self, data):
        self.frames += 1
        if self.frames != 2:
            return []
        return [
          {"type":"transcript", "segment_id":"multitalker-speaker-1-000001",
           "sequence":1,"speaker_id":"speaker-1","text":"hello",
           "is_final":False,"result_start_time_seconds":0.1,
           "result_end_time_seconds":1.2,"processing_duration_milliseconds":3.0},
          {"type":"transcript", "segment_id":"multitalker-speaker-2-000001",
           "sequence":2,"speaker_id":"speaker-2","text":"hi",
           "is_final":False,"result_start_time_seconds":0.4,
           "result_end_time_seconds":1.0,"processing_duration_milliseconds":3.0}
        ]

    def finish(self):
        return [
          {"type":"transcript", "segment_id":"multitalker-speaker-1-000001",
           "sequence":3,"speaker_id":"speaker-1","text":"hello there",
           "is_final":True,"result_start_time_seconds":0.1,
           "result_end_time_seconds":1.2,"processing_duration_milliseconds":3.0},
          {"type":"speaker_activity","activity_id":"speaker-2-activity-1",
           "sequence":1,"speaker_id":"speaker-2","start_time_seconds":0.4,
           "end_time_seconds":1.0,"is_final":True,"confidence":0.8}
        ]
'''


def frame(kind, payload=b""):
    return b"GLIP" + struct.pack(">BI", kind, len(payload)) + payload


def controls():
    hello = json.dumps({"protocol":"gpulink.adapter.ipc","version":1,"sessionId":"test-session"}).encode()
    start = json.dumps({"type":"captionlink.multitalker.start.v0", "sample_rate":16000,
                        "channels":1,"bits_per_sample":16}).encode()
    return frame(1, hello) + frame(4, start)


def audio(seq, offset, count=640):
    pcm = b"\x00\x00" * count
    return frame(3, b"ASR1" + struct.pack("<III", seq, offset, count) + pcm)


def parse_output(stream):
    output = []
    at = 0
    while at < len(stream):
        if stream[at:at+4] != b"GLIP":
            raise AssertionError("stdout contains non-GLIP output")
        kind, size = struct.unpack(">BI", stream[at+4:at+9])
        payload = stream[at+9:at+9+size]
        if len(payload) != size:
            raise AssertionError("truncated output")
        output.append((kind, json.loads(payload) if kind in (2, 4) else payload))
        at += 9 + size
    return output


class MultitalkerAdapterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workspace = tempfile.TemporaryDirectory()
        cls.folder = Path(cls.workspace.name)
        shutil.copyfile(CANDIDATE, cls.folder / "gpulink_multitalker_adapter.py")
        (cls.folder / "multitalker_service.py").write_text(MOCK_SERVICE, encoding="utf-8")
        cls.asr = cls.folder / "asr.nemo"
        cls.diar = cls.folder / "diar.nemo"
        cls.asr.write_bytes(b"test asr checkpoint")
        cls.diar.write_bytes(b"test diar checkpoint")

    @classmethod
    def tearDownClass(cls):
        cls.workspace.cleanup()

    def run_adapter(self, payload, *, assigned_gpu=True, corrupt_hash=False, omit_lock=False):
        env = dict(os.environ)
        if not omit_lock:
            env["GPULINK_ASR_CHECKPOINT"] = str(self.asr)
            env["GPULINK_DIAR_CHECKPOINT"] = str(self.diar)
            env["GPULINK_ASR_SHA256"] = hashlib.sha256(self.asr.read_bytes()).hexdigest()
            env["GPULINK_DIAR_SHA256"] = hashlib.sha256(self.diar.read_bytes()).hexdigest()
            if corrupt_hash:
                env["GPULINK_ASR_SHA256"] = "0" * 64
        else:
            for key in ("GPULINK_ASR_CHECKPOINT", "GPULINK_DIAR_CHECKPOINT",
                        "GPULINK_ASR_SHA256", "GPULINK_DIAR_SHA256"):
                env.pop(key, None)
        if assigned_gpu:
            env["CUDA_VISIBLE_DEVICES"] = "GPU-" + "a" * 32
        else:
            env.pop("CUDA_VISIBLE_DEVICES", None)
        return subprocess.run([sys.executable, "-I", "-B", str(self.folder / "gpulink_multitalker_adapter.py")],
                              input=payload, capture_output=True, env=env, timeout=5)

    def test_two_overlapping_speakers_and_final_update(self):
        done = self.run_adapter(controls() + audio(0, 0) + audio(1, 640) + frame(5))
        self.assertEqual(done.returncode, 0)
        events = parse_output(done.stdout)
        self.assertEqual([k for k, _ in events], [2,4,4,4,4,4,5])
        self.assertEqual(events[1][1]["type"], "captionlink.multitalker.model_ready.v0")
        a, b = events[2][1], events[3][1]
        self.assertEqual([a["speaker_id"], b["speaker_id"]], ["speaker-1", "speaker-2"])
        self.assertLess(a["result_start_time_seconds"], b["result_end_time_seconds"])
        self.assertLess(b["result_start_time_seconds"], a["result_end_time_seconds"])
        self.assertEqual(events[4][1]["text"], "hello there")
        self.assertTrue(events[4][1]["is_final"])
        self.assertEqual(events[5][1]["type"], "speaker_activity")
        self.assertEqual(events[6][0], 5)  # final END confirmation

    def test_rejects_wrong_model_digest(self):
        done = self.run_adapter(controls() + frame(5), corrupt_hash=True)
        self.assertEqual(done.returncode, 2)
        self.assertNotIn(5, [k for k, _ in parse_output(done.stdout)])

    def test_rejects_missing_checkpoint_lock(self):
        done = self.run_adapter(controls() + frame(5), omit_lock=True)
        self.assertEqual(done.returncode, 2)
        self.assertNotIn(5, [k for k, _ in parse_output(done.stdout)])

    def test_rejects_discontinuous_audio(self):
        done = self.run_adapter(controls() + audio(0,0) + audio(2,640) + frame(5))
        self.assertEqual(done.returncode, 2)
        self.assertNotIn(5, [k for k, _ in parse_output(done.stdout)])

    def test_rejects_implicit_success_on_eof(self):
        done = self.run_adapter(controls() + audio(0,0))
        self.assertEqual(done.returncode, 2)
        self.assertNotIn(5, [k for k, _ in parse_output(done.stdout)])

    def test_requires_worker_gpu_assignment(self):
        done = self.run_adapter(controls() + frame(5), assigned_gpu=False)
        self.assertEqual(done.returncode, 2)
        self.assertEqual([k for k, _ in parse_output(done.stdout)], [2])

    def test_rejects_invalid_start_contract(self):
        wrong = frame(1, json.dumps({"protocol":"gpulink.adapter.ipc","version":1,"sessionId":"s"}).encode())
        wrong += frame(4, b'{"type":"captionlink.multitalker.start.v0","sample_rate":48000,"channels":1,"bits_per_sample":16}')
        done = self.run_adapter(wrong)
        self.assertEqual(done.returncode, 2)
        self.assertNotIn(5, [k for k, _ in parse_output(done.stdout)])

    def test_rejects_out_of_bounds_speaker_or_timestamp(self):
        sample = {"type":"transcript","sequence":1,"speaker_id":"speaker-5",
                  "segment_id":"x","text":"hello","is_final":False,
                  "result_start_time_seconds":2.,"result_end_time_seconds":1.}
        with self.assertRaises(ValueError):
            adapter.validate_update(sample)

    def test_rejects_oversized_or_malformed_pcm(self):
        with self.assertRaises(ValueError):
            adapter.parse_asr1(b"ASR1" + struct.pack("<III", 0,0,640) + b"\0\0",0,0)


if __name__ == "__main__":
    unittest.main()
