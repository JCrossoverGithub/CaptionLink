namespace CaptionLink.OverlapBenchmark;

public readonly record struct TimeInterval(
    double StartSeconds,
    double EndSeconds)
{
    public double DurationSeconds =>
        EndSeconds - StartSeconds;

    public bool Overlaps(TimeInterval other) =>
        StartSeconds < other.EndSeconds &&
        EndSeconds > other.StartSeconds;

    public double IntersectionSeconds(
        TimeInterval other) =>
        Math.Max(
            0,
            Math.Min(EndSeconds, other.EndSeconds) -
            Math.Max(StartSeconds, other.StartSeconds));
}

public sealed record SpeakerInterval(
    string RecordingId,
    string SpeakerId,
    double StartSeconds,
    double EndSeconds)
{
    public TimeInterval Interval =>
        new(StartSeconds, EndSeconds);
}
