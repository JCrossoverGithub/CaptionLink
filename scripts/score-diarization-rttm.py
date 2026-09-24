from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path

import soundfile as sf

from nemo.collections.asr.metrics.der import (
    score_labels_from_rttm_labels,
)
from nemo.collections.asr.parts.utils.speaker_utils import (
    rttm_to_labels,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Score diarization RTTMs with the same NeMo DER "
            "implementation used by NVIDIA."
        )
    )

    parser.add_argument(
        "--audio-dir",
        required=True,
        type=Path,
    )
    parser.add_argument(
        "--reference-rttm-dir",
        required=True,
        type=Path,
    )
    parser.add_argument(
        "--hypothesis-rttm-dir",
        required=True,
        type=Path,
    )
    parser.add_argument(
        "--output-dir",
        required=True,
        type=Path,
    )
    parser.add_argument(
        "--include",
        default=None,
        help="Comma-separated recording IDs.",
    )
    parser.add_argument(
        "--collar",
        type=float,
        default=0.25,
    )
    parser.add_argument(
        "--ignore-overlap",
        action="store_true",
    )

    return parser.parse_args()


def speaker_ids(labels: list[str]) -> list[str]:
    return sorted(
        {
            line.split()[2]
            for line in labels
        }
    )


def score_one(
    recording_id: str,
    audio_path: Path,
    reference_path: Path,
    hypothesis_path: Path,
    collar: float,
    ignore_overlap: bool,
) -> dict[str, object]:
    reference_labels = rttm_to_labels(
        str(reference_path)
    )
    hypothesis_labels = rttm_to_labels(
        str(hypothesis_path)
    )

    duration = sf.info(
        str(audio_path)
    ).duration

    result = score_labels_from_rttm_labels(
        ref_labels_list=[
            (recording_id, reference_labels),
        ],
        hyp_labels_list=[
            (recording_id, hypothesis_labels),
        ],
        uem_segments_list=[
            (
                recording_id,
                [[0.0, duration]],
            ),
        ],
        collar=collar,
        ignore_overlap=ignore_overlap,
        verbose=False,
    )

    if result is None:
        raise RuntimeError(
            f"{recording_id}: NeMo scorer returned None."
        )

    _metric, mapping, errors = result

    der, confusion, false_alarm, miss = errors

    reference_speakers = speaker_ids(
        reference_labels
    )
    hypothesis_speakers = speaker_ids(
        hypothesis_labels
    )

    return {
        "recording_id": recording_id,
        "duration_seconds": duration,
        "der": der,
        "false_alarm": false_alarm,
        "miss": miss,
        "confusion": confusion,
        "reference_speakers": len(
            reference_speakers
        ),
        "predicted_speakers": len(
            hypothesis_speakers
        ),
        "speaker_count_error": (
            len(hypothesis_speakers)
            - len(reference_speakers)
        ),
        "speaker_count_correct": (
            len(hypothesis_speakers)
            == len(reference_speakers)
        ),
        "mapping": mapping.get(
            recording_id,
            {},
        ),
    }


def aggregate_score(
    rows: list[dict[str, object]],
    audio_dir: Path,
    reference_dir: Path,
    hypothesis_dir: Path,
    collar: float,
    ignore_overlap: bool,
) -> dict[str, object]:
    reference_items: list[
        tuple[str, list[str]]
    ] = []

    hypothesis_items: list[
        tuple[str, list[str]]
    ] = []

    uem_items: list[
        tuple[str, list[list[float]]]
    ] = []

    for row in rows:
        recording_id = str(
            row["recording_id"]
        )

        reference_items.append(
            (
                recording_id,
                rttm_to_labels(
                    str(
                        reference_dir
                        / f"{recording_id}.rttm"
                    )
                ),
            )
        )

        hypothesis_items.append(
            (
                recording_id,
                rttm_to_labels(
                    str(
                        hypothesis_dir
                        / f"{recording_id}.rttm"
                    )
                ),
            )
        )

        duration = sf.info(
            str(
                audio_dir
                / f"{recording_id}.wav"
            )
        ).duration

        uem_items.append(
            (
                recording_id,
                [[0.0, duration]],
            )
        )

    result = score_labels_from_rttm_labels(
        ref_labels_list=reference_items,
        hyp_labels_list=hypothesis_items,
        uem_segments_list=uem_items,
        collar=collar,
        ignore_overlap=ignore_overlap,
        verbose=False,
    )

    if result is None:
        raise RuntimeError(
            "Aggregate NeMo scorer returned None."
        )

    _metric, _mapping, errors = result

    der, confusion, false_alarm, miss = errors

    absolute_count_errors = [
        abs(
            int(row["speaker_count_error"])
        )
        for row in rows
    ]

    count_correct = sum(
        bool(row["speaker_count_correct"])
        for row in rows
    )

    return {
        "recordings": len(rows),
        "collar_seconds": collar,
        "ignore_overlap": ignore_overlap,
        "der": der,
        "false_alarm": false_alarm,
        "miss": miss,
        "confusion": confusion,
        "speaker_count_accuracy": (
            count_correct / len(rows)
            if rows
            else 0.0
        ),
        "speaker_count_mae": (
            sum(absolute_count_errors)
            / len(absolute_count_errors)
            if absolute_count_errors
            else 0.0
        ),
    }


