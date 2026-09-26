namespace CaptionLink.Core.Diarization;

public sealed class SpeakerActivityEventArgs : EventArgs
{
    public SpeakerActivityEventArgs(
        SpeakerActivity activity)
    {
        Activity = activity ??
            throw new ArgumentNullException(
                nameof(activity));
    }

    public SpeakerActivity Activity { get; }
}