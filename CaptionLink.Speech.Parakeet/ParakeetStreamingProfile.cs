namespace CaptionLink.Speech.Parakeet;

public enum ParakeetStreamingProfile
{
    Responsive,
    Accurate
}

public static class
    ParakeetStreamingProfileExtensions
{
    public static string ToServiceValue(
        this ParakeetStreamingProfile profile)
    {
        return profile switch
        {
            ParakeetStreamingProfile.Responsive =>
                "responsive",

            ParakeetStreamingProfile.Accurate =>
                "accurate",

            _ => throw new ArgumentOutOfRangeException(
                nameof(profile),
                profile,
                "Unknown Parakeet streaming profile.")
        };
    }

    public static string ToDisplayName(
        this ParakeetStreamingProfile profile)
    {
        return profile switch
        {
            ParakeetStreamingProfile.Responsive =>
                "Responsive — lower latency",

            ParakeetStreamingProfile.Accurate =>
                "Accurate — better context",

            _ => profile.ToString()
        };
    }
}