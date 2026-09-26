using System.Diagnostics;
using CaptionLink.Remote.Protocol.Messages;

namespace CaptionLink.GpuGateway;

internal sealed class GatewayLatencyTracker
{
    private const long RetentionMilliseconds = 30_000;

    private readonly object _gate = new();
    private readonly List<AudioTiming> _audioTimings = [];

    public void RecordReceived(
        long sequence,
        long startTimeMilliseconds,
        long durationMilliseconds,
        long receivedTimestamp)
    {
        var timing = new AudioTiming(
            Sequence: sequence,
            StartTimeMilliseconds: startTimeMilliseconds,
            EndTimeMilliseconds: checked(
                startTimeMilliseconds + durationMilliseconds),
            ReceivedTimestamp: receivedTimestamp);

        lock (_gate)
        {
            _audioTimings.Add(timing);
        }
    }

    public void RecordDispatchStarted(
        long sequence,
        long dispatchTimestamp)
    {
        lock (_gate)
        {
            AudioTiming? timing = _audioTimings.FindLast(
                candidate => candidate.Sequence == sequence);

            if (timing is not null)
            {
                timing.DispatchTimestamp = dispatchTimestamp;
            }
        }
    }

    public CaptionLatencyMetrics? CreateCaptionMetrics(
        long audioEndTimeMilliseconds,
        double? engineProcessingMilliseconds,
        long resultTimestamp)
    {
        lock (_gate)
        {
            AudioTiming? timing = FindTiming(audioEndTimeMilliseconds);

            if (timing is null)
            {
                return null;
            }

            PruneOldTimings(audioEndTimeMilliseconds);

            return new CaptionLatencyMetrics(
                AudioChunkSequence: timing.Sequence,
                AudioEndTimeMilliseconds: audioEndTimeMilliseconds,
                GatewayReceiveToResultMilliseconds:
                    ElapsedMilliseconds(
                        timing.ReceivedTimestamp,
                        resultTimestamp),
                GatewayDispatchToResultMilliseconds:
                    ElapsedMilliseconds(
                        timing.DispatchTimestamp ??
                            timing.ReceivedTimestamp,
                        resultTimestamp),
                EngineProcessingMilliseconds:
                    engineProcessingMilliseconds);
        }
    }

    private AudioTiming? FindTiming(long audioEndTimeMilliseconds)
    {
        for (int index = _audioTimings.Count - 1; index >= 0; index--)
        {
            AudioTiming candidate = _audioTimings[index];

            if (audioEndTimeMilliseconds >
                    candidate.StartTimeMilliseconds &&
                audioEndTimeMilliseconds <=
                    candidate.EndTimeMilliseconds)
            {
                return candidate;
            }
        }

        return _audioTimings.FindLast(
            candidate =>
                candidate.EndTimeMilliseconds <=
                audioEndTimeMilliseconds);
    }

    private void PruneOldTimings(long audioEndTimeMilliseconds)
    {
        long minimumEndTime = Math.Max(
            0,
            audioEndTimeMilliseconds - RetentionMilliseconds);

        _audioTimings.RemoveAll(
            timing => timing.EndTimeMilliseconds < minimumEndTime);
    }

    private static double ElapsedMilliseconds(
        long startTimestamp,
        long endTimestamp)
    {
        return Math.Max(
            0,
            (endTimestamp - startTimestamp) * 1000.0 /
            Stopwatch.Frequency);
    }

    private sealed record AudioTiming(
        long Sequence,
        long StartTimeMilliseconds,
        long EndTimeMilliseconds,
        long ReceivedTimestamp)
    {
        public long? DispatchTimestamp { get; set; }
    }
}
