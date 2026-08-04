from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

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
MODEL_FRAME_DURATION_SECONDS = 0.16
MODEL_FRAME_SAMPLE_COUNT = int(
    MODEL_SAMPLE_RATE * MODEL_FRAME_DURATION_SECONDS
)

DEFAULT_CONFIG_PATH = (
    Path.home()
    / "NeMo-Speech"
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


def build_parakeet_pipeline(
    config_path: Path = DEFAULT_CONFIG_PATH,
):
    """Build the persistent buffered-streaming Parakeet pipeline."""

    config_path = config_path.expanduser().resolve()

    if not config_path.is_file():
        raise FileNotFoundError(
            f"NeMo streaming config not found: {config_path}"
        )

    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is not available to PyTorch.")

    cfg = OmegaConf.load(config_path)

    with open_dict(cfg):
        cfg.asr.model_name = MODEL_NAME
        cfg.asr.device = "cuda"
        cfg.asr.device_id = 0
        cfg.asr.compute_dtype = "float16"
        cfg.asr.use_amp = False

        cfg.streaming.sample_rate = MODEL_SAMPLE_RATE
        cfg.streaming.batch_size = 1
        cfg.streaming.left_padding_size = 5.60
        cfg.streaming.chunk_size = MODEL_FRAME_DURATION_SECONDS
        cfg.streaming.right_padding_size = 0.40
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

    return PipelineBuilder.build_pipeline(cfg)


class ParakeetStreamingSession:
    """Owns one live streaming session for a connected TransGo client."""

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
            0 <= audio_frame.valid_length <= MODEL_FRAME_SAMPLE_COUNT
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

        if not outputs:
            return ParakeetPipelineResult(
                partial_text="",
                final_text="",
            )

        output = outputs[0]
        self._is_first_frame = False

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
