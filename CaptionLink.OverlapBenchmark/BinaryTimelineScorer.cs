namespace CaptionLink.OverlapBenchmark;

public sealed record TimelineScore(
    double EvaluatedSeconds,
    double TruePositiveSeconds,
    double FalsePositiveSeconds,
    double FalseNegativeSeconds)
{
    public double Precision =>
        Divide(
            TruePositiveSeconds,
            TruePositiveSeconds + FalsePositiveSeconds);

    public double Recall =>
        Divide(
            TruePositiveSeconds,
            TruePositiveSeconds + FalseNegativeSeconds);

    public double F1 =>
        Divide(
            2 * Precision * Recall,
            Precision + Recall);

    private static double Divide(
        double numerator,
        double denominator) =>
        denominator > 0
            ? numerator / denominator
            : numerator == 0
                ? 1
                : 0;
}

public sealed record BoundaryMatchSummary(
    int MatchedRegions,
    IReadOnlyList<double> BoundaryErrorsSeconds);

public static class BinaryTimelineScorer
{
    public static TimelineScore Score(
        double recordingDurationSeconds,
        IReadOnlyList<TimeInterval> reference,
        IReadOnlyList<TimeInterval> predicted,
        double collarSeconds = 0)
    {
        if (
            !double.IsFinite(recordingDurationSeconds) ||
            recordingDurationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordingDurationSeconds));
        }

        if (
            !double.IsFinite(collarSeconds) ||
            collarSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(collarSeconds));
        }

        ValidateIntervals(reference, recordingDurationSeconds);
        ValidateIntervals(predicted, recordingDurationSeconds);

        IReadOnlyList<TimeInterval> ignored =
            BuildBoundaryCollars(
                reference,
                recordingDurationSeconds,
                collarSeconds);

        double[] boundaries =
            reference
                .Concat(predicted)
                .Concat(ignored)
                .SelectMany(interval =>
                    new[]
                    {
                        interval.StartSeconds,
                        interval.EndSeconds,
                    })
                .Append(0)
                .Append(recordingDurationSeconds)
                .Distinct()
                .Where(value =>
                    value >= 0 &&
                    value <= recordingDurationSeconds)
                .Order()
                .ToArray();

        double evaluated = 0;
        double truePositive = 0;
        double falsePositive = 0;
        double falseNegative = 0;

        for (
            int index = 0;
            index < boundaries.Length - 1;
            index++)
        {
            double start = boundaries[index];
            double end = boundaries[index + 1];
            double duration = end - start;

            if (duration <= 0)
            {
                continue;
            }

            double midpoint = start + duration / 2;

            if (Contains(ignored, midpoint))
            {
                continue;
            }

            bool isReference =
                Contains(reference, midpoint);

            bool isPredicted =
                Contains(predicted, midpoint);

            evaluated += duration;

            if (isReference && isPredicted)
            {
                truePositive += duration;
            }
            else if (isPredicted)
            {
                falsePositive += duration;
            }
            else if (isReference)
            {
                falseNegative += duration;
            }
        }

        return new TimelineScore(
            evaluated,
            truePositive,
            falsePositive,
            falseNegative);
    }

    public static BoundaryMatchSummary MatchBoundaries(
        IReadOnlyList<TimeInterval> reference,
        IReadOnlyList<TimeInterval> predicted)
    {
        var candidates =
            new List<(
                int ReferenceIndex,
                int PredictedIndex,
                double Intersection)>();

        for (
            int referenceIndex = 0;
            referenceIndex < reference.Count;
            referenceIndex++)
        {
            for (
                int predictedIndex = 0;
                predictedIndex < predicted.Count;
                predictedIndex++)
            {
                double intersection =
                    reference[referenceIndex]
                        .IntersectionSeconds(
                            predicted[predictedIndex]);

                if (intersection > 0)
                {
                    candidates.Add(
                        (
                            referenceIndex,
                            predictedIndex,
                            intersection));
                }
            }
        }

        var usedReference = new HashSet<int>();
        var usedPredicted = new HashSet<int>();
        var errors = new List<double>();

        foreach (var candidate in
            candidates
                .OrderByDescending(item =>
                    item.Intersection))
        {
            if (
                !usedReference.Add(
                    candidate.ReferenceIndex) ||
                !usedPredicted.Add(
                    candidate.PredictedIndex))
            {
                continue;
            }

            TimeInterval expected =
                reference[candidate.ReferenceIndex];

            TimeInterval actual =
                predicted[candidate.PredictedIndex];

            errors.Add(
                Math.Abs(
                    expected.StartSeconds -
                    actual.StartSeconds));

            errors.Add(
                Math.Abs(
                    expected.EndSeconds -
                    actual.EndSeconds));
        }

        return new BoundaryMatchSummary(
            usedReference.Count,
            errors);
    }

    private static IReadOnlyList<TimeInterval>
        BuildBoundaryCollars(
            IReadOnlyList<TimeInterval> reference,
            double duration,
            double collar)
    {
        if (collar <= 0)
        {
            return [];
        }

        return ReferenceOverlapBuilder.MergeTouching(
            reference.SelectMany(interval =>
                new[]
                {
                    new TimeInterval(
                        Math.Max(
                            0,
                            interval.StartSeconds - collar),
                        Math.Min(
                            duration,
                            interval.StartSeconds + collar)),

                    new TimeInterval(
                        Math.Max(
                            0,
                            interval.EndSeconds - collar),
                        Math.Min(
                            duration,
                            interval.EndSeconds + collar)),
                }));
    }

    private static bool Contains(
        IReadOnlyList<TimeInterval> intervals,
        double timeSeconds) =>
        intervals.Any(interval =>
            interval.StartSeconds <= timeSeconds &&
            interval.EndSeconds > timeSeconds);

    private static void ValidateIntervals(
        IReadOnlyList<TimeInterval> intervals,
        double duration)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        if (intervals.Any(interval =>
            !double.IsFinite(interval.StartSeconds) ||
            !double.IsFinite(interval.EndSeconds) ||
            interval.StartSeconds < 0 ||
            interval.EndSeconds <= interval.StartSeconds ||
            interval.EndSeconds > duration + 0.001))
        {
            throw new ArgumentException(
                "A scoring interval is outside the recording.",
                nameof(intervals));
        }
    }
}
