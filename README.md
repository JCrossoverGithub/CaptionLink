# CaptionLink

CaptionLink is a real-time captioning system for desktop and browser audio.

The current Windows application captures system output audio and produces live on-screen captions using local or remote speech-recognition providers. Its primary local mode supports multi-speaker captions on an NVIDIA GPU, while the project also includes standalone speaker-attribution pipelines, a remote GPU gateway, a Chrome client, latency instrumentation, and diarization benchmarking tools.

## Current status

The Windows desktop application is the primary supported host today.

CaptionLink is currently being refactored so caption-session behavior, speech-provider contracts, diarization, and local-service orchestration are separated from Windows-specific UI and audio capture. That work is intended to make future macOS and Linux hosts possible without duplicating the captioning pipeline.

macOS and Linux desktop applications are not released yet.

## Features

### Live desktop captions

- Captures a selected Windows output device
- Displays interim and finalized captions in real time
- Provides an always-on-top caption overlay
- Maintains transcript history during the session
- Handles service disconnects cleanly and supports session restart/recovery
- Reports live capture and transcription diagnostics

### Local GPU captioning

The current local GPU options include:

- **Multitalker Parakeet** — the default multi-speaker mode
- **Parakeet GPU** — standard local streaming transcription with selectable quality profiles
- **Nemotron 3** — local speaker attribution
- **Sortformer** — local speaker attribution and overlap research path

Multitalker mode performs speaker detection internally using Nemotron 3, so an additional external diarization provider is not required.

The local model services run in the configured local-service runtime. The Windows host currently uses WSL.

### Other transcription providers

The Windows application also contains integrations for:

- **Remote — CaptionLink GPU Gateway**
- **Google Cloud**
- **Local — Sherpa Streaming**

These providers remain available as alternate or development paths. Current local development is focused primarily on the Parakeet and Multitalker GPU paths.

### Remote caption streaming

CaptionLink includes a WebSocket-based GPU gateway and a Chrome extension prototype.

A client can capture audio on another device, stream normalized PCM audio to a GPU machine, and receive interim and final captions in real time. The remote protocol includes timing metadata used for end-to-end latency measurement.

See:

- [Remote-session protocol](CaptionLink.Remote.Protocol/PROTOCOL.md)
- [End-to-end caption latency](docs/end-to-end-latency.md)
- [Chrome extension prototype](CaptionLink.ChromeExtension/README.md)

### Speaker overlap and diarization research

The repository contains a reproducible VoxConverse evaluation path for measuring and tuning speaker-overlap behavior.

See [VoxConverse overlap benchmark](docs/overlap-benchmark.md).

## Platform status

| Platform | Status |
| --- | --- |
| Windows desktop | Supported development host |
| Chrome remote client | Developer prototype |
| macOS desktop | Planned; portability work in progress |
| Linux desktop | Planned; portability work in progress |

The cross-platform work is intentionally not a WPF port. Reusable captioning behavior is being moved into platform-neutral projects while each desktop host supplies its own UI, audio capture, and runtime composition.

## Architecture

CaptionLink is split into reusable application/core layers and platform/provider implementations.

~~~text
CaptionLink.Windows
    Windows UI
    WASAPI audio capture
    Windows/WSL composition
            |
            v
CaptionLink.Application
    caption-session lifecycle
    normalized audio routing
    transcription + diarization coordination
            |
            v
CaptionLink.Core
    audio models
    transcription contracts
    diarization contracts
    runtime contracts
            |
            +----------------------+
            |                      |
            v                      v
    speech providers        diarization providers
~~~

Local GPU services are separate Python processes exposed over localhost HTTP/WebSocket endpoints. The desktop application starts and monitors them through an `ILocalServiceRuntime` abstraction rather than embedding WSL assumptions into the engines themselves.

On Windows, the current implementation uses WSL. Future hosts can provide a different runtime implementation.

## Repository layout

Some of the main projects are:

| Project | Purpose |
| --- | --- |
| `CaptionLink.Windows` | Windows WPF desktop host |
| `CaptionLink.Application` | Reusable live caption-session orchestration |
| `CaptionLink.Core` | Shared audio, transcription, diarization, and runtime contracts |
| `CaptionLink.Audio.Windows` | WASAPI/Windows audio capture |
| `CaptionLink.Audio.Processing` | Shared audio normalization and processing |
| `CaptionLink.Speech.Parakeet` | Parakeet and Multitalker transcription engines/launchers |
| `CaptionLink.Diarization.Nemotron` | Nemotron 3 diarization integration |
| `CaptionLink.Diarization.Sortformer` | Sortformer diarization integration |
| `CaptionLink.Speech.Remote` | Remote GPU transcription client |
| `CaptionLink.GpuGateway` | Remote transcription gateway |
| `CaptionLink.Remote.Protocol` | Shared remote-session protocol |
| `CaptionLink.ChromeExtension` | Browser captioning prototype |
| `CaptionLink.OverlapBenchmark` | Speaker-overlap evaluation tooling |

