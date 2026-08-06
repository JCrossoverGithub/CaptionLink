namespace TransGo.Core.Diarization;

/// <summary>
/// Describes how much of a transcript segment was covered by
/// one speaker.
/// </summary>
public sealed record SpeakerOverlap(
    string SpeakerId,
    TimeSpan OverlapDuration,
    double SegmentCoverage,
    TimeSpan LatestEnd,
    TimeSpan ConcurrentWithPrimaryDuration);
