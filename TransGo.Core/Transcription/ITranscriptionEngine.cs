using TransGo.Core.Audio;

namespace TransGo.Core.Transcription;

public interface ITranscriptionEngine : IAsyncDisposable
{
    event EventHandler<TranscriptResultEventArgs>? ResultReceived;

    bool IsRunning { get; }

    Task StartAsync(
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken = default);

    ValueTask SendAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default);

    Task StopAsync(
        CancellationToken cancellationToken = default);
}