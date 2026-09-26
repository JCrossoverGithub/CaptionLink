namespace CaptionLink.Core.Diarization;

/// <summary>
/// Controls how raw concurrent speaker activity becomes a stable
/// overlap region.
/// </summary>
public sealed record OverlapDetectionOptions
{
    public TimeSpan MinimumOverlapDuration { get; init; } =
        TimeSpan.FromMilliseconds(160);

    public TimeSpan StartHysteresis { get; init; } =
        TimeSpan.FromMilliseconds(160);

    // Sortformer advances in 480 ms prediction hops. Keep this
    // beyond one hop plus the merge gap so sequential messages from
    // one prediction batch cannot finalize a region prematurely.
    public TimeSpan EndHysteresis { get; init; } =
        TimeSpan.FromMilliseconds(720);

    public TimeSpan PreRoll { get; init; } =
        TimeSpan.FromMilliseconds(240);

    public TimeSpan PostRoll { get; init; } =
        TimeSpan.FromMilliseconds(240);

    public TimeSpan MergeGap { get; init; } =
        TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The first separation implementation is intentionally limited
    /// to two simultaneous speakers. Detection still reports every
    /// active speaker and marks regions that exceed this limit.
    /// </summary>
    public int MaximumSupportedSpeakers { get; init; } = 2;

    internal void Validate()
    {
        ValidateNonNegative(
            MinimumOverlapDuration,
            nameof(MinimumOverlapDuration));

        ValidateNonNegative(
            StartHysteresis,
            nameof(StartHysteresis));

        ValidateNonNegative(
            EndHysteresis,
            nameof(EndHysteresis));

        ValidateNonNegative(
            PreRoll,
            nameof(PreRoll));

        ValidateNonNegative(
            PostRoll,
            nameof(PostRoll));

        ValidateNonNegative(
            MergeGap,
            nameof(MergeGap));

        if (MaximumSupportedSpeakers < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumSupportedSpeakers),
                "At least two speakers must be supported.");
        }

        if (EndHysteresis < MergeGap)
        {
            throw new ArgumentException(
                "End hysteresis must be at least as long as " +
                "the nearby-region merge gap.");
        }
    }

    private static void ValidateNonNegative(
        TimeSpan value,
        string propertyName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                "Overlap timing values cannot be negative.");
        }
    }
}
