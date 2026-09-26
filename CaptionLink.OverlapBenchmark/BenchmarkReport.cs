using System.Text.Json.Serialization;

namespace CaptionLink.OverlapBenchmark;

public sealed record RecordingBenchmarkResult(
    [property: JsonPropertyName("recording_id")]
    string RecordingId,

    [property: JsonPropertyName("duration_seconds")]
    double DurationSeconds,

    [property: JsonPropertyName("reference_regions")]
    int ReferenceRegions,

    [property: JsonPropertyName("predicted_regions")]
    int PredictedRegions,

    [property: JsonPropertyName("strict")]
    TimelineScore Strict,

    [property: JsonPropertyName("practical")]
    TimelineScore Practical,

    [property: JsonPropertyName("matched_regions")]
    int MatchedRegions,

    [property: JsonPropertyName("boundary_errors_seconds")]
    IReadOnlyList<double> BoundaryErrorsSeconds,

    [property: JsonPropertyName("finalization_latencies_seconds")]
    IReadOnlyList<double> FinalizationLatenciesSeconds,

    [property: JsonPropertyName("buffer_windows_requested")]
    int? BufferWindowsRequested,

    [property: JsonPropertyName("buffer_windows_available")]
    int? BufferWindowsAvailable,

    [property: JsonPropertyName("reference_intervals")]
    IReadOnlyList<TimeInterval> ReferenceIntervals,

    [property: JsonPropertyName("predicted_intervals")]
    IReadOnlyList<TimeInterval> PredictedIntervals);

public sealed record DurationBucketResult(
    [property: JsonPropertyName("bucket")]
    string Bucket,

    [property: JsonPropertyName("reference_regions")]
    int ReferenceRegions,

    [property: JsonPropertyName("regions_with_any_detection")]
    int RegionsWithAnyDetection,

    [property: JsonPropertyName("region_recall")]
    double RegionRecall);

public sealed record AggregateBenchmarkReport(
    [property: JsonPropertyName("dataset")]
    string Dataset,

    [property: JsonPropertyName("generated_at_utc")]
    DateTimeOffset GeneratedAtUtc,

    [property: JsonPropertyName("recordings")]
    int Recordings,

    [property: JsonPropertyName("selected_recordings")]
    int SelectedRecordings,

    [property: JsonPropertyName("failed_recordings")]
    int FailedRecordings,

    [property: JsonPropertyName("is_complete")]
    bool IsComplete,

    [property: JsonPropertyName("audio_hours")]
    double AudioHours,

    [property: JsonPropertyName("practical_collar_ms")]
    double PracticalCollarMilliseconds,

    [property: JsonPropertyName("realtime")]
    bool Realtime,

    [property: JsonPropertyName("strict")]
    TimelineScore Strict,

    [property: JsonPropertyName("practical")]
    TimelineScore Practical,

    [property: JsonPropertyName("false_overlap_seconds_per_hour")]
    double FalseOverlapSecondsPerHour,

    [property: JsonPropertyName("missed_overlap_seconds_per_hour")]
    double MissedOverlapSecondsPerHour,

    [property: JsonPropertyName("median_boundary_error_ms")]
    double? MedianBoundaryErrorMilliseconds,

    [property: JsonPropertyName("p95_boundary_error_ms")]
    double? P95BoundaryErrorMilliseconds,

    [property: JsonPropertyName("median_finalization_latency_ms")]
    double? MedianFinalizationLatencyMilliseconds,

    [property: JsonPropertyName("p95_finalization_latency_ms")]
    double? P95FinalizationLatencyMilliseconds,

    [property: JsonPropertyName("buffer_window_availability")]
    double? BufferWindowAvailability,

    [property: JsonPropertyName("duration_buckets")]
    IReadOnlyList<DurationBucketResult> DurationBuckets,

    [property: JsonPropertyName("failures")]
    IReadOnlyList<RecordingBenchmarkFailure> Failures,

    [property: JsonPropertyName("per_recording")]
    IReadOnlyList<RecordingBenchmarkResult> PerRecording);

