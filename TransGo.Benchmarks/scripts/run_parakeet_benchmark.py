#!/usr/bin/env python3
"""
Run a LibriSpeech manifest through the live TransGo Parakeet WebSocket service.

The benchmark replays each clip at real-time speed, records transcript and
latency information, computes per-clip and aggregate WER/CER with JiWER, and
writes a JSON result file tied to the current Git commit.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import re
import subprocess
import time
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import jiwer
import numpy as np
import soundfile as sf
from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed


@dataclass
class TranscriptState:
    started_message: dict[str, Any] | None = None
    final_segments: list[str] = field(default_factory=list)
    latest_partial: str = ""
    first_partial_seconds: float | None = None
    first_final_seconds: float | None = None
    last_transcript_seconds: float | None = None
    message_count: int = 0
    partial_revision_count: int = 0
    errors: list[str] = field(default_factory=list)


def parse_args() -> argparse.Namespace:
    benchmark_root = Path(__file__).resolve().parents[1]

    parser = argparse.ArgumentParser(
        description="Benchmark the live TransGo Parakeet service."
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
        "--ws-url",
        default="ws://localhost:8765/stream",
    )
    parser.add_argument(
        "--health-url",
        default="http://localhost:8765/health",
    )
    parser.add_argument(
        "--chunk-ms",
        type=int,
        default=100,
        help="Audio packet duration in milliseconds.",
    )
    parser.add_argument(
        "--pace",
        type=float,
        default=1.0,
        help=(
            "Replay speed. 1.0 is real time, 2.0 is twice real time, "
            "and 0 sends as fast as possible."
        ),
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=None,
        help="Optional number of manifest clips to run.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
    )
    parser.add_argument(
        "--checkpoint",
        type=Path,
        default=None,
        help="Optional checkpoint path. A default path is used otherwise.",
    )
    parser.add_argument(
        "--resume",
        action="store_true",
        help="Resume completed clips from an existing checkpoint.",
    )
    parser.add_argument(
        "--retry-count",
        type=int,
        default=1,
        help="Number of retries for a clip after a connection failure.",
    )
    parser.add_argument(
        "--retry-delay",
        type=float,
        default=3.0,
        help="Seconds to wait before retrying a failed clip.",
    )
    return parser.parse_args()


def normalize_text(text: str) -> str:
    text = text.casefold().replace("_", " ")
    text = re.sub(r"[^\w']+", " ", text, flags=re.UNICODE)
    return " ".join(text.split())


def read_health(url: str) -> dict[str, Any]:
    with urllib.request.urlopen(url, timeout=5) as response:
        return json.loads(response.read().decode("utf-8"))


def get_git_commit(repository_root: Path) -> str | None:
    try:
        completed = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=repository_root,
            check=True,
            capture_output=True,
            text=True,
        )
        return completed.stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return None


def load_pcm16_mono(audio_path: Path) -> tuple[np.ndarray, int]:
    samples, sample_rate = sf.read(
        audio_path,
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

        if message.get("type") == "error":
            raise RuntimeError(
                message.get("message", "Unknown Parakeet service error.")
            )

        if message.get("type") == expected_type:
            return message


async def receive_transcripts(
    websocket,
    state: TranscriptState,
    audio_start_time: float,
) -> None:
    try:
        async for raw_message in websocket:
            if not isinstance(raw_message, str):
                continue

            message = json.loads(raw_message)
            message_type = message.get("type")

            if message_type == "transcript":
                now_seconds = time.perf_counter() - audio_start_time
                text = str(message.get("text", "")).strip()
                is_final = bool(message.get("is_final"))

                state.message_count += 1
                state.last_transcript_seconds = now_seconds

                if is_final:
                    if state.first_final_seconds is None:
                        state.first_final_seconds = now_seconds

                    if text:
                        state.final_segments.append(text)

                    state.latest_partial = ""
                else:
                    if state.first_partial_seconds is None and text:
                        state.first_partial_seconds = now_seconds

                    if text and text != state.latest_partial:
                        state.partial_revision_count += 1
                        state.latest_partial = text

            elif message_type == "error":
                state.errors.append(
                    str(message.get("message", "Unknown service error."))
                )

    except ConnectionClosed:
        pass


async def run_clip(
    websocket_url: str,
    audio_path: Path,
    reference: str,
    chunk_ms: int,
    pace: float,
) -> dict[str, Any]:
    samples, sample_rate = load_pcm16_mono(audio_path)
    audio_duration_seconds = len(samples) / sample_rate
    samples_per_chunk = max(
        1,
        round(sample_rate * chunk_ms / 1000),
    )

    state = TranscriptState()

    async with connect(
        websocket_url,
        open_timeout=15,
        close_timeout=15,
        max_size=4 * 1024 * 1024,
    ) as websocket:
        connected = await wait_for_message_type(
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

        state.started_message = await wait_for_message_type(
            websocket,
            "started",
        )

        audio_start_time = time.perf_counter()

        receiver_task = asyncio.create_task(
            receive_transcripts(
                websocket,
                state,
                audio_start_time,
            )
        )

        for chunk_index, start in enumerate(
            range(0, len(samples), samples_per_chunk)
        ):
            end = min(
                start + samples_per_chunk,
                len(samples),
            )

            chunk = samples[start:end]
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
                        - audio_start_time
                    )
                )

                if delay > 0:
                    await asyncio.sleep(delay)

        await websocket.send(
            json.dumps({"type": "stop"})
        )

        try:
            await asyncio.wait_for(
                receiver_task,
                timeout=30,
            )
        except asyncio.TimeoutError:
            receiver_task.cancel()
            await asyncio.gather(
                receiver_task,
                return_exceptions=True,
            )
            state.errors.append(
                "Timed out waiting for the service to close the stream."
            )

    hypothesis = " ".join(state.final_segments).strip()

    if not hypothesis:
        hypothesis = state.latest_partial.strip()

    normalized_reference = normalize_text(reference)
    normalized_hypothesis = normalize_text(hypothesis)

    word_result = jiwer.process_words(
        normalized_reference,
        normalized_hypothesis,
    )

    character_error_rate = jiwer.cer(
        normalized_reference,
        normalized_hypothesis,
    )

    last_latency = state.last_transcript_seconds
    finalization_delay = (
        last_latency - audio_duration_seconds
        if last_latency is not None
        else None
    )

    return {
        "audio_path": str(audio_path),
        "audio_duration_seconds": round(
            audio_duration_seconds,
            3,
        ),
        "sample_rate": sample_rate,
        "reference": reference,
        "hypothesis": hypothesis,
        "normalized_reference": normalized_reference,
        "normalized_hypothesis": normalized_hypothesis,
        "wer": word_result.wer,
        "cer": character_error_rate,
        "hits": word_result.hits,
        "substitutions": word_result.substitutions,
        "deletions": word_result.deletions,
        "insertions": word_result.insertions,
        "first_partial_seconds": state.first_partial_seconds,
        "first_final_seconds": state.first_final_seconds,
        "last_transcript_seconds": state.last_transcript_seconds,
        "finalization_delay_seconds": finalization_delay,
        "transcript_message_count": state.message_count,
        "partial_revision_count": state.partial_revision_count,
        "service_started_message": state.started_message,
        "service_connected_message": connected,
        "errors": state.errors,
    }


def default_output_path(
    benchmark_root: Path,
    profile: str,
) -> Path:
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")

    return (
        benchmark_root
        / "results"
        / f"parakeet-{profile}-{timestamp}.json"
    )



def default_checkpoint_path(
    benchmark_root: Path,
    profile: str,
    manifest_path: Path,
) -> Path:
    return (
        benchmark_root
        / "results"
        / "raw"
        / (
            f"parakeet-{profile}-"
            f"{manifest_path.stem}.checkpoint.json"
        )
    )


def calculate_summary(
    clip_results: list[dict[str, Any]],
) -> dict[str, Any]:
    if not clip_results:
        return {
            "wer": None,
            "cer": None,
            "hits": 0,
            "substitutions": 0,
            "deletions": 0,
            "insertions": 0,
            "average_first_partial_seconds": None,
            "average_first_final_seconds": None,
        }

    references = [
        result["normalized_reference"]
        for result in clip_results
    ]
    hypotheses = [
        result["normalized_hypothesis"]
        for result in clip_results
    ]

    aggregate_words = jiwer.process_words(
        references,
        hypotheses,
    )

    aggregate_cer = jiwer.cer(
        references,
        hypotheses,
    )

    partial_latencies = [
        result["first_partial_seconds"]
        for result in clip_results
        if result["first_partial_seconds"] is not None
    ]

    final_latencies = [
        result["first_final_seconds"]
        for result in clip_results
        if result["first_final_seconds"] is not None
    ]

    return {
        "wer": aggregate_words.wer,
        "cer": aggregate_cer,
        "hits": aggregate_words.hits,
        "substitutions": aggregate_words.substitutions,
        "deletions": aggregate_words.deletions,
        "insertions": aggregate_words.insertions,
        "average_first_partial_seconds": (
            sum(partial_latencies) / len(partial_latencies)
            if partial_latencies
            else None
        ),
        "average_first_final_seconds": (
            sum(final_latencies) / len(final_latencies)
            if final_latencies
            else None
        ),
    }


def build_payload(
    *,
    repository_root: Path,
    manifest_path: Path,
    profile: str,
    health: dict[str, Any],
    clip_results: list[dict[str, Any]],
    chunk_ms: int,
    pace: float,
    status: str,
    failure: dict[str, Any] | None = None,
) -> dict[str, Any]:
    return {
        "schema_version": 2,
        "status": status,
        "created_utc": datetime.now(
            timezone.utc
        ).isoformat(),
        "git_commit": get_git_commit(repository_root),
        "provider": "parakeet",
        "profile": profile,
        "service_health": health,
        "manifest": str(
            manifest_path.relative_to(repository_root)
        ),
        "clip_count": len(clip_results),
        "replay": {
            "chunk_ms": chunk_ms,
            "pace": pace,
        },
        "summary": calculate_summary(clip_results),
        "failure": failure,
        "clips": clip_results,
    }


def write_json(
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


async def async_main() -> int:
    args = parse_args()
    manifest_path = args.manifest.resolve()
    benchmark_root = Path(__file__).resolve().parents[1]
    repository_root = benchmark_root.parent

    if args.retry_count < 0:
        raise ValueError("--retry-count cannot be negative.")

    if not manifest_path.is_file():
        raise FileNotFoundError(
            f"Manifest not found: {manifest_path}"
        )

    manifest = json.loads(
        manifest_path.read_text(encoding="utf-8")
    )

    items = list(manifest.get("items", []))

    if args.limit is not None:
        items = items[: args.limit]

    if not items:
        raise RuntimeError("The manifest contains no benchmark items.")

    health = read_health(args.health_url)

    if health.get("status") != "ready":
        raise RuntimeError(
            f"Parakeet service is not ready: {health}"
        )

    profile = str(
        health.get("profile", "unknown")
    )

    checkpoint_path = (
        args.checkpoint.resolve()
        if args.checkpoint is not None
        else default_checkpoint_path(
            benchmark_root,
            profile,
            manifest_path,
        )
    )

    clip_results: list[dict[str, Any]] = []

    if args.resume and checkpoint_path.is_file():
        checkpoint = json.loads(
            checkpoint_path.read_text(encoding="utf-8")
        )

        checkpoint_profile = str(
            checkpoint.get("profile", "")
        )

        if checkpoint_profile and checkpoint_profile != profile:
            raise RuntimeError(
                "Checkpoint profile does not match the active "
                f"service profile: {checkpoint_profile!r} != {profile!r}."
            )

        clip_results = list(
            checkpoint.get("clips", [])
        )

        print(
            f"Resuming {len(clip_results)} completed clips "
            f"from {checkpoint_path}"
        )

    completed_ids = {
        str(result.get("id"))
        for result in clip_results
    }

    print(
        f"Service ready: profile={profile}, "
        f"GPU={health.get('gpu')}"
    )
    print(f"Running {len(items)} clips from {manifest_path.name}")
    print(f"Checkpoint: {checkpoint_path}")
    print()

    for index, item in enumerate(items, start=1):
        item_id = str(item["id"])

        if item_id in completed_ids:
            print(
                f"[{index}/{len(items)}] {item_id} ... skipped "
                "(already completed)"
            )
            continue

        relative_audio_path = Path(item["audio_path"])
        audio_path = benchmark_root / relative_audio_path

        if not audio_path.is_file():
            raise FileNotFoundError(
                f"Audio file not found: {audio_path}"
            )

        result: dict[str, Any] | None = None
        last_exception: Exception | None = None

        for attempt in range(args.retry_count + 1):
            attempt_label = (
                ""
                if attempt == 0
                else f" retry {attempt}/{args.retry_count}"
            )

            print(
                f"[{index}/{len(items)}] "
                f"{item_id}{attempt_label} ...",
                end=" ",
                flush=True,
            )

            try:
                result = await run_clip(
                    websocket_url=args.ws_url,
                    audio_path=audio_path,
                    reference=str(item["reference"]),
                    chunk_ms=args.chunk_ms,
                    pace=args.pace,
                )
                break
            except (
                ConnectionClosed,
                ConnectionError,
                OSError,
                asyncio.TimeoutError,
            ) as exception:
                last_exception = exception
                print(
                    f"connection failed: {type(exception).__name__}: "
                    f"{exception}"
                )

                if attempt < args.retry_count:
                    await asyncio.sleep(args.retry_delay)

                    try:
                        retry_health = read_health(args.health_url)
                        print(
                            "  Service health before retry: "
                            f"{retry_health.get('status')}, "
                            f"profile={retry_health.get('profile')}"
                        )
                    except Exception as health_exception:
                        print(
                            "  Service health check failed: "
                            f"{health_exception}"
                        )

        if result is None:
            failure_health: dict[str, Any] | None = None

            try:
                failure_health = read_health(args.health_url)
            except Exception:
                pass

            failure = {
                "clip_id": item_id,
                "index": index,
                "exception_type": (
                    type(last_exception).__name__
                    if last_exception is not None
                    else "Unknown"
                ),
                "message": str(last_exception),
                "service_health_after_failure": failure_health,
            }

            checkpoint_payload = build_payload(
                repository_root=repository_root,
                manifest_path=manifest_path,
                profile=profile,
                health=health,
                clip_results=clip_results,
                chunk_ms=args.chunk_ms,
                pace=args.pace,
                status="interrupted",
                failure=failure,
            )

            write_json(
                checkpoint_path,
                checkpoint_payload,
            )

            print()
            print("Benchmark interrupted, but completed clips were saved.")
            print(f"Failed clip: {item_id}")
            print(f"Checkpoint: {checkpoint_path}")
            print(
                "Restart the Parakeet service, then rerun with --resume."
            )
            return 2

        result["id"] = item_id
        result["dataset"] = item.get("dataset")
        result["split"] = item.get("split")
        result["speaker_id"] = item.get("speaker_id")
        result["chapter_id"] = item.get("chapter_id")

        clip_results.append(result)
        completed_ids.add(item_id)

        print(
            f"WER={result['wer']:.3f}, "
            f"first={result['first_partial_seconds']}, "
            f"hypothesis={result['hypothesis']!r}"
        )

        checkpoint_payload = build_payload(
            repository_root=repository_root,
            manifest_path=manifest_path,
            profile=profile,
            health=health,
            clip_results=clip_results,
            chunk_ms=args.chunk_ms,
            pace=args.pace,
            status="running",
        )

        write_json(
            checkpoint_path,
            checkpoint_payload,
        )

    payload = build_payload(
        repository_root=repository_root,
        manifest_path=manifest_path,
        profile=profile,
        health=health,
        clip_results=clip_results,
        chunk_ms=args.chunk_ms,
        pace=args.pace,
        status="complete",
    )

    output_path = (
        args.output.resolve()
        if args.output is not None
        else default_output_path(
            benchmark_root,
            profile,
        )
    )

    write_json(output_path, payload)
    write_json(checkpoint_path, payload)

    summary = payload["summary"]

    print()
    print("Benchmark complete.")
    print(f"WER: {summary['wer']:.4f}")
    print(f"CER: {summary['cer']:.4f}")
    print(f"Results: {output_path}")
    print(f"Checkpoint: {checkpoint_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(async_main()))
