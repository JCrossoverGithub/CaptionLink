# VoxConverse overlap benchmark

This benchmark measures TransGo's production `SpeakerOverlapDetector` against VoxConverse v0.3 reference annotations. It streams each WAV through the same local Sortformer service and C# activity path used by the WPF application.

VoxConverse is research data distributed under CC BY 4.0. The download script fetches the audio and annotations from the official project; no dataset files belong in this repository.

## 1. Download the development set

From PowerShell in the repository root:

```powershell
.\scripts\Download-VoxConverse.ps1 -Split dev
```

This creates ignored local directories:

```text
benchmark-data\VoxConverse\audio\dev
benchmark-data\VoxConverse\rttm\dev
```

Use only the development set while selecting thresholds. Do not inspect or tune against the test set.

## 2. Run a small accelerated smoke test

Close TransGo before starting because the Sortformer service accepts one session at a time.

```powershell
dotnet run `
  --project .\CaptionLink.OverlapBenchmark\CaptionLink.OverlapBenchmark.csproj `
  --configuration Release `
  -- `
  --audio-dir .\benchmark-data\VoxConverse\audio\dev `
  --rttm-dir .\benchmark-data\VoxConverse\rttm\dev `
  --output-dir .\benchmark-results\voxconverse-dev-smoke `
  --max-files 3
```

The first run may take up to 90 seconds to load Sortformer. The benchmark processes recordings sequentially because the service permits one active GPU session.

## 3. Run the complete development set

```powershell
dotnet run `
  --project .\CaptionLink.OverlapBenchmark\CaptionLink.OverlapBenchmark.csproj `
  --configuration Release `
  -- `
  --audio-dir .\benchmark-data\VoxConverse\audio\dev `
  --rttm-dir .\benchmark-data\VoxConverse\rttm\dev `
  --output-dir .\benchmark-results\voxconverse-dev
```

Generated files:

- `overlap-benchmark.json`: incrementally updated aggregate metrics, duration buckets, interval details, failures, and per-recording results
- `overlap-benchmark.csv`: incrementally updated per-recording results for spreadsheet analysis
- `overlap-benchmark-failures.csv`: recordings that failed and their latest errors
- `overlap-benchmark.checkpoint.json`: completed work used by `--resume`

The four files are updated after every recording. If the process is interrupted
or any recording fails, rerun the same command with `--resume`:

```powershell
dotnet run `
  --project .\CaptionLink.OverlapBenchmark\CaptionLink.OverlapBenchmark.csproj `
  --configuration Release `
  --no-build `
  -- `
  --audio-dir .\benchmark-data\VoxConverse\audio\dev `
  --rttm-dir .\benchmark-data\VoxConverse\rttm\dev `
  --output-dir .\benchmark-results\voxconverse-dev `
  --resume
```

Resume skips successful recordings and retries failed ones. The audio paths,
selection, chunk size, collar, speaker capacity, and realtime mode must match
the original command so incompatible results cannot be combined.

The report includes:

- Strict overlap precision, recall, and F1
- Practical precision, recall, and F1 with a 250 ms reference-boundary collar
- False-overlap and missed-overlap seconds per hour
- Median and p95 boundary error
- Median and p95 detector finalization latency in audio time
- Reference-region recall grouped by overlap duration

The practical score excludes the 250 ms region on each side of every reference overlap boundary. It does not expand a prediction until it becomes correct.

## 4. Validate production buffer availability

Accelerated input can queue audio ahead of GPU inference and invalidate a 30-second ring-buffer measurement. Buffer availability is therefore reported only with real-time pacing:

```powershell
dotnet run `
  --project .\CaptionLink.OverlapBenchmark\CaptionLink.OverlapBenchmark.csproj `
  --configuration Release `
  -- `
  --audio-dir .\benchmark-data\VoxConverse\audio\dev `
  --rttm-dir .\benchmark-data\VoxConverse\rttm\dev `
  --output-dir .\benchmark-results\voxconverse-realtime-smoke `
  --max-files 3 `
  --realtime
```

Do not run the entire development set in real time unless the additional elapsed time is intentional.

## 5. Capture and tune from cached Sortformer output

Before freezing the detector, capture Sortformer's raw frame-level
probabilities and tune the lightweight activity/detector logic without
repeating GPU inference.

### Capture reusable probability traces

The completed benchmark report contains finalized overlap intervals, not the
raw probabilities needed for threshold replay. Run the development benchmark
once more with `--trace-dir`. The GPU is required for this capture pass.

```powershell
dotnet run `
  --project .\CaptionLink.OverlapBenchmark\CaptionLink.OverlapBenchmark.csproj `
  --configuration Release `
  -- `
  --audio-dir .\benchmark-data\VoxConverse\audio\dev `
  --rttm-dir .\benchmark-data\VoxConverse\rttm\dev `
  --output-dir .\benchmark-results\voxconverse-dev `
  --trace-dir .\benchmark-traces\voxconverse-dev-v2.1 `
  --resume
