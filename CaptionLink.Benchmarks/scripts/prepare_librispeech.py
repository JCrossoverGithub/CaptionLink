#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import tarfile
import urllib.request
from collections import defaultdict
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable

OPENSLR_BASE_URLS = [
    "https://openslr.elda.org/resources/12",
    "https://openslr.trmal.net/resources/12",
    "https://openslr.magicdatatech.com/resources/12",
]

SPLITS = {
    "test-clean": {
        "archive": "test-clean.tar.gz",
        "expected_root": Path("LibriSpeech") / "test-clean",
    },
    "test-other": {
        "archive": "test-other.tar.gz",
        "expected_root": Path("LibriSpeech") / "test-other",
    },
}


@dataclass(frozen=True)
class ManifestItem:
    id: str
    dataset: str
    split: str
    speaker_id: str
    chapter_id: str
    audio_path: str
    reference: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Prepare a deterministic LibriSpeech benchmark sample."
    )
    parser.add_argument(
        "--split",
        choices=sorted(SPLITS),
        default="test-clean",
    )
    parser.add_argument(
        "--count",
        type=int,
        default=20,
    )
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
    )
    parser.add_argument(
        "--delete-archive",
        action="store_true",
    )
    return parser.parse_args()


def download_file(archive_name: str, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)

    if destination.exists() and destination.stat().st_size > 0:
        print(f"Archive already exists: {destination}")
        return

    temporary = destination.with_suffix(destination.suffix + ".part")
    last_error: Exception | None = None

    print(f"Destination: {destination}")

    for base_url in OPENSLR_BASE_URLS:
        url = f"{base_url}/{archive_name}"
        print(f"Trying mirror: {url}")

        try:
            temporary.unlink(missing_ok=True)

            with urllib.request.urlopen(url, timeout=60) as response, temporary.open("wb") as output:
                total_header = response.headers.get("Content-Length")
                total_bytes = int(total_header) if total_header else None
                copied = 0
                next_report = 25 * 1024 * 1024

                while True:
                    block = response.read(1024 * 1024)
                    if not block:
                        break

                    output.write(block)
                    copied += len(block)

                    if copied >= next_report:
                        if total_bytes:
                            percent = copied / total_bytes * 100
                            print(
                                f"  {copied / 1024 / 1024:.0f} MiB "
                                f"({percent:.1f}%)"
                            )
                        else:
                            print(f"  {copied / 1024 / 1024:.0f} MiB")

                        next_report += 25 * 1024 * 1024

            temporary.replace(destination)
            print(f"Download complete from: {base_url}")
            return

        except Exception as error:
            last_error = error
            temporary.unlink(missing_ok=True)
            print(f"Mirror failed: {error}")

    raise RuntimeError(
        "All OpenSLR mirrors failed. "
        f"Last error: {last_error}"
    )


def extract_archive(
    archive: Path,
    destination: Path,
    expected_root: Path,
) -> Path:
    extracted_root = destination / expected_root

    if extracted_root.is_dir():
        print(f"Dataset already extracted: {extracted_root}")
        return extracted_root

    destination.mkdir(parents=True, exist_ok=True)
    print(f"Extracting: {archive}")

    with tarfile.open(archive, "r:gz") as tar:
        try:
            tar.extractall(destination, filter="data")
        except TypeError:
            tar.extractall(destination)

    if not extracted_root.is_dir():
        raise FileNotFoundError(
            f"Expected extracted directory was not found: {extracted_root}"
        )

    return extracted_root


def load_utterances(
    dataset_root: Path,
    split: str,
) -> list[ManifestItem]:
    transcripts: dict[str, str] = {}

    for transcript_file in sorted(dataset_root.rglob("*.trans.txt")):
        with transcript_file.open("r", encoding="utf-8") as handle:
            for raw_line in handle:
                line = raw_line.strip()
                if not line:
                    continue

                utterance_id, reference = line.split(maxsplit=1)
                transcripts[utterance_id] = reference

    items: list[ManifestItem] = []

    for audio_file in sorted(dataset_root.rglob("*.flac")):
        utterance_id = audio_file.stem
        parts = utterance_id.split("-")

        if len(parts) < 3:
            raise ValueError(f"Unexpected LibriSpeech ID: {utterance_id}")

        reference = transcripts.get(utterance_id)

        if reference is None:
            raise KeyError(f"No transcript found for {utterance_id}")

        relative_path = audio_file.relative_to(
            dataset_root.parents[1]
        ).as_posix()

        items.append(
            ManifestItem(
                id=utterance_id,
                dataset="librispeech",
                split=split,
                speaker_id=parts[0],
                chapter_id=parts[1],
                audio_path=f"audio/librispeech/{relative_path}",
                reference=reference,
            )
        )

    if not items:
        raise RuntimeError(f"No FLAC files found under {dataset_root}")

    return items


def choose_balanced_sample(
    items: Iterable[ManifestItem],
    count: int,
) -> list[ManifestItem]:
    if count <= 0:
        raise ValueError("--count must be greater than zero.")

    by_speaker: dict[str, list[ManifestItem]] = defaultdict(list)

    for item in items:
        by_speaker[item.speaker_id].append(item)

    speakers = sorted(by_speaker)
    selected: list[ManifestItem] = []
    index_by_speaker = {speaker: 0 for speaker in speakers}

    while len(selected) < count:
        made_progress = False

        for speaker in speakers:
            index = index_by_speaker[speaker]
            speaker_items = by_speaker[speaker]

            if index >= len(speaker_items):
                continue

            selected.append(speaker_items[index])
            index_by_speaker[speaker] += 1
            made_progress = True

            if len(selected) == count:
                break

        if not made_progress:
            break

    if len(selected) < count:
        raise ValueError(
            f"Requested {count} clips, but only {len(selected)} were available."
        )

    return selected


def write_manifest(
    root: Path,
    split: str,
    selected: list[ManifestItem],
) -> Path:
    manifest_directory = root / "manifests"
    manifest_directory.mkdir(parents=True, exist_ok=True)

    manifest_path = (
        manifest_directory
        / f"librispeech-{split}-v1.json"
    )

    payload = {
        "schema_version": 1,
        "dataset": "LibriSpeech",
        "split": split,
        "clip_count": len(selected),
        "items": [asdict(item) for item in selected],
    }

    manifest_path.write_text(
        json.dumps(payload, indent=2) + "\n",
        encoding="utf-8",
    )

    return manifest_path


def main() -> int:
    args = parse_args()
    root = args.root.resolve()
    split_config = SPLITS[args.split]

    archive_name = split_config["archive"]
    archive_path = root / "audio" / "downloads" / archive_name
    extraction_directory = root / "audio" / "librispeech"
    download_file(archive_name, archive_path)

    dataset_root = extract_archive(
        archive=archive_path,
        destination=extraction_directory,
        expected_root=split_config["expected_root"],
    )

    all_items = load_utterances(
        dataset_root,
        args.split,
    )

    selected = choose_balanced_sample(
        all_items,
        args.count,
    )

    manifest_path = write_manifest(
        root,
        args.split,
        selected,
    )

    print()
    print(f"Found {len(all_items)} utterances.")
    print(f"Selected {len(selected)} benchmark clips.")
    print(f"Manifest written to: {manifest_path}")

    if args.delete_archive:
        archive_path.unlink(missing_ok=True)
        print(f"Deleted archive: {archive_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
