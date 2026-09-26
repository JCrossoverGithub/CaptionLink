namespace CaptionLink.Diarization.Nemotron;

/// <summary>
/// Reports how many audio chunks the local Nemotron service
/// has finished accepting and processing for the active session.
/// </summary>
public sealed class NemotronAudioProgressEventArgs
    : EventArgs
{
    public NemotronAudioProgressEventArgs(
        long chunksReceived)
    {
        if (chunksReceived <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunksReceived),
                "The processed chunk count must be positive.");
        }

        ChunksReceived = chunksReceived;
    }

    public long ChunksReceived { get; }
}
