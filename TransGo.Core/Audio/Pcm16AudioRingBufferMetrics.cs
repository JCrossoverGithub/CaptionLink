namespace TransGo.Core.Audio;

public sealed record Pcm16AudioRingBufferMetrics(
    long ChunksAppended,
    long InputBytesReceived,
    long OutputBytesProduced,
    long OverwrittenOutputBytes,
    long Discontinuities,
    int StoredBytes,
    TimeSpan StoredDuration,
    TimeSpan? AvailableStartTime,
    TimeSpan? AvailableEndTime);
