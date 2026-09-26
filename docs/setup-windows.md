# Windows development setup

This guide covers the current Windows development environment for CaptionLink, including the local NVIDIA GPU services that run through WSL.

## Requirements

Install or provide:

- Windows with WSL enabled
- A default WSL distribution with working NVIDIA GPU access
- Git
- .NET SDK 10
- NVIDIA Windows drivers with WSL CUDA support
- `python3`, `uv`, `git`, `curl`, and `ffmpeg` inside WSL

The current repository pins the .NET SDK through `global.json`.

The GPU Python stack is installed by the repository bootstrap script rather than by a system-wide Python environment.

## 1. Clone the repository

~~~powershell
git clone https://github.com/JCrossoverGithub/CaptionLink.git
cd CaptionLink
~~~

During private development, clone using whatever GitHub authentication method your account requires.

## 2. Check the Windows and WSL environment

Run:

~~~powershell
.\scripts\doctor.ps1
~~~

The doctor checks:

- Git
- .NET
- WSL
- installed WSL distributions
- NVIDIA GPU visibility on Windows
- NVIDIA GPU visibility inside WSL
- Python
- `uv`
- `ffmpeg`
- the installed CaptionLink NeMo environment
- the Nemotron 3 Transformers overlay

Before the model runtime has been bootstrapped, the final checks are expected to report that the environment is not installed.

## 3. Bootstrap the GPU runtime

Run from the repository root:

~~~powershell
wsl bash ./scripts/bootstrap-wsl.sh
~~~

The bootstrap reads the pinned revisions from:

~~~text
config/nemo-toolchain.conf
~~~

It then:

1. creates the CaptionLink data directory,
2. clones NVIDIA NeMo Speech,
3. checks out the pinned NeMo commit,
4. creates and synchronizes the pinned Python/CUDA environment with `uv`,
5. installs CaptionLink's FastAPI/Uvicorn/WebSocket service dependencies,
6. checks out the pinned Hugging Face Transformers revision,
7. builds the dependency overlay used by the Nemotron 3 streaming implementation,
8. verifies package versions,
9. verifies CUDA access,
10. loads and validates the Nemotron 3 streaming runtime.

By default the runtime is stored under:

~~~text
~/.local/share/transgo/
~~~

If `XDG_DATA_HOME` is set, that location is used instead.

The main Python environment is therefore normally:

~~~text
~/.local/share/transgo/nemo-speech/.venv/
~~~

The bootstrap is intentionally separate from the repository clone. Large third-party repositories, environments, model caches, and generated data are not committed to CaptionLink.

## 4. Re-run the doctor

After bootstrap:

~~~powershell
.\scripts\doctor.ps1
~~~

The WSL runtime checks should now complete successfully.

## 5. Restore and build CaptionLink

~~~powershell
dotnet restore .\CaptionLink.slnx

dotnet build `
  .\CaptionLink.slnx `
  -c Release
~~~

## 6. Run the tests

~~~powershell
dotnet test `
  .\CaptionLink.slnx `
  -c Release `
  --no-build
~~~

Some benchmark and integration-style validation tools are separate executable projects and are run independently or through CI.

## 7. Launch the Windows application

~~~powershell
dotnet run `
  --project .\CaptionLink.Windows\CaptionLink.Windows.csproj `
  -c Release
~~~

CaptionLink starts the required local GPU service when a corresponding provider is selected.

Current localhost service ports are:

| Service | Port |
| --- | ---: |
| Parakeet | 8765 |
| Sortformer | 8766 |
| Nemotron 3 | 8767 |
| Multitalker Parakeet | 8768 |

The launchers expect the runtime created by `bootstrap-wsl.sh`.

## Repository discovery

The Windows WSL runtime attempts to locate the repository by searching upward from the current working directory and application base directory for:

~~~text
CaptionLink.slnx
~~~

If CaptionLink is launched from somewhere that prevents automatic discovery, set the Windows environment variable:

~~~text
TRANSGO_REPOSITORY_ROOT
~~~

to the repository root.

Example:

~~~powershell
$env:TRANSGO_REPOSITORY_ROOT =
    "C:\Users\you\projects\CaptionLink"
~~~

No WSL distribution name is hard-coded. CaptionLink uses the user's default WSL distribution.

## Local GPU runtime locations

With the default XDG data location:

~~~text
~/.local/share/transgo/nemo-speech/
~/.local/share/transgo/transformers-nemotron-main/
~/.local/share/transgo/transformers-nemotron-deps/
~~~

The service launchers use the Python executable at:

~~~text
${XDG_DATA_HOME:-$HOME/.local/share}/transgo/nemo-speech/.venv/bin/python
~~~

Nemotron additionally loads the pinned Transformers overlay from:

~~~text
${XDG_DATA_HOME:-$HOME/.local/share}/transgo/transformers-nemotron-deps
~~~

## Remote captioning

Running the remote GPU gateway requires:

~~~text
TRANSGO_GATEWAY_TOKEN
~~~

Remote CaptionLink clients use:

~~~text
TRANSGO_REMOTE_GATEWAY_URL
TRANSGO_REMOTE_GATEWAY_TOKEN
~~~

Keep real credentials outside source control.

See [the remote-session protocol](../CaptionLink.Remote.Protocol/PROTOCOL.md) for protocol details.

## Troubleshooting

Start with:

~~~powershell
.\scripts\doctor.ps1
~~~

If the local GPU environment is missing or inconsistent, rerun:

~~~powershell
wsl bash ./scripts/bootstrap-wsl.sh
~~~

If the repository cannot be located by a service launcher, set `TRANSGO_REPOSITORY_ROOT`.

If CUDA is not visible inside WSL, resolve the Windows/WSL NVIDIA configuration before debugging the CaptionLink services themselves.
