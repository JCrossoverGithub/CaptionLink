from __future__ import annotations

import inspect

from nemo.collections.asr.models import SortformerEncLabelModel


MODEL_NAME = "nvidia/diar_streaming_sortformer_4spk-v2.1"


print("Loading Sortformer...")

model = SortformerEncLabelModel.from_pretrained(
    MODEL_NAME
)

print()
print("Relevant state/cache members")
print("----------------------------")

keywords = (
    "state",
    "cache",
    "fifo",
    "stream",
)

for name in sorted(dir(model)):
    if name.startswith("__"):
        continue

    if any(
        keyword in name.casefold()
        for keyword in keywords
    ):
        value = getattr(
            model,
            name,
        )

        if callable(value):
            try:
                signature = inspect.signature(
                    value
                )
            except (TypeError, ValueError):
                signature = "(signature unavailable)"

            print(
                f"{name}{signature}"
            )
        else:
            print(
                f"{name}: {type(value).__name__}"
            )

print()
print("forward_streaming source")
print("------------------------")
print(
    inspect.getsource(
        model.forward_streaming
    )
)

print()
print("forward_streaming_step source")
print("-----------------------------")
print(
    inspect.getsource(
        model.forward_streaming_step
    )
)

print()
print("streaming_input_examples source")
print("-------------------------------")
print(
    inspect.getsource(
        model.streaming_input_examples
    )
)
