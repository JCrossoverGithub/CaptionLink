namespace TransGo.Core.Diarization;

/// <summary>
/// Contains the complete speaker-attribution decision for one
/// transcript segment.
/// </summary>
public sealed record SpeakerAttributionDecision(
    string? PrimarySpeakerId,
    IReadOnlyList<string> SpeakerIds,
    IReadOnlyList<SpeakerOverlap> Overlaps)
{
    public bool HasSpeaker =>
        SpeakerIds.Count > 0;

    public bool IsOverlapping =>
        SpeakerIds.Count > 1;

    public string? CompositeSpeakerId =>
        HasSpeaker
            ? string.Join(
                "+",
                SpeakerIds)
            : null;
}
