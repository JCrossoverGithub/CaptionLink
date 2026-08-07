namespace TransGo.Core.Diarization;

public sealed class OverlapRegionEventArgs : EventArgs
{
    public OverlapRegionEventArgs(
        OverlapRegion region)
    {
        Region = region ??
            throw new ArgumentNullException(
                nameof(region));
    }

    public OverlapRegion Region { get; }
}
