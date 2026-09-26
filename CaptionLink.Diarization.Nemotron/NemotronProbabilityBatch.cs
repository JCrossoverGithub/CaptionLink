namespace CaptionLink.Diarization.Nemotron;

/// <summary>
/// Raw frame-level speaker probabilities produced by one Nemotron
/// streaming prediction hop. These values have not passed through
/// speaker-activity thresholds.
/// </summary>
public sealed record NemotronProbabilityBatch(
    long StartFrameIndex,
    double FrameDurationSeconds,
    IReadOnlyList<float[]> Probabilities)
{
    public int FrameCount => Probabilities.Count;

    public int SpeakerCount =>
        Probabilities.Count == 0
            ? 0
            : Probabilities[0].Length;
}
