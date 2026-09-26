namespace CaptionLink.Core.Audio;

public sealed class TranscriptionAudioChunkEventArgs : EventArgs
{
    public TranscriptionAudioChunkEventArgs(
        TranscriptionAudioChunk chunk)
    {
        Chunk = chunk ??
            throw new ArgumentNullException(nameof(chunk));
    }

    public TranscriptionAudioChunk Chunk { get; }
}