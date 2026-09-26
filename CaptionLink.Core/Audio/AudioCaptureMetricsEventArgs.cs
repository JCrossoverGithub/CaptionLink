using System;

namespace CaptionLink.Core.Audio;

public sealed class AudioCaptureMetricsEventArgs : EventArgs
{
    public AudioCaptureMetricsEventArgs(
        double levelPercent,
        long capturedBytes)
    {
        LevelPercent = levelPercent;
        CapturedBytes = capturedBytes;
    }

    public double LevelPercent { get; }

    public long CapturedBytes { get; }
}