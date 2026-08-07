namespace TransGo.Core.Audio;

/// <summary>
/// A complete mono PCM16 window copied from the bounded overlap
/// buffer. The byte array is independent of the live ring buffer.
/// </summary>
public sealed record Pcm16AudioWindow(
    TimeSpan StartTime,
    TimeSpan EndTime,
    int SampleRate,
    ReadOnlyMemory<byte> Data)
{
    public int Channels => 1;

    public int BitsPerSample => 16;

    public TimeSpan Duration =>
        EndTime - StartTime;
}
