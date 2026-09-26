using CaptionLink.Core.Diarization;
using CaptionLink.Core.Transcription;

namespace CaptionLink.Application.Tests;

public sealed class CaptionSessionTests
{
    [Fact]
    public async Task StartAsync_StartsTranscriptionBeforeDiarization()
    {
        var operations =
            new List<string>();

        var transcription =
            new FakeTranscriptionEngine(
                operations);

        var diarization =
            new FakeDiarizationEngine(
                operations);

        await using var session =
            new CaptionSession(
                transcription,
                diarization);

        await session.StartAsync(
            CreateTranscriptionConfiguration(),
            CreateDiarizationConfiguration());

        Assert.Equal(
            [
                "transcription:start",
                "diarization:start",
            ],
            operations);
    }

    [Fact]
    public async Task StopAsync_StopsDiarizationBeforeTranscription()
    {
        var operations =
            new List<string>();

        var transcription =
            new FakeTranscriptionEngine(
                operations);

        var diarization =
            new FakeDiarizationEngine(
                operations);

        await using var session =
            new CaptionSession(
                transcription,
                diarization);

        await session.StartAsync(
            CreateTranscriptionConfiguration(),
            CreateDiarizationConfiguration());

        operations.Clear();

        await session.StopAsync();

        Assert.Equal(
            [
                "diarization:stop",
                "transcription:stop",
            ],
            operations);
    }

    [Fact]
    public async Task StartAsync_ContinuesWhenDiarizationFails()
    {
        var operations =
            new List<string>();

        var transcription =
            new FakeTranscriptionEngine(
                operations);

        var diarization =
            new FakeDiarizationEngine(
                operations)
            {
                ThrowOnStart = true,
            };

        await using var session =
            new CaptionSession(
                transcription,
                diarization);

        Exception? reportedFailure =
            null;

        session.SpeakerAttributionUnavailable +=
            (_, e) =>
                reportedFailure =
                    e.Exception;

        await session.StartAsync(
            CreateTranscriptionConfiguration(),
            CreateDiarizationConfiguration());

        Assert.True(
            transcription.IsRunning);

        Assert.False(
            session.IsSpeakerAttributionRunning);

        Assert.NotNull(
            reportedFailure);

        Assert.Equal(
            [
                "transcription:start",
                "diarization:start",
                "diarization:dispose",
            ],
            operations);
    }

    [Fact]
    public async Task SendAsync_DisablesFailedDiarizationAndContinues()
    {
        var operations =
            new List<string>();

        var transcription =
            new FakeTranscriptionEngine(
                operations);

        var diarization =
            new FakeDiarizationEngine(
                operations)
            {
                ThrowOnSend = true,
            };

        await using var session =
            new CaptionSession(
                transcription,
                diarization);

        int failureNotifications = 0;

        session.SpeakerAttributionUnavailable +=
            (_, _) =>
                failureNotifications++;

        await session.StartAsync(
            CreateTranscriptionConfiguration(),
            CreateDiarizationConfiguration());

        operations.Clear();

        await session.SendAsync(
            CreateAudioChunk());

        Assert.True(
            transcription.IsRunning);

        Assert.False(
            session.IsSpeakerAttributionRunning);

        Assert.Equal(
            1,
            failureNotifications);

        Assert.Equal(
            [
                "transcription:send",
                "diarization:send",
                "diarization:stop",
                "diarization:dispose",
            ],
            operations);

        operations.Clear();

        await session.SendAsync(
            CreateAudioChunk());

        Assert.Equal(
            [
                "transcription:send",
            ],
            operations);

        Assert.Equal(
            1,
            failureNotifications);
    }

    [Fact]
    public async Task StartAsync_PropagatesDiarizationCancellation()
    {
        var operations =
            new List<string>();

        var transcription =
            new FakeTranscriptionEngine(
                operations);

        var diarization =
            new FakeDiarizationEngine(
                operations);

        await using var session =
            new CaptionSession(
                transcription,
                diarization);

        int failureNotifications = 0;

        session.SpeakerAttributionUnavailable +=
            (_, _) =>
                failureNotifications++;

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () =>
                session.StartAsync(
                    CreateTranscriptionConfiguration(),
                    CreateDiarizationConfiguration(),
                    cancellation.Token));

        Assert.False(
            transcription.IsRunning);

        Assert.Equal(
            0,
            failureNotifications);

