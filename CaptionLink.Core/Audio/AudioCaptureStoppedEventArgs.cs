using System;

namespace CaptionLink.Core.Audio;

public sealed class AudioCaptureStoppedEventArgs : EventArgs
{
    public AudioCaptureStoppedEventArgs(Exception? error)
    {
        Error = error;
    }

    public Exception? Error { get; }
}