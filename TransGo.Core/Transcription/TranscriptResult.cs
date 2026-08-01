namespace TransGo.Core.Transcription;

public sealed record TranscriptResult(
    string SegmentId,
    long Sequence,
    string Text,
    bool IsFinal,
    double? Stability,
    TimeSpan? ResultEndTime);