# End-to-end caption latency

TransGo measures caption latency without comparing wall clocks on the Chrome
client and Windows gateway. The two devices can have different clock offsets,
so subtracting their UTC timestamps would produce misleading results.

## Measurement boundaries

Each caption is correlated with the 100 ms audio chunk that reached the result's
audio end position. The session records:

- Audio chunk sequence and session-relative start/end position.
- Browser time when the completed chunk became available to the offscreen page.
- Gateway time from receiving that chunk to generating the caption.
- Gateway time from dispatching that chunk to generating the caption.
- Parakeet model-operation time reported by the Python service.
- Browser time from chunk completion to receiving the caption.
- Browser time from receiving the caption to updating the caption DOM.
- Browser time from chunk completion to updating the caption DOM.

The first audio chunk actually sent after `session_started` establishes zero on
the wire timeline. Chunks produced while the gateway is still connecting are
not sent and therefore do not create an offset between the browser, gateway,
and Parakeet audio positions.

The browser values use `performance.timeOrigin + performance.now()` and never
cross devices. Gateway durations use `Stopwatch.GetTimestamp()`. Parakeet uses
`time.perf_counter()`. All three are monotonic clocks.

`captureToDisplayMilliseconds` is the primary user-visible metric. It begins
when the caption-relevant 100 ms chunk is complete, so it does not include the
time spent collecting that chunk. Add approximately 100 ms when comparing it
with a definition that starts at the first sample in the chunk.

## Session report

When a Chrome caption session stops, the extension stores a report containing
P50, P95, and P99 values for each available boundary. The popup displays the
last session's capture-to-display P50 and P95. The complete report is available
in the extension's local storage under `lastLatencyReport` and contains metrics
only; it does not retain audio or transcript text.

The extension retains at most 5,000 received and displayed caption samples per
session. Per-caption samples are also written to the tab console while the
instrumented prototype is running.

## Tests

```powershell
dotnet test .\CaptionLink.Remote.Protocol.Tests\CaptionLink.Remote.Protocol.Tests.csproj --configuration Release
node --test .\CaptionLink.ChromeExtension\tests\protocol.test.mjs .\CaptionLink.ChromeExtension\tests\latency.test.mjs .\CaptionLink.ChromeExtension\tests\caption-state.test.mjs
python -m py_compile .\CaptionLink.LocalAsr.Parakeet\parakeet_service.py
```
