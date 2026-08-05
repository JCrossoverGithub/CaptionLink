using TransGo.Core.Audio;

namespace TransGo.Core.Diarization;

public interface IDiarizationEngine : IAsyncDisposable
{
    event EventHandler<SpeakerActivityEventArgs>?
        ActivityReceived;

    bool IsRunning { get; }

    Task StartAsync(
        DiarizationConfiguration configuration,
        CancellationToken cancellationToken = default);

    ValueTask SendAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default);

    Task StopAsync(
        CancellationToken cancellationToken = default);
}