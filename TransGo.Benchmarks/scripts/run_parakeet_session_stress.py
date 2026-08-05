#!/usr/bin/env python3
from __future__ import annotations

import argparse
import asyncio
import json
import re
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import jiwer
import numpy as np
import soundfile as sf
from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosedError, ConnectionClosedOK


def parse_args() -> argparse.Namespace:
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="Repeat one audio clip through independent Parakeet sessions."
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=root / "manifests" / "librispeech-test-clean-v1.json",
    )
    parser.add_argument("--clip-id", default=None)
    parser.add_argument("--sessions", type=int, default=20)
    parser.add_argument("--chunk-ms", type=int, default=100)
    parser.add_argument("--pace", type=float, default=1.0)
    parser.add_argument("--pause-seconds", type=float, default=0.0)
    parser.add_argument("--ws-url", default="ws://localhost:8765/stream")
    parser.add_argument("--health-url", default="http://localhost:8765/health")
    parser.add_argument("--output", type=Path, default=None)
    return parser.parse_args()


def normalize(text: str) -> str:
    text = text.casefold().replace("_", " ")
    text = re.sub(r"[^\w']+", " ", text)
    return " ".join(text.split())


def health(url: str) -> dict[str, Any] | None:
    try:
        with urllib.request.urlopen(url, timeout=3) as response:
            return json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError):
        return None


def read_manifest(
    path: Path,
    clip_id: str | None,
) -> tuple[dict[str, Any], dict[str, Any]]:
    manifest = json.loads(path.read_text(encoding="utf-8"))
    items = list(manifest.get("items", []))

    if not items:
        raise RuntimeError("Manifest contains no clips.")

    if clip_id is None:
        return manifest, items[0]

    for item in items:
        if item.get("id") == clip_id:
            return manifest, item

    raise ValueError(f"Clip {clip_id!r} was not found.")


def load_audio(path: Path) -> tuple[np.ndarray, int]:
    samples, rate = sf.read(path, dtype="int16", always_2d=True)

    if samples.shape[1] == 1:
        mono = samples[:, 0]
    else:
        mono = np.mean(samples.astype(np.int32), axis=1)
        mono = np.clip(np.rint(mono), -32768, 32767).astype(np.int16)

    return np.asarray(mono, dtype="<i2"), int(rate)


async def wait_for_type(websocket, expected: str) -> dict[str, Any]:
    while True:
        raw = await websocket.recv()

        if not isinstance(raw, str):
            continue

        message = json.loads(raw)
        kind = message.get("type")

        if kind == "error":
            raise RuntimeError(str(message.get("message", "Unknown service error.")))

        if kind == expected:
            return message


async def receive_until_close(
    websocket,
    started_at: float,
) -> dict[str, Any]:
    finals: list[str] = []
    latest_partial = ""
    first_partial = None
    first_final = None
    last_transcript = None
    stopped = None
    errors: list[str] = []

    try:
        async for raw in websocket:
            if not isinstance(raw, str):
                continue

            message = json.loads(raw)
            kind = message.get("type")

            if kind == "transcript":
                elapsed = time.perf_counter() - started_at
                text = str(message.get("text", "")).strip()
                is_final = bool(message.get("is_final"))
                last_transcript = elapsed

                if is_final:
                    if first_final is None:
                        first_final = elapsed
                    if text:
                        finals.append(text)
                    latest_partial = ""
                elif text:
                    if first_partial is None:
                        first_partial = elapsed
                    latest_partial = text

            elif kind == "stopped":
                stopped = message
            elif kind == "error":
                errors.append(str(message.get("message", "Unknown service error.")))

    except ConnectionClosedOK:
        pass
    except ConnectionClosedError:
        raise

    hypothesis = " ".join(finals).strip() or latest_partial

    return {
        "hypothesis": hypothesis,
        "first_partial_seconds": first_partial,
        "first_final_seconds": first_final,
        "last_transcript_seconds": last_transcript,
        "stopped_message": stopped,
        "service_errors": errors,
    }


