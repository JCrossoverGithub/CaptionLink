using System.Collections.Concurrent;
using CaptionLink.Core.Diarization;
using CaptionLink.OverlapBenchmark;

namespace CaptionLink.OverlapTuner;

public sealed record ReplayRecording(
    SortformerProbabilityTrace Trace,
    IReadOnlyList<TimeInterval> ReferenceOverlap);

public sealed class ReplayTuner
{
    private const double MinimumStrictPrecision = 0.68;
    private const double MinimumStrictRecall = 0.68;
    private const double ShortRecallTolerance = 0.005;
    private const double MaximumP95FinalizationLatencyMilliseconds = 1360;

    private readonly TunerOptions _options;

    public ReplayTuner(TunerOptions options)
    {
        _options = options;
    }

    public async Task<ValidationReport> RunValidationAsync(
        CancellationToken cancellationToken)
    {
        string candidatePath =
            _options.ValidationCandidatePath ??
            throw new InvalidOperationException(
                "Validation mode requires --validate-candidate.");

        FrozenValidationCandidate frozen =
            await FrozenValidationCandidateStore.LoadAsync(
                candidatePath,
                cancellationToken);

        ReplayRecording[] recordings =
            await LoadRecordingsAsync(cancellationToken);

        EnsureCompleteValidationSet(recordings);

        Console.WriteLine(
            $"Loaded {recordings.Length} held-out traces " +
            $"({recordings.Sum(item => item.Trace.Header.AudioDurationSeconds) / 3600:0.00} hours)." );

        Console.WriteLine(
            "Validation mode: evaluating exactly the production " +
            $"baseline and frozen candidate '{frozen.CandidateId}'.");

        TuningCandidateResult baseline =
            Evaluate(
                TuningConfiguration.Baseline,
                recordings);

        TuningCandidateResult candidate =
            Evaluate(
                frozen.Configuration,
                recordings);

        ValidationAcceptanceCriteria acceptance =
            frozen.Acceptance;

        bool latencyIsAcceptable =
            candidate.P95FinalizationLatencyMilliseconds is null ||
            candidate.P95FinalizationLatencyMilliseconds <=
                acceptance.MaximumP95FinalizationLatencyMilliseconds;

        var checks = new ValidationChecks(
            candidate.Strict.F1 >=
                acceptance.MinimumStrictF1,
            candidate.Strict.Precision >=
                acceptance.MinimumStrictPrecision,
            candidate.Strict.Recall >=
                acceptance.MinimumStrictRecall,
            candidate.UnderHalfSecondRecall >=
                baseline.UnderHalfSecondRecall -
                acceptance.MaximumUnderHalfSecondRecallDrop,
            latencyIsAcceptable,
            !acceptance.RequireStrictF1ImprovementOverBaseline ||
                candidate.Strict.F1 > baseline.Strict.F1);

        bool passed =
            checks.StrictF1AtLeastMinimum &&
            checks.StrictPrecisionAtLeastMinimum &&
            checks.StrictRecallAtLeastMinimum &&
            checks.UnderHalfSecondRecallPreserved &&
            checks.P95FinalizationLatencyWithinMaximum &&
            checks.StrictF1ImprovesOverBaseline;

        candidate = candidate with
        {
            MeetsGuardrails = passed,
        };

        var deltas = new ValidationMetricDeltas(
            candidate.Strict.Precision -
                baseline.Strict.Precision,
            candidate.Strict.Recall -
                baseline.Strict.Recall,
            candidate.Strict.F1 -
                baseline.Strict.F1,
            candidate.Practical.F1 -
                baseline.Practical.F1,
            candidate.UnderHalfSecondRecall -
                baseline.UnderHalfSecondRecall,
            NullableDelta(
                candidate.P95FinalizationLatencyMilliseconds,
                baseline.P95FinalizationLatencyMilliseconds),
            candidate.FalseOverlapSecondsPerHour -
                baseline.FalseOverlapSecondsPerHour,
            candidate.MissedOverlapSecondsPerHour -
                baseline.MissedOverlapSecondsPerHour);

        SortformerProbabilityTraceHeader header =
            recordings[0].Trace.Header;

        return new ValidationReport(
            DateTimeOffset.UtcNow,
            recordings.Length,
            recordings.Sum(item =>
                item.Trace.Header.AudioDurationSeconds) / 3600,
            header.Model,
            header.StreamingProfile,
            _options.PracticalCollar.TotalMilliseconds,
            frozen,
            baseline,
            candidate,
            deltas,
            checks,
            passed);
    }