        Assert.Equal(
            [
                "transcription:start",
                "diarization:start",
                "transcription:stop",
            ],
            operations);
    }

    [Fact]
    public async Task SendAsync_PropagatesDiarizationCancellationWithoutDisablingIt()
    {
        var operations =
            new List<string>();

        var transcription =
            new FakeTranscriptionEngine(
                operations);

        var diarization =
            new FakeDiarizationEngine(
                operations);

        await using var session =
            new CaptionSession(
                transcription,
                diarization);

        int failureNotifications = 0;

        session.SpeakerAttributionUnavailable +=
            (_, _) =>
                failureNotifications++;

        await session.StartAsync(
            CreateTranscriptionConfiguration(),
            CreateDiarizationConfiguration());

        operations.Clear();

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            async () =>
                await session.SendAsync(
                    CreateAudioChunk(),
                    cancellation.Token));

        Assert.True(
            session.IsSpeakerAttributionRunning);

        Assert.Equal(
            0,
            failureNotifications);

        Assert.Equal(
            [
                "transcription:send",
                "diarization:send",
            ],
            operations);
    }

    [Fact]
    public async Task SendAsync_SendsAudioToBothRunningEngines()
    {
        var operations =
            new List<string>();

        var transcription =
            new FakeTranscriptionEngine(
                operations);

        var diarization =
            new FakeDiarizationEngine(
                operations);

        await using var session =
            new CaptionSession(
                transcription,
                diarization);

        await session.StartAsync(
            CreateTranscriptionConfiguration(),
            CreateDiarizationConfiguration());

        operations.Clear();

        await session.SendAsync(
            CreateAudioChunk());

        Assert.Equal(
            [
                "transcription:send",
                "diarization:send",
            ],
            operations);
    }

    [Fact]
    public async Task ResultReceived_ForwardsTranscriptionResult()
    {
        var operations =
            new List<string>();

        var transcription =
            new FakeTranscriptionEngine(
                operations);

        await using var session =
            new CaptionSession(
                transcription);

        TranscriptResult? receivedResult =
            null;

        session.ResultReceived +=
            (_, e) =>
                receivedResult =
                    e.Result;

        var expectedResult =
            new TranscriptResult(
                SegmentId: "segment-1",
                Sequence: 1,
                Text: "hello",
                IsFinal: true,
                Stability: 1.0,
                ResultEndTime:
                    TimeSpan.FromSeconds(1));

        transcription.PublishResult(
            expectedResult);

        Assert.Same(
            expectedResult,
            receivedResult);
    }

    [Fact]
    public async Task ActivityReceived_ForwardsInternalSpeakerActivity()
    {
        var operations =
            new List<string>();

        var transcription =
            new FakeTranscriptionEngine(
                operations);

        await using var session =
            new CaptionSession(
                transcription);

        SpeakerActivity? receivedActivity =
            null;

        session.ActivityReceived +=
            (_, e) =>
                receivedActivity =
                    e.Activity;

        var expectedActivity =
            new SpeakerActivity(
                ActivityId: "activity-1",
                Sequence: 1,
                SpeakerId: "speaker-1",
                StartTime: TimeSpan.Zero,
                EndTime:
                    TimeSpan.FromSeconds(1),
                IsFinal: true,
                Confidence: 0.95);

        transcription.PublishActivity(
            expectedActivity);

        Assert.Same(
            expectedActivity,
            receivedActivity);
    }

    [Fact]
    public async Task ActivityReceived_ForwardsExternalDiarizationActivity()
    {
        var operations =
            new List<string>();

        var transcription =
            new FakeTranscriptionEngine(
                operations);

        var diarization =
            new FakeDiarizationEngine(
                operations);

        await using var session =
            new CaptionSession(
                transcription,
                diarization);

        SpeakerActivity? receivedActivity =
            null;

        session.ActivityReceived +=
            (_, e) =>
                receivedActivity =
                    e.Activity;

        var expectedActivity =
            new SpeakerActivity(
                ActivityId: "activity-1",
                Sequence: 1,
                SpeakerId: "speaker-2",
                StartTime: TimeSpan.Zero,
                EndTime:
                    TimeSpan.FromSeconds(1),
                IsFinal: true,
                Confidence: 0.90);

        diarization.PublishActivity(
            expectedActivity);

        Assert.Same(
            expectedActivity,
            receivedActivity);
    }

    private static TranscriptionConfiguration
        CreateTranscriptionConfiguration()
    {
        return new TranscriptionConfiguration(
            LanguageCode: "en-US",
            SampleRate: 48_000,
            EnableInterimResults: true);
    }

    private static DiarizationConfiguration
        CreateDiarizationConfiguration()
    {
        return new DiarizationConfiguration(
            SampleRate: 48_000,
            MaximumSpeakers: 4);
    }

    private static CaptionLink.Core.Audio.TranscriptionAudioChunk
        CreateAudioChunk()
    {
        return new CaptionLink.Core.Audio.TranscriptionAudioChunk(
            Sequence: 1,
            CapturedAt:
                DateTimeOffset.UnixEpoch,
            Data:
                new byte[960],
            SampleRate: 48_000)
        {
            SessionStartTime =
                TimeSpan.Zero,
        };
    }
}