def main() -> None:
    args = parse_args()

    include = None

    if args.include:
        include = {
            value.strip()
            for value in args.include.split(",")
            if value.strip()
        }

    audio_paths = sorted(
        args.audio_dir.glob("*.wav")
    )

    if include is not None:
        audio_paths = [
            path
            for path in audio_paths
            if path.stem in include
        ]

    if not audio_paths:
        raise RuntimeError(
            "No matching WAV files found."
        )

    rows: list[dict[str, object]] = []

    for index, audio_path in enumerate(
        audio_paths,
        start=1,
    ):
        recording_id = audio_path.stem

        reference_path = (
            args.reference_rttm_dir
            / f"{recording_id}.rttm"
        )

        hypothesis_path = (
            args.hypothesis_rttm_dir
            / f"{recording_id}.rttm"
        )

        if not reference_path.is_file():
            raise FileNotFoundError(
                reference_path
            )

        if not hypothesis_path.is_file():
            raise FileNotFoundError(
                hypothesis_path
            )

        row = score_one(
            recording_id,
            audio_path,
            reference_path,
            hypothesis_path,
            args.collar,
            args.ignore_overlap,
        )

        rows.append(row)

        print(
            f"[{index}/{len(audio_paths)}] "
            f"{recording_id}: "
            f"DER={float(row['der']):.2%}; "
            f"FA={float(row['false_alarm']):.2%}; "
            f"MISS={float(row['miss']):.2%}; "
            f"CONF={float(row['confusion']):.2%}; "
            f"speakers="
            f"{row['predicted_speakers']}/"
            f"{row['reference_speakers']}"
        )

    aggregate = aggregate_score(
        rows,
        args.audio_dir,
        args.reference_rttm_dir,
        args.hypothesis_rttm_dir,
        args.collar,
        args.ignore_overlap,
    )

    args.output_dir.mkdir(
        parents=True,
        exist_ok=True,
    )

    json_path = (
        args.output_dir
        / "diarization-score.json"
    )

    csv_path = (
        args.output_dir
        / "diarization-score.csv"
    )

    json_path.write_text(
        json.dumps(
            {
                "aggregate": aggregate,
                "recordings": rows,
            },
            indent=2,
            sort_keys=True,
        )
        + "\n"
    )

    with csv_path.open(
        "w",
        newline="",
        encoding="utf-8",
    ) as handle:
        fieldnames = [
            "recording_id",
            "duration_seconds",
            "der",
            "false_alarm",
            "miss",
            "confusion",
            "reference_speakers",
            "predicted_speakers",
            "speaker_count_error",
            "speaker_count_correct",
        ]

        writer = csv.DictWriter(
            handle,
            fieldnames=fieldnames,
        )

        writer.writeheader()

        for row in rows:
            writer.writerow(
                {
                    key: row[key]
                    for key in fieldnames
                }
            )

    print()
    print("Aggregate:")
    print(
        f"DER:       "
        f"{float(aggregate['der']):.4%}"
    )
    print(
        f"FA:        "
        f"{float(aggregate['false_alarm']):.4%}"
    )
    print(
        f"MISS:      "
        f"{float(aggregate['miss']):.4%}"
    )
    print(
        f"CONFUSION: "
        f"{float(aggregate['confusion']):.4%}"
    )
    print(
        f"Speaker count accuracy: "
        f"{float(aggregate['speaker_count_accuracy']):.2%}"
    )
    print(
        f"Speaker count MAE: "
        f"{float(aggregate['speaker_count_mae']):.4f}"
    )
    print()
    print(f"JSON: {json_path}")
    print(f"CSV:  {csv_path}")


if __name__ == "__main__":
    main()
