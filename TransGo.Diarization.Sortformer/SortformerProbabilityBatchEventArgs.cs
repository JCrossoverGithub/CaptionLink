namespace TransGo.Diarization.Sortformer;

public sealed class SortformerProbabilityBatchEventArgs
    : EventArgs
{
    public SortformerProbabilityBatchEventArgs(
        SortformerProbabilityBatch batch)
    {
        Batch = batch ??
            throw new ArgumentNullException(nameof(batch));
    }

    public SortformerProbabilityBatch Batch { get; }
}
