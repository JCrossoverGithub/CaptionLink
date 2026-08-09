# TransGo Desktop

TransGo Desktop is a Windows application that captures desktop output audio and produces real-time on-screen captions.

## Current prototype

- WPF desktop application
- Selectable Windows output device
- WASAPI loopback capture through NAudio
- Live audio-level monitoring

## Planned functionality

- Real-time speech transcription
- Modern always-on-top caption overlay
- Finalized transcript history
- Session recovery and reconnection
- Windows and macOS clients

## Overlap benchmark

The [VoxConverse benchmark guide](docs/overlap-benchmark.md) explains how to measure the production Sortformer overlap path against v0.3 reference annotations without committing the dataset or generated results.

## Remote caption latency

The [end-to-end latency guide](docs/end-to-end-latency.md) documents the monotonic capture-to-display measurements across the Chrome client, GPU gateway, and Parakeet service.
