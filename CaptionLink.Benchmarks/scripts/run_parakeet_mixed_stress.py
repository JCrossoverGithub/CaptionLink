#!/usr/bin/env python3
"""
Run a deterministic mixed-clip Parakeet reliability test.

The script cycles through every clip in a benchmark manifest, opening a fresh
WebSocket transcription session for each clip. It records transcript accuracy,
latency, service health, and CUDA memory values exposed by /health. Progress is
saved after every session so failures remain diagnosable.
"""

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
    benchmark_root = Path(__file__).resolve().parents[1]

    parser = argparse.ArgumentParser(
        description=(
            "Cycle through mixed LibriSpeech clips using independent "
            "Parakeet WebSocket sessions."
        )
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=(
            benchmark_root
            / "manifests"
            / "librispeech-test-clean-v1.json"
        ),
    )
    parser.add_argument(
        "--sessions",
        type=int,
        default=60,
        help="Total independent WebSocket sessions to attempt.",
    )
    parser.add_argument(
        "--chunk-ms",
        type=int,
        default=100,
    )
    parser.add_argument(
        "--pace",
        type=float,
        default=1.0,
        help="1.0 replays in real time; 0 sends as fast as possible.",
    )
    parser.add_argument(
        "--pause-seconds",
        type=float,
        default=0.25,
        help="Delay between completed sessions.",
    )
    parser.add_argument(
        "--ws-url",
        default="ws://localhost:8765/stream",
    )
    parser.add_argument(
        "--health-url",
        default="http://localhost:8765/health",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
    )
    return parser.parse_args()


def normalize_text(text: str) -> str:
    text = text.casefold().replace("_", " ")
    text = re.sub(r"[^\w']+", " ", text, flags=re.UNICODE)
    return " ".join(text.split())


def read_health(url: str) -> dict[str, Any] | None:
    try:
        with urllib.request.urlopen(url, timeout=3) as response:
            return json.loads(response.read().decode("utf-8"))
    except (
        urllib.error.URLError,
        TimeoutError,
        json.JSONDecodeError,
    ):
        return None


