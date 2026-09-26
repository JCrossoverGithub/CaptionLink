namespace CaptionLink.Core.Transcription;

public sealed class TranscriptResultEventArgs : EventArgs
{
    public TranscriptResultEventArgs(TranscriptResult result)
    {
        Result = result ??
            throw new ArgumentNullException(nameof(result));
    }

    public TranscriptResult Result { get; }
}