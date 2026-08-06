namespace TransGo.Core.Transcription;

public sealed record TranscriptResult(
    string SegmentId,
    long Sequence,
    string Text,
    bool IsFinal,
    double? Stability,
    TimeSpan? ResultEndTime)
{
    /// <summary>
    /// The position in the transcription session at which this
    /// result began. Providers may leave this null when timing
    /// information is not yet available.
    /// </summary>
    public TimeSpan? ResultStartTime { get; init; }

    /// <summary>
    /// Internal speaker identifier assigned by an optional
    /// diarization engine, such as "speaker-1".
    /// Null means that speaker attribution is unavailable or off.
    /// </summary>
    public string? SpeakerId { get; init; }
}