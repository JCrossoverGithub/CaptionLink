namespace TransGo.OverlapBenchmark;

public static class ReferenceOverlapBuilder
{
    public static IReadOnlyList<TimeInterval> Build(
        IReadOnlyList<SpeakerInterval> speakerIntervals)
    {
        ArgumentNullException.ThrowIfNull(speakerIntervals);

        if (speakerIntervals.Count < 2)
        {
            return [];
        }

        double[] boundaries =
            speakerIntervals
                .SelectMany(interval =>
                    new[]
                    {
                        interval.StartSeconds,
                        interval.EndSeconds,
                    })
                .Distinct()
                .Order()
                .ToArray();

        var overlapSegments =
            new List<TimeInterval>();

        for (
            int index = 0;
            index < boundaries.Length - 1;
            index++)
        {
            double start = boundaries[index];
            double end = boundaries[index + 1];

            if (end <= start)
            {
                continue;
            }

            int activeSpeakerCount =
                speakerIntervals
                    .Where(interval =>
                        interval.StartSeconds < end &&
                        interval.EndSeconds > start)
                    .Select(interval =>
                        interval.SpeakerId)
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .Count();

            if (activeSpeakerCount >= 2)
            {
                overlapSegments.Add(
                    new TimeInterval(start, end));
            }
        }

        return MergeTouching(overlapSegments);
    }

    public static IReadOnlyList<TimeInterval> MergeTouching(
        IEnumerable<TimeInterval> intervals)
    {
        TimeInterval[] ordered =
            intervals
                .Where(interval =>
                    interval.EndSeconds >
                        interval.StartSeconds)
                .OrderBy(interval =>
                    interval.StartSeconds)
                .ThenBy(interval =>
                    interval.EndSeconds)
                .ToArray();

        if (ordered.Length == 0)
        {
            return [];
        }

        var merged =
            new List<TimeInterval>();

        TimeInterval current = ordered[0];

        foreach (TimeInterval next in ordered.Skip(1))
        {
            if (next.StartSeconds <= current.EndSeconds)
            {
                current = new TimeInterval(
                    current.StartSeconds,
                    Math.Max(
                        current.EndSeconds,
                        next.EndSeconds));
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }
}