The `CaptionLink.LocalAsr.*` and `CaptionLink.LocalDiarization.*` directories contain the Python services used by the local GPU integrations.

## Development requirements

For the Windows desktop and local NVIDIA GPU path:

- Windows with WSL available
- NVIDIA GPU with working WSL GPU access
- .NET SDK 10
- Git
- Python 3 and `uv` inside WSL
- `ffmpeg` inside WSL for development and benchmark tooling

The repository pins the expected .NET SDK in `global.json` and pins the NeMo/Transformers toolchain in `config/nemo-toolchain.conf`.

## Windows development setup

Detailed setup instructions are in [docs/setup-windows.md](docs/setup-windows.md).

The short version is:

~~~powershell
git clone https://github.com/JCrossoverGithub/CaptionLink.git
cd CaptionLink

.\scripts\doctor.ps1
wsl bash ./scripts/bootstrap-wsl.sh

dotnet restore .\CaptionLink.slnx
dotnet build .\CaptionLink.slnx -c Release
dotnet test .\CaptionLink.slnx -c Release --no-build
~~~

`bootstrap-wsl.sh` creates the pinned local GPU runtime under the user's XDG data directory, normally:

~~~text
~/.local/share/transgo/
~~~

It installs the pinned NeMo environment, CaptionLink's service dependencies, and the pinned Transformers/Nemotron overlay, then validates CUDA and the required model runtime.

The first bootstrap requires network access and may take substantial time because GPU dependencies and model components are large.

## Running the Windows application

From the repository root:

~~~powershell
dotnet run `
  --project .\CaptionLink.Windows\CaptionLink.Windows.csproj `
  -c Release
~~~

For repository discovery, CaptionLink normally finds `CaptionLink.slnx` by walking upward from the current directory or application directory.

If needed, set:

~~~text
CAPTIONLINK_REPOSITORY_ROOT
~~~

to the Windows path of the repository clone.

## Local service endpoints

The development services currently bind only to localhost:

| Service | Port |
| --- | ---: |
| Parakeet | `8765` |
| Sortformer | `8766` |
| Nemotron 3 | `8767` |
| Multitalker Parakeet | `8768` |

These endpoints are implementation details for local development rather than public network services.

## Remote gateway configuration

The GPU gateway requires:

~~~text
CAPTIONLINK_GATEWAY_TOKEN
~~~

Remote CaptionLink clients can use:

~~~text
CAPTIONLINK_REMOTE_GATEWAY_URL
CAPTIONLINK_REMOTE_GATEWAY_TOKEN
~~~

Do not commit real tokens or credentials to the repository.

The Chrome extension currently stores its development gateway token locally in Chrome and should be treated as a private/development authentication path rather than a production multi-user security model.

## Tests

Build and run the standard .NET test projects with:

~~~powershell
dotnet build .\CaptionLink.slnx -c Release
dotnet test .\CaptionLink.slnx -c Release --no-build
~~~

Additional benchmark/test runners and Chrome protocol tests are exercised by CI.

## Reproducible model environment

The local GPU runtime is defined by:

- `config/nemo-toolchain.conf`
- `config/service-runtime-requirements.txt`
- `config/hf-nemotron-overlay-requirements.txt`
- `scripts/bootstrap-wsl.sh`
- `scripts/doctor-wsl.sh`

The bootstrap checks out pinned revisions of NVIDIA NeMo Speech and Hugging Face Transformers rather than relying on whatever versions happen to be installed globally.

## Releases

Versioned tags matching `v*.*.*` trigger the GitHub release workflow, which publishes a self-contained Windows x64 archive.

The repository also contains dated research/validation tags used to preserve known-good milestones. Those tags are development checkpoints and are separate from versioned product releases.

## Development direction

The current architecture work is focused on:

1. moving live caption-session orchestration out of the WPF host,
2. keeping reusable speech and diarization components platform-neutral,
3. reducing Windows/WSL assumptions to host-level composition,
4. preparing native macOS and Linux hosts,
5. improving public setup, CI, packaging, and release reproducibility.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and contribution guidance.

## License

CaptionLink is licensed under the [MIT License](LICENSE).