```

With `--resume`, a completed recording is skipped only when its probability
trace also exists. Because the original 216-recording run predates trace
capture, the first capture pass will rerun those recordings. Each successful
recording produces one atomic, gzip-compressed file named
`<recording-id>.sortformer-trace.json.gz`.

Normal TransGo and benchmark sessions do not publish raw probabilities unless
`--trace-dir` is present, so production sessions avoid the extra serialization
and WebSocket traffic.

### Replay and rank detector settings on the CPU

```powershell
dotnet run `
  --project .\CaptionLink.OverlapTuner\CaptionLink.OverlapTuner.csproj `
  --configuration Release `
  -- `
  --trace-dir .\benchmark-traces\voxconverse-dev-v2.1 `
  --rttm-dir .\benchmark-data\VoxConverse\rttm\dev `
  --output-dir .\benchmark-results\voxconverse-dev-tuning
```

The default grid evaluates 648 configurations. It sweeps:

- Activity start and stop thresholds
- One or two consecutive onset frames
- Minimum overlap duration
- Detector start and end hysteresis
- Nearby-region merge gap

The tuner ranks configurations by strict F1 and reports practical F1 as a
secondary metric. A recommendation must also keep strict precision and recall
at or above 68%, preserve under-500-ms region recall within 0.5 percentage
points of the replayed baseline, and keep p95 logical finalization latency at
or below 1.36 seconds.

Generated files:

- `overlap-tuning-results.json`: baseline, guardrails, recommendation, and every candidate
- `overlap-tuning-results.csv`: ranked candidate summary
- `recommended-overlap-settings.json`: compact winning configuration for production verification

For a quick end-to-end smoke test, capture a few named recordings with the
benchmark's `--include` option and pass the same IDs to the tuner. The tuner
also accepts comma-separated value lists such as
`--merge-gap-ms 200,320,480` and `--start-thresholds 0.45,0.50,0.55`.

Replay measures detector behavior in audio time. It does not measure GPU
throughput, WebSocket delay, Tailscale latency, or browser caption latency.
After choosing a candidate, implement its settings in the production tracker,
verify difficult development recordings through the live GPU path, run the
small real-time buffer test, freeze the configuration, and only then evaluate
the VoxConverse test split once.

## 6. Freeze and evaluate the test set

The development-set fine sweep selected and froze this validation candidate:

```text
start threshold:       0.51
stop threshold:        0.37
onset frames:          1
minimum overlap:       80 ms
start hysteresis:      80 ms
end hysteresis:        640 ms
merge gap:             200 ms
```

Its immutable settings and predeclared acceptance rules are stored in:

```text
benchmark-config\frozen-overlap-candidate-dev-fine-rank19.json
```

The candidate remains separate from production defaults until it passes the
held-out test. Validation mode rejects every grid option and `--include`,
requires a probability trace for every RTTM file, and evaluates exactly the
production baseline and this candidate. That prevents accidental partial-set
selection or test-set tuning.

Download the test split once:

```powershell
.\scripts\Download-VoxConverse.ps1 -Split test
```

Capture raw test-set probabilities. This is the only GPU pass:

```powershell
dotnet run `
  --project .\CaptionLink.OverlapBenchmark\CaptionLink.OverlapBenchmark.csproj `
  --configuration Release `
  --no-build `
  -- `
  --audio-dir .\benchmark-data\VoxConverse\audio\test `
  --rttm-dir .\benchmark-data\VoxConverse\rttm\test `
  --output-dir .\benchmark-results\voxconverse-test-capture `
  --trace-dir .\benchmark-traces\voxconverse-test-v2.1
```

If interrupted, rerun that exact command with `--resume`. After every test
recording has a trace, perform the locked CPU comparison:

```powershell
dotnet run `
  --project .\CaptionLink.OverlapTuner\CaptionLink.OverlapTuner.csproj `
  --configuration Release `
  --no-build `
  -- `
  --trace-dir .\benchmark-traces\voxconverse-test-v2.1 `
  --rttm-dir .\benchmark-data\VoxConverse\rttm\test `
  --output-dir .\benchmark-results\voxconverse-test-validation `
  --validate-candidate .\benchmark-config\frozen-overlap-candidate-dev-fine-rank19.json
```

Generated files:

- `overlap-validation-results.json`: frozen provenance, both metric sets,
  deltas, every pass/fail check, and the final verdict
- `overlap-validation-comparison.csv`: compact production-versus-candidate
  comparison

The frozen candidate passes only if strict F1 is at least 70%, strict precision
and recall are each at least 68%, under-500-ms recall drops by no more than 0.5
percentage points from the test-set production baseline, p95 logical
finalization latency stays at or below 1.36 seconds, and strict F1 improves over
the test-set production baseline. A failure is a valid experimental result; do
not adjust the candidate using test-set feedback.

Record the commit hash and exact commands alongside the final metrics so the
result remains reproducible.
