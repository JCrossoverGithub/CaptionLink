using System.Globalization;
using CaptionLink.OverlapBenchmark;
using CaptionLink.OverlapTuner;

namespace CaptionLink.OverlapBenchmark.Tests;

internal static class Program
{
    private static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            (
                "RTTM parser reads speaker intervals",
                RttmParserReadsSpeakerIntervals),

            (
                "Reference builder finds concurrent speakers",
                ReferenceBuilderFindsConcurrentSpeakers),

            (
                "Strict scorer measures timeline errors",
                StrictScorerMeasuresTimelineErrors),

            (
                "Practical collar ignores boundary uncertainty",
                PracticalCollarIgnoresBoundaryUncertainty),

            (
                "Boundary matcher reports paired errors",
                BoundaryMatcherReportsPairedErrors),

            (
                "Percentile interpolates sorted values",
                PercentileInterpolatesSortedValues),

            (
                "Resume option is parsed",
                ResumeOptionIsParsed),

            (
                "Checkpoint round-trips and rejects incompatible options",
                CheckpointRoundTripsAndRejectsIncompatibleOptions),

            (
                "Probability tracker reproduces production hysteresis",
                ProbabilityTrackerReproducesProductionHysteresis),

            (
                "Probability trace round-trips",
                ProbabilityTraceRoundTrips),

            (
                "Tuning grid contains the production baseline",
                TuningGridContainsProductionBaseline),

            (
                "Validation mode rejects tuning-grid options",
                ValidationModeRejectsTuningGridOptions),

