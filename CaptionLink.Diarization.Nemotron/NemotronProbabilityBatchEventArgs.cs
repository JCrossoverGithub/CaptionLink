namespace CaptionLink.Diarization.Nemotron;

public sealed class NemotronProbabilityBatchEventArgs
    : EventArgs
{
    public NemotronProbabilityBatchEventArgs(
        NemotronProbabilityBatch batch)
    {
        Batch = batch ??
            throw new ArgumentNullException(nameof(batch));
    }

    public NemotronProbabilityBatch Batch { get; }
}
