# Contributing to TransGo

Thanks for your interest in CaptionLink.

TransGo is under active development, particularly around reusable caption-session orchestration, local GPU speech services, and future cross-platform support.

## Development setup

The current primary development environment is Windows with WSL and an NVIDIA GPU.

Start with:

- [Windows development setup](docs/setup-windows.md)
- [Remote-session protocol](CaptionLink.Remote.Protocol/PROTOCOL.md)
- [End-to-end caption latency](docs/end-to-end-latency.md)
- [VoxConverse overlap benchmark](docs/overlap-benchmark.md)

## Before making changes

Create a branch from the current development base and keep changes focused.

TransGo favors small, reviewable commits that preserve boundaries between:

- platform-neutral application/core code,
- platform-specific UI and audio capture,
- transcription providers,
- diarization providers,
- local service runtimes,
- remote protocol and transport code.

Avoid introducing Windows, WSL, WPF, WASAPI, or provider-specific assumptions into reusable application/core layers unless the abstraction explicitly belongs there.

## Build

From the repository root:

~~~powershell
dotnet restore .\CaptionLink.slnx

dotnet build `
  .\CaptionLink.slnx `
  -c Release
~~~

## Tests

Run the standard .NET test suite:

~~~powershell
dotnet test `
  .\CaptionLink.slnx `
  -c Release `
  --no-build
~~~

The repository also contains standalone overlap validation projects and Chrome protocol tests. CI runs those separately.

Before committing, also run:

~~~powershell
git diff --check
~~~

## Local GPU environment

Use the repository-managed environment rather than installing arbitrary NeMo or Transformers versions globally.

Check the environment with:

~~~powershell
.\scripts\doctor.ps1
~~~

Bootstrap or repair it with:

~~~powershell
wsl bash ./scripts/bootstrap-wsl.sh
~~~

The toolchain revisions are pinned in `config/nemo-toolchain.conf`.

## Generated data and credentials

Do not commit:

- model caches,
- Python virtual environments,
- benchmark datasets,
- generated benchmark results,
- API keys,
- gateway tokens,
- credentials,
- local `.env` files,
- machine-specific paths or development logs.

The repository already ignores the normal locations for these files, but verify staged changes before committing.

## Pull requests

A useful pull request should explain:

1. what changed,
2. why the change is needed,
3. how it was tested,
4. any platform, model, or runtime assumptions introduced by the change.

For behavior changes, add or update tests where practical.

For platform-specific work, keep the platform boundary explicit rather than adding host-specific behavior to reusable projects.

## Issues

Bug reports are most useful when they include:

- TransGo version or commit,
- operating system,
- relevant provider or model,
- whether transcription is local or remote,
- GPU model when relevant,
- reproduction steps,
- expected behavior,
- actual behavior,
- logs that do not contain credentials or private transcript content.

Please do not include access tokens, private credentials, or sensitive captured audio/transcripts in an issue.
