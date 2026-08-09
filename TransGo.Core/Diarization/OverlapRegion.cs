namespace TransGo.Core.Diarization;

/// <summary>
/// A stable interval in which at least two speakers were active.
/// StartTime and EndTime describe detected overlap. AudioStartTime
/// and AudioEndTime include the configured context required by a
/// future separation stage.
/// </summary>
public sealed record OverlapRegion(
    string RegionId,
    long Revision,
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan OverlapDuration,
    IReadOnlyList<string> ActiveSpeakerIds,
    double? DetectionConfidence,
    bool IsFinal,
    bool ExceedsSupportedSpeakerCount,
    TimeSpan AudioStartTime,
    TimeSpan AudioEndTime);
