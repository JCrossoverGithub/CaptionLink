namespace TransGo.Core.Transcription;

public sealed record TranscriptionConfiguration(
    string LanguageCode,
    int SampleRate,
    bool EnableInterimResults = true)
{
    public int Channels => 1;

    public int BitsPerSample => 16;
}