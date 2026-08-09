using System.Globalization;

namespace TransGo.OverlapBenchmark;

public static class RttmReader
{
    public static IReadOnlyList<SpeakerInterval> Read(
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var intervals =
            new List<SpeakerInterval>();

        int lineNumber = 0;

        foreach (string rawLine in File.ReadLines(path))
        {
            lineNumber++;

            string line = rawLine.Trim();

            if (
                line.Length == 0 ||
                line.StartsWith('#'))
            {
                continue;
            }

            string[] fields =
                line.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries);

            if (
                fields.Length < 8 ||
                !string.Equals(
                    fields[0],
                    "SPEAKER",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Invalid RTTM row at {path}:{lineNumber}.");
            }

            if (
                !double.TryParse(
                    fields[3],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double startSeconds) ||
                !double.TryParse(
                    fields[4],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double durationSeconds) ||
                !double.IsFinite(startSeconds) ||
                !double.IsFinite(durationSeconds) ||
                startSeconds < 0 ||
                durationSeconds <= 0)
            {
                throw new InvalidDataException(
                    $"Invalid RTTM timing at {path}:{lineNumber}.");
            }

            intervals.Add(
                new SpeakerInterval(
                    RecordingId: fields[1],
                    SpeakerId: fields[7],
                    StartSeconds: startSeconds,
                    EndSeconds:
                        startSeconds + durationSeconds));
        }

        return intervals;
    }
}
