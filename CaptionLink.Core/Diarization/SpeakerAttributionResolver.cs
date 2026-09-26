using CaptionLink.Core.Transcription;

namespace CaptionLink.Core.Diarization;

/// <summary>
/// Resolves transcript timing against diarization activity.
/// It reports multiple speakers only when their activity truly
/// overlaps inside the transcript segment.
/// </summary>
public static class SpeakerAttributionResolver
{
    private static readonly TimeSpan
        MinimumSecondaryConcurrentOverlap =
            TimeSpan.FromMilliseconds(240);

    private const double
        MinimumSecondarySegmentCoverage =
            0.20;

    public static string? ResolveSpeakerId(
        TranscriptResult result,
        IEnumerable<SpeakerActivity> activities)
    {
        return Resolve(
            result,
            activities).CompositeSpeakerId;
    }

    public static SpeakerAttributionDecision Resolve(
        TranscriptResult result,
        IEnumerable<SpeakerActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(activities);

        if (
            result.ResultStartTime is not
                TimeSpan resultStart
            ||
            result.ResultEndTime is not
                TimeSpan resultEnd
            ||
            resultEnd <= resultStart)
        {
            return EmptyDecision();
        }

        TimeSpan segmentDuration =
            resultEnd - resultStart;

        Dictionary<string, List<TimeInterval>>
            intervalsBySpeaker =
                CollectIntervalsBySpeaker(
                    activities,
                    resultStart,
                    resultEnd);

        if (intervalsBySpeaker.Count == 0)
        {
            return EmptyDecision();
        }

        var summaries =
            new List<MutableSpeakerSummary>();

        foreach (
            KeyValuePair<
                string,
                List<TimeInterval>> entry
            in intervalsBySpeaker)
        {
            List<TimeInterval> mergedIntervals =
                MergeIntervals(entry.Value);

            TimeSpan totalOverlap =
                SumDuration(mergedIntervals);

            if (totalOverlap <= TimeSpan.Zero)
            {
                continue;
            }

            TimeSpan latestEnd =
                mergedIntervals[^1].End;

            summaries.Add(
                new MutableSpeakerSummary(
                    entry.Key,
                    mergedIntervals,
                    totalOverlap,
                    totalOverlap.TotalSeconds /
                        segmentDuration.TotalSeconds,
                    latestEnd));
        }

        if (summaries.Count == 0)
        {
            return EmptyDecision();
        }

        summaries.Sort(
            CompareSummaries);

        MutableSpeakerSummary primary =
            summaries[0];

        var selectedSpeakerIds =
            new List<string>
            {
                primary.SpeakerId,
            };

        var overlaps =
            new List<SpeakerOverlap>(
                summaries.Count);

        foreach (
            MutableSpeakerSummary summary
            in summaries)
        {
            TimeSpan concurrentWithPrimary =
                ReferenceEquals(
                    summary,
                    primary)
                    ? TimeSpan.Zero
                    : CalculateConcurrentDuration(
                        primary.Intervals,
                        summary.Intervals);

            if (
                !ReferenceEquals(
                    summary,
                    primary)
                &&
                concurrentWithPrimary >=
                    MinimumSecondaryConcurrentOverlap
                &&
                summary.SegmentCoverage >=
                    MinimumSecondarySegmentCoverage)
            {
                selectedSpeakerIds.Add(
                    summary.SpeakerId);
            }

            overlaps.Add(
                new SpeakerOverlap(
                    SpeakerId:
                        summary.SpeakerId,

                    OverlapDuration:
                        summary.TotalOverlap,

                    SegmentCoverage:
                        summary.SegmentCoverage,

                    LatestEnd:
                        summary.LatestEnd,

                    ConcurrentWithPrimaryDuration:
                        concurrentWithPrimary));
        }

        return new SpeakerAttributionDecision(
            PrimarySpeakerId:
                primary.SpeakerId,

            SpeakerIds:
                selectedSpeakerIds,

            Overlaps:
                overlaps);
    }