public static class BenchmarkReportBuilder
{
    public static AggregateBenchmarkReport Build(
        IReadOnlyList<RecordingBenchmarkResult> results,
        BenchmarkOptions options,
        int? selectedRecordings = null,
        IReadOnlyList<RecordingBenchmarkFailure>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        failures ??= [];

        int selected = selectedRecordings ?? results.Count;

        double audioSeconds =
            results.Sum(result =>
                result.DurationSeconds);

        TimelineScore strict = Sum(results, practical: false);
        TimelineScore practical = Sum(results, practical: true);

        double[] boundaryErrors =
            results
                .SelectMany(result =>
                    result.BoundaryErrorsSeconds)
                .Order()
                .ToArray();

        double[] finalizationLatencies =
            results
                .SelectMany(result =>
                    result.FinalizationLatenciesSeconds)
                .Order()
                .ToArray();

        int? bufferRequested = options.Realtime
            ? results.Sum(result =>
                result.BufferWindowsRequested ?? 0)
            : null;

        int? bufferAvailable = options.Realtime
            ? results.Sum(result =>
                result.BufferWindowsAvailable ?? 0)
            : null;

        return new AggregateBenchmarkReport(
            Dataset: "VoxConverse v0.3",
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Recordings: results.Count,
            SelectedRecordings: selected,
            FailedRecordings: failures.Count,
            IsComplete:
                results.Count == selected &&
                failures.Count == 0,
            AudioHours: audioSeconds / 3600,
            PracticalCollarMilliseconds:
                options.PracticalCollar.TotalMilliseconds,
            Realtime: options.Realtime,
            Strict: strict,
            Practical: practical,
            FalseOverlapSecondsPerHour:
                PerHour(
                    strict.FalsePositiveSeconds,
                    audioSeconds),
            MissedOverlapSecondsPerHour:
                PerHour(
                    strict.FalseNegativeSeconds,
                    audioSeconds),
            MedianBoundaryErrorMilliseconds:
                Percentile(boundaryErrors, 0.5) * 1000,
            P95BoundaryErrorMilliseconds:
                Percentile(boundaryErrors, 0.95) * 1000,
            MedianFinalizationLatencyMilliseconds:
                Percentile(finalizationLatencies, 0.5) * 1000,
            P95FinalizationLatencyMilliseconds:
                Percentile(finalizationLatencies, 0.95) * 1000,
            BufferWindowAvailability:
                bufferRequested > 0
                    ? bufferAvailable /
                      (double)bufferRequested.Value
                    : null,
            DurationBuckets: BuildDurationBuckets(results),
            Failures: failures,
            PerRecording: results);
    }

    public static double? Percentile(
        IReadOnlyList<double> sortedValues,
        double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return null;
        }

        if (percentile < 0 || percentile > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentile));
        }

        double position =
            (sortedValues.Count - 1) * percentile;

        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);

        if (lower == upper)
        {
            return sortedValues[lower];
        }

        double weight = position - lower;

        return sortedValues[lower] * (1 - weight) +
            sortedValues[upper] * weight;
    }

    private static TimelineScore Sum(
        IEnumerable<RecordingBenchmarkResult> results,
        bool practical)
    {
        TimelineScore[] scores =
            results
                .Select(result =>
                    practical
                        ? result.Practical
                        : result.Strict)
                .ToArray();

        return new TimelineScore(
            scores.Sum(score => score.EvaluatedSeconds),
            scores.Sum(score => score.TruePositiveSeconds),
            scores.Sum(score => score.FalsePositiveSeconds),
            scores.Sum(score => score.FalseNegativeSeconds));
    }

    private static IReadOnlyList<DurationBucketResult>
        BuildDurationBuckets(
            IReadOnlyList<RecordingBenchmarkResult> results)
    {
        (string Name, double Minimum, double Maximum)[] buckets =
        [
            ("under_0.5s", 0, 0.5),
            ("0.5_to_1s", 0.5, 1),
            ("1_to_2s", 1, 2),
            ("2s_and_over", 2, double.PositiveInfinity),
        ];

        return buckets
            .Select(bucket =>
            {
                (TimeInterval Reference, IReadOnlyList<TimeInterval> Predicted)[]
                    candidates =
                        results
                            .SelectMany(result =>
                                result.ReferenceIntervals
                                    .Where(interval =>
                                        interval.DurationSeconds >=
                                            bucket.Minimum &&
                                        interval.DurationSeconds <
                                            bucket.Maximum)
                                    .Select(interval =>
                                        (
                                            interval,
                                            result.PredictedIntervals)))
                            .ToArray();

                int detected = candidates.Count(item =>
                    item.Predicted.Any(prediction =>
                        prediction.Overlaps(item.Reference)));

                return new DurationBucketResult(
                    bucket.Name,
                    candidates.Length,
                    detected,
                    candidates.Length > 0
                        ? detected / (double)candidates.Length
                        : 1);
            })
            .ToArray();
    }

    private static double PerHour(
        double seconds,
        double audioSeconds) =>
        audioSeconds > 0
            ? seconds / (audioSeconds / 3600)
            : 0;
}
