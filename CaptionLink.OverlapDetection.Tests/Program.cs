using System.Buffers.Binary;
using CaptionLink.Core.Audio;
using CaptionLink.Core.Diarization;

namespace CaptionLink.OverlapDetection.Tests;

internal static class Program
{
    private static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            (
                "Single speaker does not create overlap",
                SingleSpeakerDoesNotCreateOverlap),

            (
                "Concurrent speakers create a final region",
                ConcurrentSpeakersCreateFinalRegion),

            (
                "Short interruption is rejected",
                ShortInterruptionIsRejected),

            (
                "Nearby overlap intervals merge before finalization",
                NearbyOverlapIntervalsMerge),

            (
                "Stale activity revisions are ignored",
                StaleActivityRevisionIsIgnored),

            (
                "Speaker limit is reported without losing speakers",
                SpeakerLimitIsReported),

            (
                "PCM buffer resamples and evicts old audio",
                PcmBufferResamplesAndEvicts),

            (
                "PCM buffer resets its timeline after a gap",
                PcmBufferHandlesDiscontinuity),
        ];

        int failureCount = 0;

        foreach ((string name, Action body) in tests)
        {
            try
            {
                body();
                Console.WriteLine(
                    $"PASS: {name}");
            }
            catch (Exception exception)
            {
                failureCount++;

                Console.Error.WriteLine(
                    $"FAIL: {name}");

                Console.Error.WriteLine(
                    exception);
            }
        }

        Console.WriteLine(
            $"Executed {tests.Length} overlap tests; " +
            $"failures: {failureCount}.");

        return failureCount == 0
            ? 0
            : 1;
    }

    private static void
        SingleSpeakerDoesNotCreateOverlap()
    {
        var detector =
            new SpeakerOverlapDetector();

        List<OverlapRegion> regions =
            CollectRegions(detector);

        detector.Process(
            Activity(
                "a-1",
                1,
                "speaker-1",
                0,
                2,
                isFinal: true));

        detector.Complete(
            Seconds(2));

        AssertEqual(
            0,
            regions.Count,
            "Single-speaker speech was classified as overlap.");
    }

    private static void
        ConcurrentSpeakersCreateFinalRegion()
    {
        var detector =
            new SpeakerOverlapDetector();

        List<OverlapRegion> regions =
            CollectRegions(detector);

        detector.Process(
            Activity(
                "a-1",
                1,
                "speaker-1",
                0,
                2,
                isFinal: false,
                confidence: 0.9));

        detector.Process(
            Activity(
                "b-1",
                2,
                "speaker-2",
                0.5,
                1,
                isFinal: true,
                confidence: 0.8));

        OverlapRegion region =
            regions[^1];

        AssertTrue(
            region.IsFinal,
            "The sealed historical overlap was not finalized.");

        AssertTime(
            Seconds(0.5),
            region.StartTime,
            "Unexpected overlap start.");

        AssertTime(
            Seconds(1),
            region.EndTime,
            "Unexpected overlap end.");

        AssertTime(
            Seconds(0.5),
            region.OverlapDuration,
            "Unexpected overlap duration.");

        AssertSequence(
            ["speaker-1", "speaker-2"],
            region.ActiveSpeakerIds,
            "Unexpected speakers.");

        AssertNear(
            0.85,
            region.DetectionConfidence ?? 0,
            0.0001,
            "Unexpected confidence.");

        AssertTime(
            Seconds(0.26),
            region.AudioStartTime,
            "Pre-roll was not applied.");

        AssertTime(
            Seconds(1.24),
            region.AudioEndTime,
            "Post-roll was not applied.");

        OverlapDetectionMetrics metrics =
            detector.GetMetrics();

        AssertEqual(
            1L,
            metrics.RegionsStarted,
            "Region start metric is incorrect.");

        AssertEqual(
            1L,
            metrics.RegionsFinalized,
            "Region final metric is incorrect.");
    }

    private static void ShortInterruptionIsRejected()
    {
        var detector =
            new SpeakerOverlapDetector();

        List<OverlapRegion> regions =
            CollectRegions(detector);

        detector.Process(
            Activity(
                "a-1",
                1,
                "speaker-1",
                0,
                1,
                isFinal: true));

        detector.Process(
            Activity(
                "b-1",
                2,
                "speaker-2",
                0.9,
                1,
                isFinal: true));

        detector.Complete(
            Seconds(1));

        AssertEqual(
            0,
            regions.Count,
            "A sub-threshold overlap was published.");
    }

    private static void NearbyOverlapIntervalsMerge()
    {
        var detector =
            new SpeakerOverlapDetector();

        List<OverlapRegion> regions =
            CollectRegions(detector);

        detector.Process(
            Activity(
                "a-1",
                1,
                "speaker-1",
                0,
                1.05,
                isFinal: false));

        detector.Process(
            Activity(
                "b-1",
                2,
                "speaker-2",
                0.5,
                1,
                isFinal: true));

        detector.Process(
            Activity(
                "a-1",
                3,
                "speaker-1",
                0,
                1.5,
                isFinal: false));

        AssertTrue(
            regions.Count > 0 &&
            !regions[^1].IsFinal,
            "The first region finalized between same-hop updates.");

        detector.Process(
            Activity(
                "b-2",
                4,
                "speaker-2",
                1.1,
                1.5,
                isFinal: true));

        detector.Process(
            Activity(
                "a-1",
                5,
                "speaker-1",
                0,
                2.5,
                isFinal: true));

        OverlapRegion finalRegion =
            regions[^1];

        AssertTrue(
            finalRegion.IsFinal,
            "Merged overlap did not finalize.");

        AssertTime(
            Seconds(0.5),
            finalRegion.StartTime,
            "Merged overlap start changed.");

        AssertTime(
            Seconds(1.5),
            finalRegion.EndTime,
            "Nearby overlap did not extend the region.");

        AssertTime(
            Seconds(0.9),
            finalRegion.OverlapDuration,
            "The quiet merge gap was counted as overlap.");

        AssertEqual(
            1,
            regions
                .Select(region => region.RegionId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            "Nearby intervals were assigned different region IDs.");
    }

    private static void StaleActivityRevisionIsIgnored()
    {
        var detector =
            new SpeakerOverlapDetector();

        detector.Process(
            Activity(
                "a-1",
                2,
                "speaker-1",
                0,
                2,
                isFinal: false));

        detector.Process(
            Activity(
                "a-1",
                1,
                "speaker-1",
                0,
                1,
                isFinal: true));

        OverlapDetectionMetrics metrics =
            detector.GetMetrics();

        AssertEqual(
            1L,
            metrics.StaleActivityUpdatesIgnored,
            "The stale revision was not counted.");
    }

    private static void SpeakerLimitIsReported()
    {
        var options =
            new OverlapDetectionOptions
            {
                EndHysteresis =
                    Seconds(2),
            };

        var detector =
            new SpeakerOverlapDetector(
                options);

        List<OverlapRegion> regions =
            CollectRegions(detector);

        detector.Process(
            Activity(
                "a-1",
                1,
                "speaker-1",
                0,
                2,
                isFinal: true));

        detector.Process(
            Activity(
                "b-1",
                2,
                "speaker-2",
                0.5,
                1.5,
                isFinal: true));

        detector.Process(
            Activity(
                "c-1",
                3,
                "speaker-3",
                0.75,
                1.25,
                isFinal: true));

        detector.Complete(
            Seconds(2));

        OverlapRegion region =
            regions[^1];

        AssertTrue(
            region.ExceedsSupportedSpeakerCount,
            "Three-speaker overlap was not marked unsupported.");

        AssertSequence(
            ["speaker-1", "speaker-2", "speaker-3"],
            region.ActiveSpeakerIds,
            "A detected speaker was discarded.");

        AssertEqual(
            1L,
            detector.GetMetrics()
                .UnsupportedSpeakerRegions,
            "The unsupported-region metric is incorrect.");
    }

    private static void PcmBufferResamplesAndEvicts()
    {
        var buffer =
            new Pcm16AudioRingBuffer(
                sampleRate: 16_000,
                capacity: Seconds(0.2));

        buffer.Append(
            AudioChunk(
                1,
                0,
                inputSampleRate: 48_000,
                sampleValue: 12_000));

        buffer.Append(
            AudioChunk(
                2,
                0.1,
                inputSampleRate: 48_000,
                sampleValue: 12_000));

        buffer.Append(
            AudioChunk(
                3,
                0.2,
                inputSampleRate: 48_000,
                sampleValue: 12_000));

        AssertTime(
            Seconds(0.1),
            buffer.AvailableStartTime ??
                TimeSpan.MinValue,
            "The oldest 100 ms was not evicted.");

        AssertTime(
            Seconds(0.3),
            buffer.AvailableEndTime ??
                TimeSpan.MinValue,
            "Unexpected buffer end.");

        bool readSucceeded =
            buffer.TryRead(
                Seconds(0.1),
                Seconds(0.3),
                out Pcm16AudioWindow? window);

        AssertTrue(
            readSucceeded && window is not null,
            "The retained buffer range could not be read.");

        AssertEqual(
            6_400,
            window!.Data.Length,
            "The 16 kHz PCM16 byte count is incorrect.");

        short firstSample =
            BinaryPrimitives
                .ReadInt16LittleEndian(
                    window.Data.Span[..2]);

        AssertEqual(
            (short)12_000,
            firstSample,
            "Constant PCM changed during resampling.");

        Pcm16AudioRingBufferMetrics metrics =
            buffer.GetMetrics();

        AssertEqual(
            3_200L,
            metrics.OverwrittenOutputBytes,
            "Evicted bytes metric is incorrect.");
    }

    private static void PcmBufferHandlesDiscontinuity()
    {
        var buffer =
            new Pcm16AudioRingBuffer();

        buffer.Append(
            AudioChunk(
                1,
                0,
                inputSampleRate: 48_000,
                sampleValue: 1_000));

        buffer.Append(
            AudioChunk(
                2,
                0.2,
                inputSampleRate: 48_000,
                sampleValue: 2_000));

        Pcm16AudioRingBufferMetrics metrics =
            buffer.GetMetrics();

        AssertEqual(
            1L,
            metrics.Discontinuities,
            "The timeline gap was not recorded.");

        AssertTime(
            Seconds(0.2),
            metrics.AvailableStartTime ??
                TimeSpan.MinValue,
            "Audio before the discontinuity was retained.");
    }

    private static List<OverlapRegion> CollectRegions(
        SpeakerOverlapDetector detector)
    {
        var regions =
            new List<OverlapRegion>();

        detector.RegionUpdated +=
            (_, eventArgs) =>
                regions.Add(
                    eventArgs.Region);

        return regions;
    }

    private static SpeakerActivity Activity(
        string activityId,
        long sequence,
        string speakerId,
        double startSeconds,
        double endSeconds,
        bool isFinal,
        double? confidence = 0.9)
    {
        return new SpeakerActivity(
            ActivityId: activityId,
            Sequence: sequence,
            SpeakerId: speakerId,
            StartTime: Seconds(startSeconds),
            EndTime: Seconds(endSeconds),
            IsFinal: isFinal,
            Confidence: confidence);
    }

    private static TranscriptionAudioChunk AudioChunk(
        long sequence,
        double startSeconds,
        int inputSampleRate,
        short sampleValue)
    {
        int sampleCount =
            inputSampleRate /
            10;

        var data =
            new byte[
                sampleCount *
                sizeof(short)];

        for (
            int sampleIndex = 0;
            sampleIndex < sampleCount;
            sampleIndex++)
        {
            BinaryPrimitives
                .WriteInt16LittleEndian(
                    data.AsSpan(
                        sampleIndex *
                            sizeof(short),

                        sizeof(short)),

                    sampleValue);
        }

        return new TranscriptionAudioChunk(
            Sequence: sequence,
            CapturedAt: DateTimeOffset.UnixEpoch,
            Data: data,
            SampleRate: inputSampleRate)
        {
            SessionStartTime =
                Seconds(startSeconds),
        };
    }

    private static TimeSpan Seconds(
        double value)
    {
        return TimeSpan.FromSeconds(value);
    }

    private static void AssertTrue(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                message);
        }
    }

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(
                expected,
                actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected {expected}; " +
                $"actual {actual}.");
        }
    }

    private static void AssertTime(
        TimeSpan expected,
        TimeSpan actual,
        string message)
    {
        if (
            Math.Abs(
                (expected - actual)
                    .TotalMilliseconds) > 0.01)
        {
            throw new InvalidOperationException(
                $"{message} Expected {expected}; " +
                $"actual {actual}.");
        }
    }

    private static void AssertNear(
        double expected,
        double actual,
        double tolerance,
        string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"{message} Expected {expected}; " +
                $"actual {actual}.");
        }
    }

    private static void AssertSequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual,
        string message)
    {
        if (
            !expected.SequenceEqual(
                actual,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{message} Expected " +
                $"[{string.Join(", ", expected)}]; " +
                $"actual [{string.Join(", ", actual)}].");
        }
    }
}
