using System.Diagnostics;
using TransGo.GpuGateway;

namespace TransGo.Remote.Protocol.Tests;

public sealed class GatewayLatencyTrackerTests
{
    [Fact]
    public void CaptionMetrics_CorrelateResultWithAudioBoundary()
    {
        var tracker = new GatewayLatencyTracker();
        long received = Stopwatch.GetTimestamp();
        long dispatch = AddMilliseconds(received, 5);
        long result = AddMilliseconds(received, 425);

        tracker.RecordReceived(
            sequence: 14,
            startTimeMilliseconds: 1400,
            durationMilliseconds: 100,
            receivedTimestamp: received);
        tracker.RecordDispatchStarted(14, dispatch);

        var metrics = tracker.CreateCaptionMetrics(
            audioEndTimeMilliseconds: 1500,
            engineProcessingMilliseconds: 97.25,
            resultTimestamp: result);

        Assert.NotNull(metrics);
        Assert.Equal(14, metrics.AudioChunkSequence);
        Assert.Equal(1500, metrics.AudioEndTimeMilliseconds);
        Assert.InRange(
            metrics.GatewayReceiveToResultMilliseconds,
            424.9,
            425.1);
        Assert.InRange(
            metrics.GatewayDispatchToResultMilliseconds,
            419.9,
            420.1);
        Assert.Equal(97.25, metrics.EngineProcessingMilliseconds);
    }

    [Fact]
    public void CaptionMetrics_ReturnNullBeforeAudioArrives()
    {
        var tracker = new GatewayLatencyTracker();

        Assert.Null(
            tracker.CreateCaptionMetrics(
                audioEndTimeMilliseconds: 100,
                engineProcessingMilliseconds: null,
                resultTimestamp: Stopwatch.GetTimestamp()));
    }

    private static long AddMilliseconds(
        long timestamp,
        double milliseconds)
    {
        return timestamp +
            (long)Math.Round(
                milliseconds * Stopwatch.Frequency / 1000.0);
    }
}
