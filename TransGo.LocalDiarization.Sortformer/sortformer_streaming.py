from __future__ import annotations

import torch
from nemo.collections.asr.models import SortformerEncLabelModel


MODEL_NAME = "nvidia/diar_streaming_sortformer_4spk-v2.1"
MODEL_SAMPLE_RATE = 16000
MAXIMUM_SPEAKERS = 4
PREDICTION_FRAME_DURATION_SECONDS = 0.08

# Low-latency profile validated locally on the TransGo development machine.
CHUNK_LENGTH = 6
CHUNK_RIGHT_CONTEXT = 7
FIFO_LENGTH = 188
SPEAKER_CACHE_UPDATE_PERIOD = 144
SPEAKER_CACHE_LENGTH = 188


def build_sortformer_model() -> SortformerEncLabelModel:
    if not torch.cuda.is_available():
        raise RuntimeError(
            "CUDA is not available. Sortformer requires the NVIDIA GPU."
        )

    model = SortformerEncLabelModel.from_pretrained(
        MODEL_NAME
    )

    model.eval()
    model = model.cuda()

    modules = model.sortformer_modules

    modules.chunk_len = CHUNK_LENGTH
    modules.chunk_right_context = CHUNK_RIGHT_CONTEXT
    modules.fifo_len = FIFO_LENGTH
    modules.spkcache_update_period = (
        SPEAKER_CACHE_UPDATE_PERIOD
    )
    modules.spkcache_len = SPEAKER_CACHE_LENGTH

    modules._check_streaming_parameters()

    torch.cuda.synchronize()

    return model
