namespace CaptionLink.Application;

/// <summary>
/// Reports that optional external speaker attribution became unavailable
/// while the captioning session continues to run.
/// </summary>
public sealed class SpeakerAttributionUnavailableEventArgs
    : EventArgs
{
    public SpeakerAttributionUnavailableEventArgs(
        Exception exception)
    {
        Exception =
            exception
            ?? throw new ArgumentNullException(
                nameof(exception));
    }

    public Exception Exception { get; }
}
