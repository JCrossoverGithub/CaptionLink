from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import torch
from nemo.collections.asr.models import SortformerEncLabelModel

from sortformer_audio import SortformerAudioWindow


MODEL_NAME = "nvidia/diar_streaming_sortformer_4spk-v2.1"
MODEL_SAMPLE_RATE = 16_000
MAXIMUM_SPEAKERS = 4
PREDICTION_FRAME_DURATION_SECONDS = 0.08
PREDICTION_FRAME_SAMPLE_COUNT = round(
    MODEL_SAMPLE_RATE
    * PREDICTION_FRAME_DURATION_SECONDS
)

# Low-latency profile validated on the CaptionLink development machine.
CHUNK_LENGTH = 6
CHUNK_RIGHT_CONTEXT = 7
FIFO_LENGTH = 188
SPEAKER_CACHE_UPDATE_PERIOD = 144
SPEAKER_CACHE_LENGTH = 188

MODEL_PREDICTION_HOP_DURATION_SECONDS = (
    CHUNK_LENGTH
    * PREDICTION_FRAME_DURATION_SECONDS
)
MODEL_PREDICTION_HOP_SAMPLE_COUNT = round(
    MODEL_SAMPLE_RATE
    * MODEL_PREDICTION_HOP_DURATION_SECONDS
)

MODEL_INPUT_WINDOW_DURATION_SECONDS = (
    CHUNK_LENGTH + CHUNK_RIGHT_CONTEXT
) * PREDICTION_FRAME_DURATION_SECONDS

MODEL_INPUT_WINDOW_SAMPLE_COUNT = round(
    MODEL_SAMPLE_RATE
    * MODEL_INPUT_WINDOW_DURATION_SECONDS
)


@dataclass(frozen=True)
class SortformerPredictionBatch:
    start_frame_index: int
    probabilities: np.ndarray

    @property
    def frame_count(self) -> int:
        return int(
            self.probabilities.shape[0]
        )


class SortformerStreamingSession:
    """
    Owns one persistent Sortformer speaker cache and FIFO.

    Each audio window contains one 0.48-second prediction hop
    plus 0.56 seconds of right context. Only the first loader
    step is processed because the following overlapping window
    advances by exactly one prediction hop.
    """

    def __init__(
        self,
        model: SortformerEncLabelModel,
    ) -> None:
        self._model = model
        self._modules = model.sortformer_modules

        self._streaming_state = None
        self._maximum_speakers = MAXIMUM_SPEAKERS
        self._next_prediction_frame_index = 0
        self._is_open = False

    @property
    def is_open(self) -> bool:
        return self._is_open

    @property
    def prediction_frame_count(self) -> int:
        return self._next_prediction_frame_index

    def open(
        self,
        maximum_speakers: int = MAXIMUM_SPEAKERS,
    ) -> None:
        if self._is_open:
            raise RuntimeError(
                "The Sortformer streaming session is already open."
            )

        if not (
            1
            <= maximum_speakers
            <= MAXIMUM_SPEAKERS
        ):
            raise ValueError(
                "Maximum speakers must be between "
                f"1 and {MAXIMUM_SPEAKERS}."
            )

        self._streaming_state = (
            self._modules.init_streaming_state(
                batch_size=1,
                async_streaming=(
                    self._model.async_streaming
                ),
                device=self._model.device,
            )
        )

        self._maximum_speakers = maximum_speakers
        self._next_prediction_frame_index = 0
        self._is_open = True

    def process(
        self,
        window: SortformerAudioWindow,
    ) -> SortformerPredictionBatch:
        if not self._is_open:
            raise RuntimeError(
                "The Sortformer streaming session is not open."
            )

        if self._streaming_state is None:
            raise RuntimeError(
                "The Sortformer streaming state is unavailable."
            )

        if (
            window.samples.ndim != 1
            or window.samples.size
            != MODEL_INPUT_WINDOW_SAMPLE_COUNT
        ):
            raise ValueError(
                "Sortformer audio windows must contain exactly "
                f"{MODEL_INPUT_WINDOW_SAMPLE_COUNT} samples."
            )

        if not (
            0
            < window.valid_length
            <= window.samples.size
        ):
            raise ValueError(
                "The Sortformer window valid length is invalid."
            )

        requested_frame_count = (
            window.prediction_frame_count
        )

        if requested_frame_count <= 0:
            return SortformerPredictionBatch(
                start_frame_index=(
                    self._next_prediction_frame_index
                ),
                probabilities=np.empty(
                    (
                        0,
                        self._maximum_speakers,
                    ),
                    dtype=np.float32,
                ),
            )

        audio = (
            torch.from_numpy(
                np.asarray(
                    window.samples,
                    dtype=np.float32,
                )
            )
            .unsqueeze(0)
            .to(
                device=self._model.device,
                dtype=torch.float32,
            )
        )

        # Flush windows are padded with silence to the normal model
        # window size. Treat that padding as model context so NeMo
        # produces the requested prediction frames; only
        # requested_frame_count frames are published below, so the
        # padded tail never extends the session timeline.
        audio_length = torch.tensor(
            [window.samples.size],
            dtype=torch.long,
            device=self._model.device,
        )

        with torch.inference_mode():
            features, feature_lengths = (
                self._model.preprocessor(
                    input_signal=audio,
                    length=audio_length,
                )
            )

            feature_offset = torch.zeros(
                (1,),
                dtype=torch.long,
                device=self._model.device,
            )

            loader = (
                self._modules.streaming_feat_loader(
                    feat_seq=features,
                    feat_seq_length=feature_lengths,
                    feat_seq_offset=feature_offset,
                )
            )

            try:
                (
                    step_index,
                    chunk,
                    chunk_lengths,
                    left_offset,
                    right_offset,
                ) = next(loader)
            except StopIteration as exception:
                raise RuntimeError(
                    "Sortformer produced no streaming feature step."
                ) from exception

            if step_index != 0:
                raise RuntimeError(
                    "The first Sortformer streaming step had "
                    "an unexpected index."
                )

            empty_predictions = torch.zeros(
                (
                    1,
                    0,
                    self._modules.n_spk,
                ),
                dtype=torch.float32,
                device=self._model.device,
            )

            (
                self._streaming_state,
                prediction_tensor,
            ) = self._model.forward_streaming_step(
                processed_signal=chunk,
                processed_signal_length=(
                    chunk_lengths
                ),
                streaming_state=(
                    self._streaming_state
                ),
                total_preds=empty_predictions,
                left_offset=left_offset,
                right_offset=right_offset,
            )

            if (
                prediction_tensor.shape[1]
                < requested_frame_count
            ):
                raise RuntimeError(
                    "Sortformer returned fewer prediction "
                    "frames than the audio window requires."
                )

            selected = prediction_tensor[
                0,
                :requested_frame_count,
                : self._maximum_speakers,
            ]

            probabilities = (
                selected
                .detach()
                .to(
                    device="cpu",
                    dtype=torch.float32,
                )
                .numpy()
                .copy()
            )

        start_frame_index = (
            self._next_prediction_frame_index
        )

        self._next_prediction_frame_index += (
            requested_frame_count
        )

        return SortformerPredictionBatch(
            start_frame_index=start_frame_index,
            probabilities=probabilities,
        )

    def close(self) -> None:
        self._streaming_state = None
        self._next_prediction_frame_index = 0
        self._is_open = False


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