    public async Task<TuningReport> RunAsync(
        CancellationToken cancellationToken)
    {
        ReplayRecording[] recordings =
            await LoadRecordingsAsync(cancellationToken);

        TuningConfiguration[] configurations =
            BuildConfigurations().ToArray();

        Console.WriteLine(
            $"Loaded {recordings.Length} traces " +
            $"({recordings.Sum(item => item.Trace.Header.AudioDurationSeconds) / 3600:0.00} hours)." );

        Console.WriteLine(
            $"Evaluating {configurations.Length} detector configurations " +
            $"with {_options.Parallelism} CPU worker(s)." );

        TuningCandidateResult baseline =
            Evaluate(
                TuningConfiguration.Baseline,
                recordings);

        double minimumShortRecall = Math.Max(
            0,
            baseline.UnderHalfSecondRecall -
                ShortRecallTolerance);

        var guardrails = new TuningGuardrails(
            MinimumStrictPrecision,
            MinimumStrictRecall,
            minimumShortRecall,
            MaximumP95FinalizationLatencyMilliseconds);

        var results =
            new ConcurrentBag<TuningCandidateResult>();

        int completed = 0;

        await Parallel.ForEachAsync(
            configurations.Where(configuration =>
                configuration != TuningConfiguration.Baseline),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.Parallelism,
                CancellationToken = cancellationToken,
            },
            (configuration, _) =>
            {
                TuningCandidateResult result =
                    ApplyGuardrails(
                        Evaluate(configuration, recordings),
                        guardrails);

                results.Add(result);

                int current = Interlocked.Increment(
                    ref completed);

                if (current % 25 == 0 ||
                    current == configurations.Length - 1)
                {
                    Console.WriteLine(
                        $"  evaluated {current}/" +
                        $"{configurations.Length - 1}");
                }

                return ValueTask.CompletedTask;
            });

        baseline = ApplyGuardrails(baseline, guardrails);
        results.Add(baseline);

        TuningCandidateResult[] ranked = results
            .OrderByDescending(result => result.MeetsGuardrails)
            .ThenByDescending(result => result.Strict.F1)
            .ThenByDescending(result => result.Practical.F1)
            .ThenByDescending(result => result.UnderHalfSecondRecall)
            .ThenBy(result =>
                result.P95FinalizationLatencyMilliseconds ??
                double.PositiveInfinity)
            .ThenBy(result => result.Configuration.Id)
            .ToArray();

        TuningCandidateResult recommended =
            ranked.FirstOrDefault(result => result.MeetsGuardrails) ??
            baseline;

        SortformerProbabilityTraceHeader header =
            recordings[0].Trace.Header;

