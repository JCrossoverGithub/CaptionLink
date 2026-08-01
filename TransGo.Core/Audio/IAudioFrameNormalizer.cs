namespace TransGo.Core.Audio;

public interface IAudioFrameNormalizer : IDisposable
{
    event EventHandler<TranscriptionAudioChunkEventArgs>?
        ChunkAvailable;

    void Process(AudioFrame frame);

    void Reset();
}