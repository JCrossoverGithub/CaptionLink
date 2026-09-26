using System.Globalization;
using CaptionLink.Core.Diarization;

namespace CaptionLink.OverlapBenchmark;

internal static class DiarizationRttmWriter
{
    public static async Task WriteAsync(
        string directory,
        string recordingId,
        double recordingDurationSeconds,
        IReadOnlyCollection<SpeakerActivity> activities,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            directory);

        ArgumentException.ThrowIfNullOrWhiteSpace(
            recordingId);

        ArgumentNullException.ThrowIfNull(
            activities);

        Directory.CreateDirectory(directory);

        string outputPath =
            Path.Combine(
                directory,
                recordingId + ".rttm");

        string[] lines =
            activities
                .Where(activity =>
                    activity.IsFinal)
                .Select(activity =>
                    ToRttmInterval(
                        activity,
                        recordingDurationSeconds))
                .Where(interval =>
                    interval is not null)
                .Select(interval =>
                    interval!)
                .OrderBy(interval =>
                    interval.StartSeconds)
                .ThenBy(interval =>
                    interval.EndSeconds)
                .ThenBy(interval =>
                    interval.SpeakerId,
                    StringComparer.Ordinal)
                .Select(interval =>
                    ToRttmLine(
                        recordingId,
                        interval))
                .ToArray();

        await File.WriteAllLinesAsync(
            outputPath,
            lines,
            cancellationToken);
    }

    private static RttmInterval? ToRttmInterval(
        SpeakerActivity activity,
        double recordingDurationSeconds)
    {
        double startSeconds =
            Math.Clamp(
                activity.StartTime.TotalSeconds,
                0,
                recordingDurationSeconds);

        double endSeconds =
            Math.Clamp(
                activity.EndTime.TotalSeconds,
                0,
                recordingDurationSeconds);

        if (endSeconds <= startSeconds)
        {
            return null;
        }

        return new RttmInterval(
            activity.SpeakerId,
            startSeconds,
            endSeconds);
    }

    private static string ToRttmLine(
        string recordingId,
        RttmInterval interval)
    {
        double durationSeconds =
            interval.EndSeconds -
            interval.StartSeconds;

        return string.Join(
            ' ',
            "SPEAKER",
            recordingId,
            "1",
            interval.StartSeconds.ToString(
                "0.000000",
                CultureInfo.InvariantCulture),
            durationSeconds.ToString(
                "0.000000",
                CultureInfo.InvariantCulture),
            "<NA>",
            "<NA>",
            interval.SpeakerId,
            "<NA>",
            "<NA>");
    }

    private sealed record RttmInterval(
        string SpeakerId,
        double StartSeconds,
        double EndSeconds);
}
