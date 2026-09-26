namespace CaptionLink.Core.Audio;

public sealed class AudioFrameEventArgs : EventArgs
{
    public AudioFrameEventArgs(AudioFrame frame)
    {
        Frame = frame ??
            throw new ArgumentNullException(nameof(frame));
    }

    public AudioFrame Frame { get; }
}