async def run_session(
    *,
    ws_url: str,
    samples: np.ndarray,
    sample_rate: int,
    chunk_ms: int,
    pace: float,
    reference: str,
) -> dict[str, Any]:
    samples_per_chunk = max(1, round(sample_rate * chunk_ms / 1000))

    async with connect(
        ws_url,
        open_timeout=15,
        close_timeout=15,
        max_size=4 * 1024 * 1024,
    ) as websocket:
        connected = await wait_for_type(websocket, "connected")

        await websocket.send(
            json.dumps(
                {
                    "type": "start",
                    "sample_rate": sample_rate,
                    "channels": 1,
                    "bits_per_sample": 16,
                }
            )
        )

        started = await wait_for_type(websocket, "started")
        audio_started = time.perf_counter()
        receiver = asyncio.create_task(
            receive_until_close(websocket, audio_started)
        )

        for index, start in enumerate(range(0, len(samples), samples_per_chunk)):
            chunk = samples[start : start + samples_per_chunk]
            await websocket.send(chunk.tobytes())

            if pace > 0:
                target = (index + 1) * chunk_ms / 1000 / pace
                delay = target - (time.perf_counter() - audio_started)
                if delay > 0:
                    await asyncio.sleep(delay)

        await websocket.send(json.dumps({"type": "stop"}))
        received = await asyncio.wait_for(receiver, timeout=30)

    hypothesis = received["hypothesis"]
    ref = normalize(reference)
    hyp = normalize(hypothesis)

    return {
        "success": True,
        "reference": reference,
        "hypothesis": hypothesis,
        "wer": jiwer.wer(ref, hyp),
        "cer": jiwer.cer(ref, hyp),
        "connected_message": connected,
        "started_message": started,
        **received,
    }


def save(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")


async def main() -> int:
    args = parse_args()

    if args.sessions <= 0:
        raise ValueError("--sessions must be greater than zero.")

    benchmark_root = Path(__file__).resolve().parents[1]
    manifest_path = args.manifest.resolve()
    manifest, item = read_manifest(manifest_path, args.clip_id)
    audio_path = benchmark_root / Path(str(item["audio_path"]))

    if not audio_path.is_file():
        raise FileNotFoundError(audio_path)

    initial_health = health(args.health_url)

    if initial_health is None or initial_health.get("status") != "ready":
        raise RuntimeError(
            "Parakeet is not ready. Open TransGo, wait for the local "
            "service to load, and do not click Start Listening."
        )

    samples, sample_rate = load_audio(audio_path)
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    output = (
        args.output.resolve()
        if args.output
        else benchmark_root
        / "results"
        / "raw"
        / f"parakeet-session-stress-{item['id']}-{timestamp}.json"
    )

    payload: dict[str, Any] = {
        "schema_version": 1,
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "test": "repeated_websocket_sessions",
        "clip": {
            "id": item["id"],
            "audio_path": item["audio_path"],
            "reference": item["reference"],
            "sample_rate": sample_rate,
            "duration_seconds": round(len(samples) / sample_rate, 3),
        },
        "configuration": {
            "sessions": args.sessions,
            "chunk_ms": args.chunk_ms,
            "pace": args.pace,
            "pause_seconds": args.pause_seconds,
            "profile": initial_health.get("profile"),
            "model": initial_health.get("model"),
            "gpu": initial_health.get("gpu"),
        },
        "sessions": [],
        "summary": {
            "completed_sessions": 0,
            "failed_session": None,
            "all_passed": False,
        },
    }
    save(output, payload)

    print(
        f"Service ready: profile={initial_health.get('profile')}, "
        f"GPU={initial_health.get('gpu')}"
    )
    print(
        f"Repeating clip {item['id']} across "
        f"{args.sessions} independent sessions."
    )
    print(f"Progress file: {output}\n")

    for number in range(1, args.sessions + 1):
        print(f"[{number}/{args.sessions}] ...", end=" ", flush=True)
        before = health(args.health_url)

        try:
            result = await run_session(
                ws_url=args.ws_url,
                samples=samples,
                sample_rate=sample_rate,
                chunk_ms=args.chunk_ms,
                pace=args.pace,
                reference=str(item["reference"]),
            )
            result["session_number"] = number
            result["health_before"] = before
            result["health_after"] = health(args.health_url)
            payload["sessions"].append(result)
            payload["summary"]["completed_sessions"] = number
            save(output, payload)

            print(
                f"passed, WER={result['wer']:.3f}, "
                f"first={result['first_partial_seconds']}"
            )

        except Exception as exception:
            payload["sessions"].append(
                {
                    "success": False,
                    "session_number": number,
                    "exception_type": type(exception).__name__,
                    "message": str(exception),
                    "health_before": before,
                    "health_after": health(args.health_url),
                }
            )
            payload["summary"]["failed_session"] = number
            save(output, payload)

            print(f"FAILED: {type(exception).__name__}: {exception}")
            print(
                f"\nFailure occurred on session {number} after "
                f"{number - 1} successful sessions."
            )
            print(f"Saved: {output}")
            return 1

        if args.pause_seconds > 0 and number < args.sessions:
            await asyncio.sleep(args.pause_seconds)

    payload["summary"]["all_passed"] = True
    save(output, payload)

    print(f"\nAll {args.sessions} sessions completed successfully.")
    print(f"Saved: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
