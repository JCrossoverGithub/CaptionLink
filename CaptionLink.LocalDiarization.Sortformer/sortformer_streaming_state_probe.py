from __future__ import annotations

import inspect

import torch

from nemo.collections.asr.models import SortformerEncLabelModel


MODEL_NAME = "nvidia/diar_streaming_sortformer_4spk-v2.1"
SAMPLE_RATE = 16_000
WINDOW_SAMPLE_COUNT = 16_640


def describe_state(state) -> None:
    print("State type:", type(state).__name__)

    for name in (
        "spkcache",
        "spkcache_lengths",
        "spkcache_preds",
        "fifo",
        "fifo_lengths",
        "fifo_preds",
        "spk_perm",
    ):
        value = getattr(
            state,
            name,
            None,
        )

        if isinstance(value, torch.Tensor):
            print(
                f"{name}: "
                f"shape={tuple(value.shape)}, "
                f"dtype={value.dtype}, "
                f"device={value.device}"
            )
        else:
            print(
                f"{name}: "
                f"{type(value).__name__} = {value}"
            )


print("Loading Sortformer...")

model = SortformerEncLabelModel.from_pretrained(
    MODEL_NAME
)

model.eval()
model = model.cuda()

modules = model.sortformer_modules

modules.chunk_len = 6
modules.chunk_right_context = 7
modules.fifo_len = 188
modules.spkcache_update_period = 144
modules.spkcache_len = 188
modules._check_streaming_parameters()

print()
print("init_streaming_state source")
print("---------------------------")
print(
    inspect.getsource(
        modules.init_streaming_state
    )
)

print()
print("streaming_feat_loader source")
print("----------------------------")
print(
    inspect.getsource(
        modules.streaming_feat_loader
    )
)

audio = torch.zeros(
    1,
    WINDOW_SAMPLE_COUNT,
    dtype=torch.float32,
    device="cuda",
)

audio_length = torch.tensor(
    [WINDOW_SAMPLE_COUNT],
    dtype=torch.long,
    device="cuda",
)

with torch.inference_mode():
    features, feature_lengths = model.preprocessor(
        input_signal=audio,
        length=audio_length,
    )

print()
print("Preprocessor output")
print("-------------------")
print("features:", tuple(features.shape))
print("feature_lengths:", feature_lengths.tolist())

state = modules.init_streaming_state(
    batch_size=1,
    async_streaming=model.async_streaming,
    device=model.device,
)

print()
print("Initial streaming state")
print("-----------------------")
describe_state(state)

offset = torch.zeros(
    (1,),
    dtype=torch.long,
    device=model.device,
)

loader = modules.streaming_feat_loader(
    feat_seq=features,
    feat_seq_length=feature_lengths,
    feat_seq_offset=offset,
)

total_preds = torch.zeros(
    (
        1,
        0,
        modules.n_spk,
    ),
    dtype=torch.float32,
    device=model.device,
)

print()
print("Streaming-loader steps")
print("----------------------")

with torch.inference_mode():
    for (
        step_index,
        chunk,
        chunk_lengths,
        left_offset,
        right_offset,
    ) in loader:
        print(
            f"step={step_index}, "
            f"chunk={tuple(chunk.shape)}, "
            f"lengths={chunk_lengths.tolist()}, "
            f"left_offset={left_offset}, "
            f"right_offset={right_offset}"
        )

        state, total_preds = (
            model.forward_streaming_step(
                processed_signal=chunk,
                processed_signal_length=chunk_lengths,
                streaming_state=state,
                total_preds=total_preds,
                left_offset=left_offset,
                right_offset=right_offset,
            )
        )

        print(
            "total_preds:",
            tuple(total_preds.shape),
        )

print()
print("Updated streaming state")
print("-----------------------")
describe_state(state)

print()
print("Final predictions")
print("-----------------")
print("Shape:", tuple(total_preds.shape))

if total_preds.numel() > 0:
    print(
        "Minimum:",
        total_preds.min().item(),
    )
    print(
        "Maximum:",
        total_preds.max().item(),
    )