def load_manifest(path: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    manifest = json.loads(path.read_text(encoding="utf-8"))
    items = list(manifest.get("items", []))

    if not items:
        raise RuntimeError("The manifest contains no benchmark clips.")

    return manifest, items


def load_pcm16_mono(path: Path) -> tuple[np.ndarray, int]:
    samples, sample_rate = sf.read(
        path,
        dtype="int16",
        always_2d=True,
    )

    if samples.shape[1] == 1:
        mono = samples[:, 0]
    else:
        mono = np.mean(
            samples.astype(np.int32),
            axis=1,
        )
        mono = np.clip(
            np.rint(mono),
            -32768,
            32767,
        ).astype(np.int16)

    return np.asarray(mono, dtype="<i2"), int(sample_rate)


async def wait_for_message_type(
    websocket,
    expected_type: str,
) -> dict[str, Any]:
    while True:
        raw_message = await websocket.recv()

        if not isinstance(raw_message, str):
            continue

        message = json.loads(raw_message)
        message_type = message.get("type")

        if message_type == "error":
            raise RuntimeError(
                str(
                    message.get(
                        "message",
                        "Unknown Parakeet service error.",
                    )
                )
            )

        if message_type == expected_type:
            return message


async def receive_until_close(
    websocket,
    audio_started_at: float,
) -> dict[str, Any]:
    final_segments: list[str] = []
    latest_partial = ""
    first_partial_seconds = None
    first_final_seconds = None
    last_transcript_seconds = None
    transcript_message_count = 0
    partial_revision_count = 0
    stopped_message = None
    service_errors: list[str] = []

    try:
        async for raw_message in websocket:
            if not isinstance(raw_message, str):
                continue

            message = json.loads(raw_message)
            message_type = message.get("type")

            if message_type == "transcript":
                elapsed = time.perf_counter() - audio_started_at
                text = str(message.get("text", "")).strip()
                is_final = bool(message.get("is_final"))

                transcript_message_count += 1
                last_transcript_seconds = elapsed

                if is_final:
                    if first_final_seconds is None:
                        first_final_seconds = elapsed

                    if text:
                        final_segments.append(text)

                    latest_partial = ""
                elif text:
                    if first_partial_seconds is None:
                        first_partial_seconds = elapsed

                    if text != latest_partial:
                        partial_revision_count += 1
                        latest_partial = text

            elif message_type == "stopped":
                stopped_message = message

            elif message_type == "error":
                service_errors.append(
                    str(
                        message.get(
                            "message",
                            "Unknown service error.",
                        )
                    )
                )

    except ConnectionClosedOK:
        pass
    except ConnectionClosedError:
        raise

    hypothesis = " ".join(final_segments).strip() or latest_partial

    return {
        "hypothesis": hypothesis,
        "first_partial_seconds": first_partial_seconds,
        "first_final_seconds": first_final_seconds,
        "last_transcript_seconds": last_transcript_seconds,
        "transcript_message_count": transcript_message_count,
        "partial_revision_count": partial_revision_count,
        "stopped_message": stopped_message,
        "service_errors": service_errors,
    }


async def run_one_session(
    *,
    websocket_url: str,
    samples: np.ndarray,
    sample_rate: int,
    reference: str,
    chunk_ms: int,
    pace: float,
) -> dict[str, Any]:
    samples_per_chunk = max(
        1,
        round(sample_rate * chunk_ms / 1000),
    )
    audio_duration_seconds = len(samples) / sample_rate
    session_started_at = time.perf_counter()

    async with connect(
        websocket_url,
        open_timeout=15,
        close_timeout=15,
        max_size=4 * 1024 * 1024,
    ) as websocket:
        connected_message = await wait_for_message_type(
            websocket,
            "connected",
        )

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

        started_message = await wait_for_message_type(
            websocket,
            "started",
        )

        audio_started_at = time.perf_counter()

        receiver_task = asyncio.create_task(
            receive_until_close(
                websocket,
                audio_started_at,
            )
        )

        for chunk_index, start in enumerate(
            range(0, len(samples), samples_per_chunk)
        ):
            chunk = samples[
                start : start + samples_per_chunk
            ]

            await websocket.send(chunk.tobytes())

            if pace > 0:
                target_elapsed = (
                    (chunk_index + 1)
                    * chunk_ms
                    / 1000
                    / pace
                )
                delay = (
                    target_elapsed
                    - (
                        time.perf_counter()
                        - audio_started_at
                    )
                )

                if delay > 0:
                    await asyncio.sleep(delay)

        await websocket.send(
            json.dumps({"type": "stop"})
        )

        received = await asyncio.wait_for(
            receiver_task,
            timeout=30,
        )

    hypothesis = str(received["hypothesis"])
    normalized_reference = normalize_text(reference)
    normalized_hypothesis = normalize_text(hypothesis)

    word_result = jiwer.process_words(
        normalized_reference,
        normalized_hypothesis,
    )

    finalization_delay_seconds = None

    if received["last_transcript_seconds"] is not None:
        finalization_delay_seconds = (
            received["last_transcript_seconds"]
            - audio_duration_seconds
        )

    return {
        "success": True,
        "wall_time_seconds": round(
            time.perf_counter() - session_started_at,
            3,
        ),
        "audio_duration_seconds": round(
            audio_duration_seconds,
            3,
        ),
        "reference": reference,
        "hypothesis": hypothesis,
        "wer": word_result.wer,
        "cer": jiwer.cer(
            normalized_reference,
            normalized_hypothesis,
        ),
        "hits": word_result.hits,
        "substitutions": word_result.substitutions,
        "deletions": word_result.deletions,
        "insertions": word_result.insertions,
        "first_partial_seconds": (
            received["first_partial_seconds"]
        ),
        "first_final_seconds": (
            received["first_final_seconds"]
        ),
        "last_transcript_seconds": (
            received["last_transcript_seconds"]
        ),
        "finalization_delay_seconds": (
            finalization_delay_seconds
        ),
        "transcript_message_count": (
            received["transcript_message_count"]
        ),
        "partial_revision_count": (
            received["partial_revision_count"]
        ),
        "connected_message": connected_message,
        "started_message": started_message,
        "stopped_message": received["stopped_message"],
        "service_errors": received["service_errors"],
    }


def save_json(
    path: Path,
    payload: dict[str, Any],
) -> None:
    path.parent.mkdir(
        parents=True,
        exist_ok=True,
    )
    path.write_text(
        json.dumps(payload, indent=2) + "\n",
        encoding="utf-8",
    )


def memory_summary(
    health_data: dict[str, Any] | None,
) -> str:
    if not health_data:
        return "health unavailable"

    allocated = health_data.get(
        "cuda_memory_allocated_mib"
    )
    reserved = health_data.get(
        "cuda_memory_reserved_mib"
    )

    if allocated is None or reserved is None:
        return "CUDA memory metrics unavailable"

    return (
        f"allocated={allocated} MiB, "
        f"reserved={reserved} MiB"
    )


async def async_main() -> int:
    args = parse_args()

    if args.sessions <= 0:
        raise ValueError(
            "--sessions must be greater than zero."
        )

    benchmark_root = Path(__file__).resolve().parents[1]
    repository_root = benchmark_root.parent
    manifest_path = args.manifest.resolve()

    if not manifest_path.is_file():
        raise FileNotFoundError(
            f"Manifest not found: {manifest_path}"
        )

    manifest, items = load_manifest(
        manifest_path
    )

    initial_health = read_health(
        args.health_url
    )

    if (
        initial_health is None
        or initial_health.get("status") != "ready"
    ):
        raise RuntimeError(
            "Parakeet is not ready. Open CaptionLink, wait for "
            "the local service to load, and do not click "
            "Start Listening."
        )

    timestamp = datetime.now().strftime(
        "%Y%m%d-%H%M%S"
    )

    output_path = (
        args.output.resolve()
        if args.output is not None
        else (
            benchmark_root
            / "results"
            / "raw"
            / (
                "parakeet-mixed-stress-"
                f"{timestamp}.json"
            )
        )
    )

    payload: dict[str, Any] = {
        "schema_version": 1,
        "created_utc": datetime.now(
            timezone.utc
        ).isoformat(),
        "test": "mixed_clip_reliability",
        "manifest": str(
            manifest_path.relative_to(
                repository_root
            )
        ),
        "dataset": manifest.get("dataset"),
        "split": manifest.get("split"),
        "configuration": {
            "requested_sessions": args.sessions,
            "manifest_clip_count": len(items),
            "chunk_ms": args.chunk_ms,
            "pace": args.pace,
            "pause_seconds": args.pause_seconds,
            "profile": initial_health.get("profile"),
            "model": initial_health.get("model"),
            "gpu": initial_health.get("gpu"),
        },
        "initial_health": initial_health,
        "sessions": [],
        "summary": {
            "completed_sessions": 0,
            "failed_session": None,
            "all_passed": False,
        },
    }

    save_json(output_path, payload)

    print(
        f"Service ready: profile={initial_health.get('profile')}, "
        f"GPU={initial_health.get('gpu')}"
    )
    print(
        f"Running {args.sessions} mixed sessions across "
        f"{len(items)} manifest clips."
    )
    print(
        f"Initial memory: {memory_summary(initial_health)}"
    )
    print(f"Progress file: {output_path}")
    print()

    for session_number in range(
        1,
        args.sessions + 1,
    ):
        item_index = (
            session_number - 1
        ) % len(items)
        cycle_number = (
            session_number - 1
        ) // len(items) + 1
        item = items[item_index]

        audio_path = (
            benchmark_root
            / Path(str(item["audio_path"]))
        )

        if not audio_path.is_file():
            raise FileNotFoundError(
                f"Audio file not found: {audio_path}"
            )

        samples, sample_rate = load_pcm16_mono(
            audio_path
        )

        health_before = read_health(
            args.health_url
        )

        print(
            f"[{session_number}/{args.sessions}] "
            f"cycle={cycle_number} "
            f"clip={item['id']} ...",
            end=" ",
            flush=True,
        )

        try:
            result = await run_one_session(
                websocket_url=args.ws_url,
                samples=samples,
                sample_rate=sample_rate,
                reference=str(item["reference"]),
                chunk_ms=args.chunk_ms,
                pace=args.pace,
            )

            health_after = read_health(
                args.health_url
            )

            result.update(
                {
                    "session_number": session_number,
                    "cycle_number": cycle_number,
                    "manifest_index": item_index,
                    "clip_id": item["id"],
                    "speaker_id": item.get("speaker_id"),
                    "chapter_id": item.get("chapter_id"),
                    "audio_path": item["audio_path"],
                    "health_before": health_before,
                    "health_after": health_after,
                }
            )

            payload["sessions"].append(result)
            payload["summary"]["completed_sessions"] = (
                session_number
            )

            save_json(output_path, payload)

            print(
                f"passed, WER={result['wer']:.3f}, "
                f"first={result['first_partial_seconds']}, "
                f"{memory_summary(health_after)}"
            )

        except Exception as exception:
            health_after = read_health(
                args.health_url
            )

            failure = {
                "success": False,
                "session_number": session_number,
                "cycle_number": cycle_number,
                "manifest_index": item_index,
                "clip_id": item["id"],
                "speaker_id": item.get("speaker_id"),
                "chapter_id": item.get("chapter_id"),
                "audio_path": item["audio_path"],
                "audio_duration_seconds": round(
                    len(samples) / sample_rate,
                    3,
                ),
                "exception_type": type(exception).__name__,
                "message": str(exception),
                "health_before": health_before,
                "health_after": health_after,
            }

            payload["sessions"].append(failure)
            payload["summary"]["failed_session"] = (
                session_number
            )

            save_json(output_path, payload)

            print(
                f"FAILED: {type(exception).__name__}: "
                f"{exception}"
            )
            print()
            print(
                f"Failure occurred on session {session_number}, "
                f"cycle {cycle_number}, clip {item['id']}."
            )
            print(
                f"Memory before failure: "
                f"{memory_summary(health_before)}"
            )
            print(
                f"Health after failure: "
                f"{health_after}"
            )
            print(f"Saved: {output_path}")
            return 1

        if (
            args.pause_seconds > 0
            and session_number < args.sessions
        ):
            await asyncio.sleep(
                args.pause_seconds
            )

    payload["summary"]["all_passed"] = True
    payload["final_health"] = read_health(
        args.health_url
    )

    successful_sessions = [
        session
        for session in payload["sessions"]
        if session.get("success")
    ]

    if successful_sessions:
        references = [
            normalize_text(
                str(session["reference"])
            )
            for session in successful_sessions
        ]
        hypotheses = [
            normalize_text(
                str(session["hypothesis"])
            )
            for session in successful_sessions
        ]

        payload["summary"]["aggregate_wer"] = (
            jiwer.wer(
                references,
                hypotheses,
            )
        )
        payload["summary"]["aggregate_cer"] = (
            jiwer.cer(
                references,
                hypotheses,
            )
        )

    save_json(output_path, payload)

    print()
    print(
        f"All {args.sessions} mixed sessions completed successfully."
    )
    print(
        f"Final memory: "
        f"{memory_summary(payload['final_health'])}"
    )
    print(f"Saved: {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(
        asyncio.run(async_main())
    )
