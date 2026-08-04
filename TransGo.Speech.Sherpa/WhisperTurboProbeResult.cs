namespace TransGo.Speech.Sherpa;

public sealed record WhisperTurboProbeResult(
    string Text,
    TimeSpan AudioDuration,
    TimeSpan ModelLoadTime,
    TimeSpan DecodeTime)
{
    public double RealTimeFactor =>
        AudioDuration.TotalSeconds <= 0
            ? 0
            : DecodeTime.TotalSeconds /
              AudioDuration.TotalSeconds;
}