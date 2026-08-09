namespace TransGo.Diarization.Sortformer;

/// <summary>
/// Reports how many audio chunks the local Sortformer service
/// has finished accepting and processing for the active session.
/// </summary>
public sealed class SortformerAudioProgressEventArgs
    : EventArgs
{
    public SortformerAudioProgressEventArgs(
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
