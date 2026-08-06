# TransGo Chrome extension prototype

This Manifest V3 extension captures audio from the user-selected active tab,
keeps that audio playing normally, resamples it to 16 kHz mono PCM, and sends
100 ms chunks to the existing TransGo GPU gateway. Interim and final captions
are rendered over the page and move into the active fullscreen element.

## Load the unpacked extension

1. Open `chrome://extensions`.
2. Enable **Developer mode**.
3. Choose **Load unpacked**.
4. Select the `TransGo.ChromeExtension` directory.

The GPU gateway and Tailscale Serve must already be running. Open a normal
HTTP/HTTPS tab with audio, select the extension, enter the Tailscale HTTPS URL
and gateway token, then choose **Start captions**.

The token is stored in `chrome.storage.local` for this private prototype. Do
not distribute this build or reuse one shared gateway token for public users.
Production access requires per-user, short-lived credentials and server-side
authorization limits.

## Protocol tests

With Node.js 20 or newer installed:

```powershell
node --test .\TransGo.ChromeExtension\tests\protocol.test.mjs
```
