namespace TransGo.Core.Diarization;

/// <summary>
/// Exposes live speaker-activity updates from a component that
/// performs speaker detection internally.
/// </summary>
public interface ISpeakerActivitySource
{
    event EventHandler<SpeakerActivityEventArgs>?
        ActivityReceived;
}
