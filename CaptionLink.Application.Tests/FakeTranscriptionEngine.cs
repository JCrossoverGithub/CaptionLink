using CaptionLink.Core.Audio;
using CaptionLink.Core.Diarization;
using CaptionLink.Core.Transcription;

namespace CaptionLink.Application.Tests;

internal sealed class FakeTranscriptionEngine
    : ITranscriptionEngine,
      ISpeakerActivitySource
{
    private readonly IList<string>
        _operations;

    public FakeTranscriptionEngine(
        IList<string> operations)
    {
        _operations =
            operations;
    }

    public event EventHandler<TranscriptResultEventArgs>?
        ResultReceived;

    public event EventHandler<SpeakerActivityEventArgs>?
        ActivityReceived;

    public bool IsRunning { get; private set; }

    public bool ThrowOnStart { get; set; }

    public bool ThrowOnStop { get; set; }

    public Task StartAsync(
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        _operations.Add(
            "transcription:start");

        if (ThrowOnStart)
        {
            throw new InvalidOperationException(
                "Transcription startup failed.");
        }

        IsRunning = true;

        return Task.CompletedTask;
    }

    public ValueTask SendAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        _operations.Add(
            "transcription:send");

        return ValueTask.CompletedTask;
    }

    public Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        _operations.Add(
            "transcription:stop");

        if (ThrowOnStop)
        {
            throw new InvalidOperationException(
                "Transcription stop failed.");
        }

        IsRunning = false;

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _operations.Add(
            "transcription:dispose");

        IsRunning = false;

        return ValueTask.CompletedTask;
    }

    public void PublishResult(
        TranscriptResult result)
    {
        ResultReceived?.Invoke(
            this,
            new TranscriptResultEventArgs(
                result));
    }

    public void PublishActivity(
        SpeakerActivity activity)
    {
        ActivityReceived?.Invoke(
            this,
            new SpeakerActivityEventArgs(
                activity));
    }
}
