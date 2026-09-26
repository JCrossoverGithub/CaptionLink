using CaptionLink.Core.Audio;
using CaptionLink.Core.Diarization;

namespace CaptionLink.Application.Tests;

internal sealed class FakeDiarizationEngine
    : IDiarizationEngine
{
    private readonly IList<string>
        _operations;

    public FakeDiarizationEngine(
        IList<string> operations)
    {
        _operations =
            operations;
    }

    public event EventHandler<SpeakerActivityEventArgs>?
        ActivityReceived;

    public bool IsRunning { get; private set; }

    public bool ThrowOnStart { get; set; }

    public bool ThrowOnSend { get; set; }

    public bool ThrowOnStop { get; set; }

    public Task StartAsync(
        DiarizationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        _operations.Add(
            "diarization:start");

        cancellationToken.ThrowIfCancellationRequested();

        if (ThrowOnStart)
        {
            throw new InvalidOperationException(
                "Diarization startup failed.");
        }

        IsRunning = true;

        return Task.CompletedTask;
    }

    public ValueTask SendAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        _operations.Add(
            "diarization:send");

        cancellationToken.ThrowIfCancellationRequested();

        if (ThrowOnSend)
        {
            throw new InvalidOperationException(
                "Diarization send failed.");
        }

        return ValueTask.CompletedTask;
    }

    public Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        _operations.Add(
            "diarization:stop");

        if (ThrowOnStop)
        {
            throw new InvalidOperationException(
                "Diarization stop failed.");
        }

        IsRunning = false;

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _operations.Add(
            "diarization:dispose");

        IsRunning = false;

        return ValueTask.CompletedTask;
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
