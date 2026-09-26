namespace CaptionLink.Core.Diarization;

public sealed record DiarizationConfiguration(
    int SampleRate,
    int MaximumSpeakers = 4,
    bool PublishSpeakerProbabilities = false);