    private static Dictionary<
        string,
        List<TimeInterval>>
        CollectIntervalsBySpeaker(
            IEnumerable<SpeakerActivity> activities,
            TimeSpan resultStart,
            TimeSpan resultEnd)
    {
        var intervalsBySpeaker =
            new Dictionary<
                string,
                List<TimeInterval>>(
                    StringComparer.OrdinalIgnoreCase);

        foreach (
            SpeakerActivity activity
            in activities)
        {
            if (
                string.IsNullOrWhiteSpace(
                    activity.SpeakerId)
                ||
                activity.EndTime <=
                    activity.StartTime)
            {
                continue;
            }

            TimeSpan overlapStart =
                resultStart >
                    activity.StartTime
                    ? resultStart
                    : activity.StartTime;

            TimeSpan overlapEnd =
                resultEnd <
                    activity.EndTime
                    ? resultEnd
                    : activity.EndTime;

            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            string speakerId =
                activity.SpeakerId.Trim();

            if (
                !intervalsBySpeaker.TryGetValue(
                    speakerId,
                    out List<TimeInterval>?
                        speakerIntervals))
            {
                speakerIntervals =
                    new List<TimeInterval>();

                intervalsBySpeaker.Add(
                    speakerId,
                    speakerIntervals);
            }

            speakerIntervals.Add(
                new TimeInterval(
                    overlapStart,
                    overlapEnd));
        }

        return intervalsBySpeaker;
    }

    private static List<TimeInterval>
        MergeIntervals(
            List<TimeInterval> intervals)
    {
        intervals.Sort(
            static (left, right) =>
            {
                int startComparison =
                    left.Start.CompareTo(
                        right.Start);

                return startComparison != 0
                    ? startComparison
                    : left.End.CompareTo(
                        right.End);
            });

        var merged =
            new List<TimeInterval>();

        foreach (
            TimeInterval interval
            in intervals)
        {
            if (merged.Count == 0)
            {
                merged.Add(interval);
                continue;
            }

            TimeInterval previous =
                merged[^1];

            if (interval.Start <= previous.End)
            {
                merged[^1] =
                    new TimeInterval(
                        previous.Start,
                        interval.End >
                            previous.End
                            ? interval.End
                            : previous.End);

                continue;
            }

            merged.Add(interval);
        }

        return merged;
    }

    private static TimeSpan SumDuration(
        IEnumerable<TimeInterval> intervals)
    {
        TimeSpan total =
            TimeSpan.Zero;

        foreach (
            TimeInterval interval
            in intervals)
        {
            total +=
                interval.End -
                interval.Start;
        }

        return total;
    }

    private static TimeSpan
        CalculateConcurrentDuration(
            IReadOnlyList<TimeInterval>
                primaryIntervals,
            IReadOnlyList<TimeInterval>
                secondaryIntervals)
    {
        int primaryIndex = 0;
        int secondaryIndex = 0;

        TimeSpan concurrentDuration =
            TimeSpan.Zero;

        while (
            primaryIndex <
                primaryIntervals.Count
            &&
            secondaryIndex <
                secondaryIntervals.Count)
        {
            TimeInterval primary =
                primaryIntervals[
                    primaryIndex];

            TimeInterval secondary =
                secondaryIntervals[
                    secondaryIndex];

            TimeSpan concurrentStart =
                primary.Start >
                    secondary.Start
                    ? primary.Start
                    : secondary.Start;

            TimeSpan concurrentEnd =
                primary.End <
                    secondary.End
                    ? primary.End
                    : secondary.End;

            if (
                concurrentEnd >
                    concurrentStart)
            {
                concurrentDuration +=
                    concurrentEnd -
                    concurrentStart;
            }

            if (
                primary.End <=
                    secondary.End)
            {
                primaryIndex++;
            }
            else
            {
                secondaryIndex++;
            }
        }

        return concurrentDuration;
    }

    private static int CompareSummaries(
        MutableSpeakerSummary left,
        MutableSpeakerSummary right)
    {
        int totalComparison =
            right.TotalOverlap.CompareTo(
                left.TotalOverlap);

        if (totalComparison != 0)
        {
            return totalComparison;
        }

        int latestEndComparison =
            right.LatestEnd.CompareTo(
                left.LatestEnd);

        if (latestEndComparison != 0)
        {
            return latestEndComparison;
        }

        return StringComparer
            .OrdinalIgnoreCase
            .Compare(
                left.SpeakerId,
                right.SpeakerId);
    }

    private static SpeakerAttributionDecision
        EmptyDecision()
    {
        return new SpeakerAttributionDecision(
            PrimarySpeakerId:
                null,

            SpeakerIds:
                Array.Empty<string>(),

            Overlaps:
                Array.Empty<SpeakerOverlap>());
    }

    private sealed record MutableSpeakerSummary(
        string SpeakerId,
        IReadOnlyList<TimeInterval> Intervals,
        TimeSpan TotalOverlap,
        double SegmentCoverage,
        TimeSpan LatestEnd);

    private readonly record struct TimeInterval(
        TimeSpan Start,
        TimeSpan End);
}
