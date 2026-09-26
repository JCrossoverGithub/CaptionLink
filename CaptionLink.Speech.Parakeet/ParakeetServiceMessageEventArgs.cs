namespace CaptionLink.Speech.Parakeet;

public sealed class ParakeetServiceMessageEventArgs
    : EventArgs
{
    public ParakeetServiceMessageEventArgs(
        string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            json);

        Json = json;
    }

    public string Json { get; }
}