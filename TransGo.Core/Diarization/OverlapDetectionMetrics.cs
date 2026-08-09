namespace TransGo.Core.Diarization;

public sealed record OverlapDetectionMetrics(
    long ActivityUpdatesReceived,
    long StaleActivityUpdatesIgnored,
    long RegionUpdatesPublished,
    long RegionsStarted,
    long RegionsFinalized,
    long UnsupportedSpeakerRegions,
    TimeSpan TotalFinalOverlapDuration);
