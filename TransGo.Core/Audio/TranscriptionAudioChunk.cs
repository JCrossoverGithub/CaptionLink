namespace TransGo.Core.Audio;

public sealed record TranscriptionAudioChunk(
    long Sequence,
    DateTimeOffset CapturedAt,
    ReadOnlyMemory<byte> Data,
    int SampleRate)
{
    public int Channels => 1;

    public int BitsPerSample => 16;

    public AudioSampleEncoding Encoding =>
        AudioSampleEncoding.PcmInteger;

    public int ByteCount => Data.Length;

    public TimeSpan Duration =>
        TimeSpan.FromSeconds(
            Data.Length /
            (double)(
                SampleRate *
                Channels *
                sizeof(short)));

    /// <summary>
    /// The chunk's start position relative to the beginning
    /// of the current audio-capture session.
    /// </summary>
    public TimeSpan SessionStartTime { get; init; }

    /// <summary>
    /// The chunk's end position relative to the beginning
    /// of the current audio-capture session.
    /// </summary>
    public TimeSpan SessionEndTime =>
        SessionStartTime + Duration;
}