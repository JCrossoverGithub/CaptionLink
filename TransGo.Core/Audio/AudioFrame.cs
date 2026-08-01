namespace TransGo.Core.Audio;

public sealed record AudioFrame(
    long Sequence,
    DateTimeOffset CapturedAt,
    ReadOnlyMemory<byte> Data,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    AudioSampleEncoding Encoding)
{
    public int ByteCount => Data.Length;
}