            (
                "Frozen validation candidate is parsed",
                FrozenValidationCandidateIsParsed),
        ];

        int failures = 0;

        foreach ((string name, Action body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine(
            $"Executed {tests.Length} benchmark tests; " +
            $"failures: {failures}.");

        return failures == 0 ? 0 : 1;
    }

    private static void RttmParserReadsSpeakerIntervals()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"captionlink-{Guid.NewGuid():N}.rttm");

        try
        {
            File.WriteAllText(
                path,
                "SPEAKER clip 1 1.250 0.500 <NA> <NA> speaker-a <NA> <NA>\n");

            IReadOnlyList<SpeakerInterval> intervals =
                RttmReader.Read(path);

            AssertEqual(1, intervals.Count, "Unexpected RTTM row count.");
            AssertEqual("clip", intervals[0].RecordingId, "Wrong recording ID.");
            AssertEqual("speaker-a", intervals[0].SpeakerId, "Wrong speaker ID.");
            AssertNear(1.25, intervals[0].StartSeconds, "Wrong start time.");
            AssertNear(1.75, intervals[0].EndSeconds, "Wrong end time.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void ReferenceBuilderFindsConcurrentSpeakers()
    {
        SpeakerInterval[] speakers =
        [
            new("clip", "a", 0, 2),
            new("clip", "b", 1, 1.5),
            new("clip", "b", 1.5, 2.5),
        ];

        IReadOnlyList<TimeInterval> overlap =
            ReferenceOverlapBuilder.Build(speakers);

        AssertEqual(1, overlap.Count, "Touching overlap segments were not merged.");
        AssertNear(1, overlap[0].StartSeconds, "Wrong overlap start.");
        AssertNear(2, overlap[0].EndSeconds, "Wrong overlap end.");
    }

    private static void StrictScorerMeasuresTimelineErrors()
    {
        TimeInterval[] reference =
        [
            new(1, 3),
        ];

        TimeInterval[] predicted =
        [
            new(2, 4),
        ];

        TimelineScore score =
            BinaryTimelineScorer.Score(
                5,
                reference,
                predicted);

        AssertNear(1, score.TruePositiveSeconds, "Wrong true-positive duration.");
        AssertNear(1, score.FalsePositiveSeconds, "Wrong false-positive duration.");
        AssertNear(1, score.FalseNegativeSeconds, "Wrong false-negative duration.");
        AssertNear(0.5, score.F1, "Wrong strict F1.");
    }

    private static void PracticalCollarIgnoresBoundaryUncertainty()
    {
        TimeInterval[] reference =
        [
            new(1, 2),
        ];

        TimeInterval[] predicted =
        [
            new(1.1, 1.9),
        ];

        TimelineScore score =
            BinaryTimelineScorer.Score(
                3,
                reference,
                predicted,
                collarSeconds: 0.2);

        AssertNear(1, score.Precision, "Collared precision should be perfect.");
        AssertNear(1, score.Recall, "Collared recall should be perfect.");
        AssertNear(0.6, score.TruePositiveSeconds, "Wrong collared overlap duration.");
    }

    private static void BoundaryMatcherReportsPairedErrors()
    {
        BoundaryMatchSummary summary =
            BinaryTimelineScorer.MatchBoundaries(
                [new TimeInterval(1, 2)],
                [new TimeInterval(1.1, 2.2)]);

        AssertEqual(1, summary.MatchedRegions, "Region was not matched.");
        AssertEqual(2, summary.BoundaryErrorsSeconds.Count, "Wrong error count.");
        AssertNear(0.1, summary.BoundaryErrorsSeconds[0], "Wrong start error.");
        AssertNear(0.2, summary.BoundaryErrorsSeconds[1], "Wrong end error.");
    }

    private static void PercentileInterpolatesSortedValues()
    {
        double? median =
            BenchmarkReportBuilder.Percentile(
                [1, 2, 3, 4],
                0.5);

        AssertNear(2.5, median ?? -1, "Wrong interpolated median.");
    }

    private static void ResumeOptionIsParsed()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse(
        [
            "--audio-dir",
            ".",
            "--rttm-dir",
            ".",
            "--trace-dir",
            "trace-output",
            "--resume",
        ]);

        AssertEqual(true, options.Resume, "Resume switch was not parsed.");
        AssertEqual(
            Path.GetFullPath("trace-output"),
            options.TraceDirectory ?? string.Empty,
            "Trace directory was not parsed.");
    }

    private static void
        ProbabilityTrackerReproducesProductionHysteresis()
    {
        var tracker = new ProbabilityActivityTracker(
            2,
            new ProbabilityActivityOptions(
                StartThreshold: 0.50,
                StopThreshold: 0.35));

        var batch = new CaptionLink.Diarization.Sortformer
            .SortformerProbabilityBatch(
                StartFrameIndex: 0,
                FrameDurationSeconds: 0.08,
                Probabilities:
                [
                    new float[] { 0.90f, 0.10f },
                    new float[] { 0.80f, 0.60f },
                    new float[] { 0.70f, 0.70f },
                    new float[] { 0.60f, 0.20f },
                    new float[] { 0.20f, 0.10f },
                ]);

        IReadOnlyList<CaptionLink.Core.Diarization.SpeakerActivity>
            updates = tracker.Consume(batch);

        AssertEqual(2, updates.Count, "Wrong final activity count.");
        AssertEqual(true, updates[0].IsFinal, "First activity was not final.");
        AssertEqual(true, updates[1].IsFinal, "Second activity was not final.");

        CaptionLink.Core.Diarization.SpeakerActivity speakerTwo =
            updates.Single(activity =>
                activity.SpeakerId == "speaker-2");

        AssertNear(
            0.08,
            speakerTwo.StartTime.TotalSeconds,
            "Wrong speaker-two start.");
        AssertNear(
            0.24,
            speakerTwo.EndTime.TotalSeconds,
            "Wrong speaker-two end.");
    }

    private static void ProbabilityTraceRoundTrips()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"captionlink-trace-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);
            string audioPath = Path.Combine(root, "clip.wav");
            File.WriteAllBytes(audioPath, [1, 2, 3, 4]);

            CaptionLink.Diarization.Sortformer.SortformerProbabilityBatch[]
                batches =
                [
                    new(
                        0,
                        0.08,
                        [
                            new float[]
                            {
                                0.9f,
                                0.2f,
                            },
                        ]),
                ];

            SortformerProbabilityTraceStore.WriteAsync(
                    root,
                    "clip",
                    audioPath,
                    0.08,
                    16_000,
                    2,
                    batches,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            string tracePath =
                SortformerProbabilityTraceStore.GetTracePath(
                    root,
                    "clip");

            SortformerProbabilityTrace trace =
                SortformerProbabilityTraceStore.LoadAsync(
                        tracePath,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            AssertEqual("clip", trace.Header.RecordingId, "Wrong trace ID.");
            AssertEqual(1, trace.Batches.Count, "Wrong trace batch count.");
            AssertNear(
                0.9,
                trace.Batches[0].Probabilities[0][0],
                "Wrong trace probability.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void TuningGridContainsProductionBaseline()
    {
        TunerOptions options = TunerOptions.Parse(
        [
            "--trace-dir",
            ".",
            "--rttm-dir",
            ".",
        ]);

        IReadOnlyList<TuningConfiguration> configurations =
            new ReplayTuner(options).BuildConfigurations();

        AssertEqual(
            648,
            configurations.Count,
            "Unexpected default tuning-grid size.");

        AssertEqual(
            true,
            configurations.Contains(
                TuningConfiguration.Baseline),
            "Production baseline is missing from the tuning grid.");

        AssertEqual(
            false,
            configurations.Any(configuration =>
                configuration.StopThreshold >
                    configuration.StartThreshold ||
                configuration.EndHysteresisMilliseconds <
                    configuration.MergeGapMilliseconds),
            "Tuning grid contains an invalid configuration.");
    }

    private static void
        ValidationModeRejectsTuningGridOptions()
    {
        TunerOptions options = TunerOptions.Parse(
        [
            "--trace-dir",
            ".",
            "--rttm-dir",
            ".",
            "--validate-candidate",
            "frozen.json",
        ]);

        AssertEqual(
            Path.GetFullPath("frozen.json"),
            options.ValidationCandidatePath ?? string.Empty,
            "Frozen candidate path was not parsed.");

        AssertThrows<ArgumentException>(() =>
            TunerOptions.Parse(
            [
                "--trace-dir",
                ".",
                "--rttm-dir",
                ".",
                "--validate-candidate",
                "frozen.json",
                "--start-thresholds",
                "0.50,0.51",
            ]));
    }

    private static void FrozenValidationCandidateIsParsed()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"captionlink-frozen-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "schema_version": 1,
                  "candidate_id": "candidate-a",
                  "selection_source": "development split",
                  "frozen_at_utc": "2026-08-08T00:00:00Z",
                  "configuration": {
                    "start_threshold": 0.51,
                    "stop_threshold": 0.37,
                    "consecutive_start_frames": 1,
                    "minimum_overlap_ms": 80,
                    "start_hysteresis_ms": 80,
                    "end_hysteresis_ms": 640,
                    "merge_gap_ms": 200
                  },
                  "selection_metrics": {
                    "source_rank": 19,
                    "evaluated_configurations": 4321,
                    "strict_precision": 0.718777,
                    "strict_recall": 0.688649,
                    "strict_f1": 0.703391,
                    "practical_f1": 0.726363,
                    "under_0_5s_recall": 0.622660,
                    "p95_finalization_latency_ms": 1200,
                    "false_overlap_seconds_per_hour": 33.793412,
                    "missed_overlap_seconds_per_hour": 39.050612
                  },
                  "acceptance": {
                    "minimum_strict_f1": 0.70,
                    "minimum_strict_precision": 0.68,
                    "minimum_strict_recall": 0.68,
                    "maximum_under_0_5s_recall_drop": 0.005,
                    "maximum_p95_finalization_latency_ms": 1360,
                    "require_strict_f1_improvement_over_baseline": true
                  }
                }
                """);

            FrozenValidationCandidate candidate =
                FrozenValidationCandidateStore.LoadAsync(
                        path,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            AssertEqual(
                "candidate-a",
                candidate.CandidateId,
                "Wrong frozen candidate ID.");

            AssertNear(
                0.51,
                candidate.Configuration.StartThreshold,
                "Wrong frozen start threshold.");

            AssertNear(
                0.70,
                candidate.Acceptance.MinimumStrictF1,
                "Wrong frozen strict-F1 floor.");

            AssertEqual(
                19,
                candidate.SelectionMetrics.SourceRank,
                "Wrong development-set source rank.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void
        CheckpointRoundTripsAndRejectsIncompatibleOptions()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"captionlink-benchmark-{Guid.NewGuid():N}");

        try
        {
            string audio = Path.Combine(root, "audio");
            string rttm = Path.Combine(root, "rttm");
            string output = Path.Combine(root, "output");

            var options = new BenchmarkOptions(
                audio,
                rttm,
                output,
                MaximumFiles: 3,
                IncludedRecordingIds:
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase),
                ChunkDuration: TimeSpan.FromMilliseconds(100),
                PracticalCollar: TimeSpan.FromMilliseconds(250),
                MaximumSpeakers: 4,
                Realtime: false,
                Resume: true,
                TraceDirectory: null);

            var store = new BenchmarkCheckpointStore(options);

            var snapshot = new BenchmarkRunSnapshot(
                SelectedRecordings: 3,
                SuccessfulResults: [],
                Failures:
                [
                    new RecordingBenchmarkFailure(
                        "clip-a",
                        "test failure",
                        DateTimeOffset.UtcNow),
                ]);

            store.SaveAsync(
                    snapshot,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            BenchmarkCheckpoint? checkpoint =
                store.LoadAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            AssertEqual(
                3,
                checkpoint?.SelectedRecordings ?? -1,
                "Checkpoint selected count did not round-trip.");

            AssertEqual(
                1,
                checkpoint?.Failures.Count ?? -1,
                "Checkpoint failures did not round-trip.");

            AssertEqual(
                true,
                File.Exists(store.ReportPath),
                "Incremental JSON report was not written.");

            AssertEqual(
                true,
                File.Exists(store.CsvPath),
                "Incremental CSV report was not written.");

            BenchmarkOptions incompatible =
                options with
                {
                    PracticalCollar =
                        TimeSpan.FromMilliseconds(500),
                };

            AssertThrows<InvalidOperationException>(() =>
                new BenchmarkCheckpointStore(incompatible)
                    .LoadAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssertEqual<T>(
        T expected,
        T actual,
        string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected {expected}; actual {actual}.");
        }
    }

    private static void AssertNear(
        double expected,
        double actual,
        string message)
    {
        if (Math.Abs(expected - actual) > 0.000001)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{message} Expected {expected}; actual {actual}."));
        }
    }

    private static void AssertThrows<TException>(
        Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name}.");
    }
}
