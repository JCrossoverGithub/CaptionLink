from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

import nemo
import numpy as np
import torch
from omegaconf import OmegaConf, open_dict

from nemo.collections.asr.inference.factory.pipeline_builder import (
    PipelineBuilder,
)
from nemo.collections.asr.inference.streaming.framing.request import Frame
from nemo.collections.asr.inference.streaming.framing.request_options import (
    ASRRequestOptions,
)

from parakeet_audio import ModelAudioFrame


MODEL_NAME = "nvidia/parakeet-unified-en-0.6b"
MODEL_SAMPLE_RATE = 16_000
PROFILE_ENVIRONMENT_VARIABLE = "CAPTIONLINK_PARAKEET_PROFILE"
LEGACY_PROFILE_ENVIRONMENT_VARIABLE = "TRANSGO_PARAKEET_PROFILE"


@dataclass(frozen=True)
class ParakeetStreamingProfile:
    name: str
    frame_duration_seconds: float
    left_padding_seconds: float
    right_padding_seconds: float


STREAMING_PROFILES = {
    "responsive": ParakeetStreamingProfile(
        name="responsive",
        frame_duration_seconds=0.16,
        left_padding_seconds=5.60,
        right_padding_seconds=0.40,
    ),
    "accurate": ParakeetStreamingProfile(
        name="accurate",
        frame_duration_seconds=0.56,
        left_padding_seconds=5.60,
        right_padding_seconds=0.56,
    ),
}


def load_active_profile() -> ParakeetStreamingProfile:
    requested_profile = (
        os.getenv(PROFILE_ENVIRONMENT_VARIABLE)
        or os.getenv(LEGACY_PROFILE_ENVIRONMENT_VARIABLE)
        or "accurate"
    ).strip().lower()

    profile = STREAMING_PROFILES.get(requested_profile)

    if profile is None:
        supported_profiles = ", ".join(
            sorted(STREAMING_PROFILES)
        )
        raise ValueError(
            f"Unknown Parakeet streaming profile "
            f"'{requested_profile}'. Supported profiles: "
            f"{supported_profiles}."
        )

    return profile


ACTIVE_PROFILE = load_active_profile()
ACTIVE_PROFILE_NAME = ACTIVE_PROFILE.name

MODEL_FRAME_DURATION_SECONDS = (
    ACTIVE_PROFILE.frame_duration_seconds
)

MODEL_FRAME_SAMPLE_COUNT = int(
    round(
        MODEL_SAMPLE_RATE
        * MODEL_FRAME_DURATION_SECONDS
    )
)

NEMO_SOURCE_ROOT = (
    Path(nemo.__file__).resolve().parent.parent
)

DEFAULT_CONFIG_PATH = (
    NEMO_SOURCE_ROOT
    / "examples"
    / "asr"
    / "conf"
    / "asr_streaming_inference"
    / "buffered_rnnt.yaml"
)


@dataclass(frozen=True)
class ParakeetPipelineResult:
    partial_text: str
    final_text: str

def apply_nemo_decoder_length_guard(pipeline):
    """
    Prevent NeMo's stateful RNN-T decoder from receiving sequence
    lengths larger than the encoder tensor's actual time dimension.

    With the current Accurate profile, NeMo can report 16 decoder
    steps while the sliced encoder tensor contains only 14. A final
    streaming frame can expose this mismatch as an out-of-bounds
    CUDA tensor access.
    """
    if getattr(
        pipeline,
        "_captionlink_decoder_length_guard_installed",
        False,
    ):
        return pipeline

    original_stateful_transcribe_step = (
        pipeline.stateful_transcribe_step
    )

    has_reported_clamp = False

    def guarded_stateful_transcribe_step(
        requests,
        encs,
        enc_lens_chunk,
        enc_lens,
        ready_state_ids,
    ):
        nonlocal has_reported_clamp

        max_encoder_time = encs.shape[2]

        safe_enc_lens = torch.clamp(
            enc_lens,
            min=0,
            max=max_encoder_time,
        )

        safe_enc_lens_chunk = torch.clamp(
            enc_lens_chunk,
            min=0,
            max=max_encoder_time,
        )

        safe_enc_lens_chunk = torch.minimum(
            safe_enc_lens_chunk,
            safe_enc_lens,
        )

        if not has_reported_clamp:
            lengths_changed = (
                torch.any(
                    safe_enc_lens != enc_lens
                ).item()
                or torch.any(
                    safe_enc_lens_chunk
                    != enc_lens_chunk
                ).item()
            )

            if lengths_changed:
                print(
                    "Applied CaptionLink NeMo "
                    "decoder-length guard: "
                    f"encoder_time={max_encoder_time}, "
                    f"original_total={enc_lens.tolist()}, "
                    f"safe_total={safe_enc_lens.tolist()}, "
                    f"original_chunk="
                    f"{enc_lens_chunk.tolist()}, "
                    f"safe_chunk="
                    f"{safe_enc_lens_chunk.tolist()}",
                    flush=True,
                )
                has_reported_clamp = True

        return original_stateful_transcribe_step(
            requests,
            encs,
            safe_enc_lens_chunk,
            safe_enc_lens,
            ready_state_ids,
        )

    pipeline.stateful_transcribe_step = (
        guarded_stateful_transcribe_step
    )

    pipeline._captionlink_decoder_length_guard_installed = True

    return pipeline

