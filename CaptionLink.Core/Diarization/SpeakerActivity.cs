namespace CaptionLink.Core.Diarization;

/// <summary>
/// Represents speaker activity over a specific interval
/// in the current audio-capture session.
/// </summary>
public sealed record SpeakerActivity(
    string ActivityId,
    long Sequence,
    string SpeakerId,
    TimeSpan StartTime,
    TimeSpan EndTime,
    bool IsFinal,
    double? Confidence);