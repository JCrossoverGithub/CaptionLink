# TransGo Chrome extension prototype

This Manifest V3 extension captures audio from the user-selected active tab,
keeps that audio playing normally, resamples it to 16 kHz mono PCM, and sends
100 ms chunks to the existing TransGo GPU gateway. Interim and final captions
are composed by segment, rendered over the page, and move into the active
fullscreen element. Interim updates are rate-limited so corrections do not
make the overlay flicker between finalized and partial text.

## Load the unpacked extension

1. Open `chrome://extensions`.
2. Enable **Developer mode**.
3. Choose **Load unpacked**.
4. Select the `CaptionLink.ChromeExtension` directory.

The GPU gateway and Tailscale Serve must already be running. Open a normal
HTTP/HTTPS tab with audio, select the extension, enter the Tailscale HTTPS URL
and gateway token, then choose **Start captions**.

The token is stored in `chrome.storage.local` for this private prototype. Do
not distribute this build or reuse one shared gateway token for public users.
Production access requires per-user, short-lived credentials and server-side
authorization limits.

## Latency report

The extension records capture-to-receive and capture-to-display latency using
monotonic timing on the Chromebook, plus gateway and Parakeet processing
durations measured on the GPU computer. After a session stops, the popup shows
the last session's P50 and P95 capture-to-display latency. See
[`docs/end-to-end-latency.md`](../docs/end-to-end-latency.md) for the exact
measurement boundaries.

## Protocol tests

With Node.js 20 or newer installed:

```powershell
node --test .\CaptionLink.ChromeExtension\tests\protocol.test.mjs .\CaptionLink.ChromeExtension\tests\latency.test.mjs .\CaptionLink.ChromeExtension\tests\caption-state.test.mjs
```