def build_parakeet_pipeline(
    config_path: Path = DEFAULT_CONFIG_PATH,
):
    config_path = config_path.expanduser().resolve()

    if not config_path.is_file():
        raise FileNotFoundError(
            f"NeMo streaming config not found: {config_path}"
        )

    if not torch.cuda.is_available():
        raise RuntimeError(
            "CUDA is not available to PyTorch."
        )

    cfg = OmegaConf.load(config_path)

    with open_dict(cfg):
        cfg.asr.model_name = MODEL_NAME
        cfg.asr.device = "cuda"
        cfg.asr.device_id = 0
        cfg.asr.compute_dtype = "float16"
        cfg.asr.use_amp = False

        cfg.streaming.sample_rate = MODEL_SAMPLE_RATE
        cfg.streaming.batch_size = 1
        cfg.streaming.left_padding_size = (
            ACTIVE_PROFILE.left_padding_seconds
        )
        cfg.streaming.chunk_size = (
            ACTIVE_PROFILE.frame_duration_seconds
        )
        cfg.streaming.right_padding_size = (
            ACTIVE_PROFILE.right_padding_seconds
        )
        cfg.streaming.request_type = "frame"
        cfg.streaming.stateful = True
        cfg.streaming.padding_mode = "right"

        cfg.asr_decoding_type = "rnnt"
        cfg.asr.decoding.strategy = "greedy_batch"
        cfg.asr.decoding.greedy.use_cuda_graph_decoder = False

        cfg.enable_itn = False
        cfg.enable_nmt = False
        cfg.return_tail_result = True
        cfg.calculate_wer = False
        cfg.calculate_bleu = False
        cfg.log_level = 30

    print(
        "Parakeet streaming profile: "
        f"{ACTIVE_PROFILE_NAME}; "
        f"chunk={ACTIVE_PROFILE.frame_duration_seconds:.2f}s; "
        f"left={ACTIVE_PROFILE.left_padding_seconds:.2f}s; "
        f"right={ACTIVE_PROFILE.right_padding_seconds:.2f}s"
    )

    pipeline = PipelineBuilder.build_pipeline(cfg)
    return apply_nemo_decoder_length_guard(pipeline)


class ParakeetStreamingSession:
    def __init__(
        self,
        pipeline,
        stream_id: int = 0,
    ) -> None:
        self._pipeline = pipeline
        self._stream_id = stream_id
        self._options = ASRRequestOptions()
        self._is_first_frame = True
        self._is_open = False

    def open(self) -> None:
        if self._is_open:
            raise RuntimeError(
                "The Parakeet streaming session is already open."
            )

        self._pipeline.open_session()
        self._is_open = True
        self._is_first_frame = True

    def transcribe(
        self,
        audio_frame: ModelAudioFrame,
        *,
        is_last: bool = False,
    ) -> ParakeetPipelineResult:
        if not self._is_open:
            raise RuntimeError(
                "Open the Parakeet streaming session first."
            )

        samples = np.asarray(
            audio_frame.samples,
            dtype=np.float32,
        ).reshape(-1)

        if samples.size != MODEL_FRAME_SAMPLE_COUNT:
            raise ValueError(
                "A Parakeet model frame must contain exactly "
                f"{MODEL_FRAME_SAMPLE_COUNT} samples, but received "
                f"{samples.size}."
            )

        if not (
            0
            <= audio_frame.valid_length
            <= MODEL_FRAME_SAMPLE_COUNT
        ):
            raise ValueError(
                "The model frame has an invalid valid_length."
            )

        request = Frame(
            samples=torch.from_numpy(samples),
            stream_id=self._stream_id,
            is_first=self._is_first_frame,
            is_last=is_last,
            length=audio_frame.valid_length,
            options=self._options,
        )

        outputs = self._pipeline.transcribe_step([request])
        self._is_first_frame = False

        if not outputs:
            return ParakeetPipelineResult(
                partial_text="",
                final_text="",
            )

        output = outputs[0]

        partial_text = (
            output.partial_transcript.strip()
            if output.partial_transcript
            else ""
        )

        final_text = (
            output.final_transcript.strip()
            if output.final_transcript
            else ""
        )

        return ParakeetPipelineResult(
            partial_text=partial_text,
            final_text=final_text,
        )

    def close(self) -> None:
        if not self._is_open:
            return

        try:
            self._pipeline.close_session()
        finally:
            self._is_open = False
            self._is_first_frame = True