        return new TuningReport(
            DateTimeOffset.UtcNow,
            recordings.Length,
            recordings.Sum(item =>
                item.Trace.Header.AudioDurationSeconds) / 3600,
            header.Model,
            header.StreamingProfile,
            _options.PracticalCollar.TotalMilliseconds,
            guardrails,
            baseline,
            recommended,
            ranked);
    }

    public IReadOnlyList<TuningConfiguration>
        BuildConfigurations()
    {
        var configurations =
            new HashSet<TuningConfiguration>
            {
                TuningConfiguration.Baseline,
            };

        foreach (double startThreshold in
            _options.StartThresholds)
        foreach (double stopThreshold in
            _options.StopThresholds)
        foreach (int onsetFrames in
            _options.ConsecutiveStartFrames)
        foreach (int minimumOverlap in
            _options.MinimumOverlapMilliseconds)
        foreach (int startHysteresis in
            _options.StartHysteresisMilliseconds)
        foreach (int endHysteresis in
            _options.EndHysteresisMilliseconds)
        foreach (int mergeGap in
            _options.MergeGapMilliseconds)
        {
            if (stopThreshold > startThreshold ||
                endHysteresis < mergeGap)
            {
                continue;
            }

            configurations.Add(
                new TuningConfiguration(
                    startThreshold,
                    stopThreshold,
                    onsetFrames,
                    minimumOverlap,
                    startHysteresis,
                    endHysteresis,
                    mergeGap));
        }

        return configurations
            .OrderBy(configuration => configuration.Id)
            .ToArray();
    }

    public TuningCandidateResult Evaluate(
        TuningConfiguration configuration,
        IReadOnlyList<ReplayRecording> recordings)
    {
        double evaluated = 0;
        double strictTruePositive = 0;
        double strictFalsePositive = 0;
        double strictFalseNegative = 0;

        double practicalEvaluated = 0;
        double practicalTruePositive = 0;
        double practicalFalsePositive = 0;
        double practicalFalseNegative = 0;

        int shortRegions = 0;
        int shortDetected = 0;
        double totalAudioSeconds = 0;

        var finalizationLatencies =
            new List<double>();

        foreach (ReplayRecording recording in recordings)
        {
            ReplayRecordingResult replay =
                Replay(configuration, recording);

            TimelineScore strict =
                BinaryTimelineScorer.Score(
                    recording.Trace.Header.AudioDurationSeconds,
                    recording.ReferenceOverlap,
                    replay.Predicted);

            TimelineScore practical =
                BinaryTimelineScorer.Score(
                    recording.Trace.Header.AudioDurationSeconds,
                    recording.ReferenceOverlap,
                    replay.Predicted,
                    _options.PracticalCollar.TotalSeconds);

            evaluated += strict.EvaluatedSeconds;
            strictTruePositive += strict.TruePositiveSeconds;
            strictFalsePositive += strict.FalsePositiveSeconds;
            strictFalseNegative += strict.FalseNegativeSeconds;

            practicalEvaluated += practical.EvaluatedSeconds;
            practicalTruePositive += practical.TruePositiveSeconds;
            practicalFalsePositive += practical.FalsePositiveSeconds;
            practicalFalseNegative += practical.FalseNegativeSeconds;

            TimeInterval[] shortReferences =
                recording.ReferenceOverlap
                    .Where(interval =>
                        interval.DurationSeconds < 0.5)
                    .ToArray();

            shortRegions += shortReferences.Length;
            shortDetected += shortReferences.Count(reference =>
                replay.Predicted.Any(prediction =>
                    prediction.Overlaps(reference)));

            finalizationLatencies.AddRange(
                replay.FinalizationLatenciesSeconds);

            totalAudioSeconds +=
                recording.Trace.Header.AudioDurationSeconds;
        }

        var aggregateStrict = new TimelineScore(
            evaluated,
            strictTruePositive,
            strictFalsePositive,
            strictFalseNegative);

        var aggregatePractical = new TimelineScore(
            practicalEvaluated,
            practicalTruePositive,
            practicalFalsePositive,
            practicalFalseNegative);

        double[] sortedLatencies =
            finalizationLatencies.Order().ToArray();

        return new TuningCandidateResult(
            configuration,
            aggregateStrict,
            aggregatePractical,
            shortRegions,
            shortDetected,
            shortRegions > 0
                ? shortDetected / (double)shortRegions
                : 1,
            BenchmarkReportBuilder.Percentile(
                sortedLatencies,
                0.95) * 1000,
            PerHour(
                aggregateStrict.FalsePositiveSeconds,
                totalAudioSeconds),
            PerHour(
                aggregateStrict.FalseNegativeSeconds,
                totalAudioSeconds));
    }

    private ReplayRecordingResult Replay(
        TuningConfiguration configuration,
        ReplayRecording recording)
    {
        SortformerProbabilityTrace trace = recording.Trace;

        var tracker = new ProbabilityActivityTracker(
            trace.Header.MaximumSpeakers,
            new ProbabilityActivityOptions(
                configuration.StartThreshold,
                configuration.StopThreshold,
                configuration.ConsecutiveStartFrames));

        var detector = new SpeakerOverlapDetector(
            new OverlapDetectionOptions
            {
                MinimumOverlapDuration = TimeSpan.FromMilliseconds(
                    configuration.MinimumOverlapMilliseconds),
                StartHysteresis = TimeSpan.FromMilliseconds(
                    configuration.StartHysteresisMilliseconds),
                EndHysteresis = TimeSpan.FromMilliseconds(
                    configuration.EndHysteresisMilliseconds),
                MergeGap = TimeSpan.FromMilliseconds(
                    configuration.MergeGapMilliseconds),
            });

        var finalRegions = new List<OverlapRegion>();
        var finalizationLatencies = new List<double>();
        double observedActivityEnd = 0;

        detector.RegionUpdated += (_, eventArgs) =>
        {
            if (!eventArgs.Region.IsFinal)
            {
                return;
            }

            finalRegions.Add(eventArgs.Region);
            finalizationLatencies.Add(
                Math.Max(
                    0,
                    observedActivityEnd -
                    eventArgs.Region.EndTime.TotalSeconds));
        };

        foreach (var batch in trace.Batches)
        {
            ProcessActivities(
                tracker.Consume(batch));
        }

        ProcessActivities(
            tracker.Flush(
                trace.Header.FrameDurationSeconds));

        detector.Complete(
            TimeSpan.FromSeconds(
                trace.Header.AudioDurationSeconds));

        TimeInterval[] predicted = finalRegions
            .Select(region =>
                new TimeInterval(
                    Math.Max(
                        0,
                        region.StartTime.TotalSeconds),
                    Math.Min(
                        trace.Header.AudioDurationSeconds,
                        region.EndTime.TotalSeconds)))
            .Where(interval => interval.DurationSeconds > 0)
            .ToArray();

        return new ReplayRecordingResult(
            predicted,
            finalizationLatencies);

        void ProcessActivities(
            IReadOnlyList<SpeakerActivity> activities)
        {
            foreach (SpeakerActivity activity in activities)
            {
                observedActivityEnd = Math.Max(
                    observedActivityEnd,
                    activity.EndTime.TotalSeconds);

                detector.Process(activity);
            }
        }
    }

    private async Task<ReplayRecording[]> LoadRecordingsAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_options.TraceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Trace directory not found: {_options.TraceDirectory}");
        }

        if (!Directory.Exists(_options.RttmDirectory))
        {
            throw new DirectoryNotFoundException(
                $"RTTM directory not found: {_options.RttmDirectory}");
        }

        string[] paths = Directory
            .EnumerateFiles(
                _options.TraceDirectory,
                "*.sortformer-trace.json.gz",
                SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                string recordingId = Path
                    .GetFileName(path)
                    .Replace(
                        ".sortformer-trace.json.gz",
                        string.Empty,
                        StringComparison.OrdinalIgnoreCase);

                return _options.IncludedRecordingIds.Count == 0 ||
                    _options.IncludedRecordingIds.Contains(recordingId);
            })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths.Length == 0)
        {
            throw new InvalidDataException(
                "No matching Sortformer probability traces were found.");
        }

        var recordings = new List<ReplayRecording>();

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SortformerProbabilityTrace trace =
                await SortformerProbabilityTraceStore.LoadAsync(
                    path,
                    cancellationToken);

            string rttmPath = Path.Combine(
                _options.RttmDirectory,
                trace.Header.RecordingId + ".rttm");

            if (!File.Exists(rttmPath))
            {
                throw new FileNotFoundException(
                    $"RTTM file not found for trace '{trace.Header.RecordingId}'.",
                    rttmPath);
            }

            IReadOnlyList<TimeInterval> reference =
                ReferenceOverlapBuilder
                    .Build(RttmReader.Read(rttmPath))
                    .Select(interval =>
                        new TimeInterval(
                            Math.Max(0, interval.StartSeconds),
                            Math.Min(
                                trace.Header.AudioDurationSeconds,
                                interval.EndSeconds)))
                    .Where(interval => interval.DurationSeconds > 0)
                    .ToArray();

            recordings.Add(
                new ReplayRecording(trace, reference));
        }

        string[] models = recordings
            .Select(item => item.Trace.Header.Model)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] profiles = recordings
            .Select(item => item.Trace.Header.StreamingProfile)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (models.Length != 1 || profiles.Length != 1)
        {
            throw new InvalidDataException(
                "Traces from different Sortformer models or streaming " +
                "profiles cannot be tuned together.");
        }

        if (_options.IncludedRecordingIds.Count > 0)
        {
            string[] missing = _options.IncludedRecordingIds
                .Except(
                    recordings.Select(item =>
                        item.Trace.Header.RecordingId),
                    StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (missing.Length > 0)
            {
                throw new InvalidDataException(
                    "Requested traces were not found: " +
                    string.Join(", ", missing));
            }
        }

        return recordings.ToArray();
    }

    private static TuningCandidateResult ApplyGuardrails(
        TuningCandidateResult result,
        TuningGuardrails guardrails)
    {
        bool latencyIsAcceptable =
            result.P95FinalizationLatencyMilliseconds is null ||
            result.P95FinalizationLatencyMilliseconds <=
                guardrails.MaximumP95FinalizationLatencyMilliseconds;

        return result with
        {
            MeetsGuardrails =
                result.Strict.Precision >=
                    guardrails.MinimumStrictPrecision &&
                result.Strict.Recall >=
                    guardrails.MinimumStrictRecall &&
                result.UnderHalfSecondRecall >=
                    guardrails.MinimumUnderHalfSecondRecall &&
                latencyIsAcceptable,
        };
    }

    private static double PerHour(
        double seconds,
        double audioSeconds) =>
        audioSeconds > 0
            ? seconds / (audioSeconds / 3600)
            : 0;

    private void EnsureCompleteValidationSet(
        IReadOnlyList<ReplayRecording> recordings)
    {
        string[] referenceIds = Directory
            .EnumerateFiles(
                _options.RttmDirectory,
                "*.rttm",
                SearchOption.TopDirectoryOnly)
            .Select(path =>
                Path.GetFileNameWithoutExtension(path)!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (referenceIds.Length == 0)
        {
            throw new InvalidDataException(
                "No RTTM files were found for held-out validation.");
        }

        HashSet<string> traceIds = recordings
            .Select(recording =>
                recording.Trace.Header.RecordingId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] missing = referenceIds
            .Where(id => !traceIds.Contains(id))
            .ToArray();

        if (missing.Length > 0)
        {
            string sample = string.Join(
                ", ",
                missing.Take(10));

            throw new InvalidDataException(
                $"Held-out validation requires a trace for every RTTM " +
                $"file. Missing {missing.Length}: {sample}" +
                (missing.Length > 10 ? ", ..." : string.Empty));
        }
    }

    private static double? NullableDelta(
        double? candidate,
        double? baseline) =>
        candidate is double candidateValue &&
        baseline is double baselineValue
            ? candidateValue - baselineValue
            : null;

    private sealed record ReplayRecordingResult(
        IReadOnlyList<TimeInterval> Predicted,
        IReadOnlyList<double> FinalizationLatenciesSeconds);
}
