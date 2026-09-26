using System.Diagnostics;
using CaptionLink.Core.Audio;
using CaptionLink.Core.Diarization;
using CaptionLink.Diarization.Sortformer;
using CaptionLink.Diarization.Nemotron;

namespace CaptionLink.OverlapBenchmark;

public sealed class VoxConverseBenchmarkRunner
{
    private const int MaximumUnacknowledgedChunks = 20;

    private readonly BenchmarkOptions _options;

    private readonly Func<
        DiarizationBackend,
        IDiarizationEngine> _engineFactory;

    public VoxConverseBenchmarkRunner(
        BenchmarkOptions options,
        Func<
            DiarizationBackend,
            IDiarizationEngine> engineFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engineFactory);

        _options = options;
        _engineFactory = engineFactory;
    }

    public async Task<BenchmarkRunSnapshot>
        RunAsync(
            BenchmarkRunSnapshot initialState,
            Func<BenchmarkRunSnapshot, Task> saveProgressAsync,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(saveProgressAsync);

        RecordingInput[] inputs = ResolveInputs();

        Console.WriteLine(
            $"Benchmarking {inputs.Length} VoxConverse recordings.");

        var results = initialState.SuccessfulResults
            .ToDictionary(
                result => result.RecordingId,
                StringComparer.OrdinalIgnoreCase);

        var failures = initialState.Failures
            .ToDictionary(
                failure => failure.RecordingId,
                StringComparer.OrdinalIgnoreCase);

        if (results.Count > 0 || failures.Count > 0)
        {
            Console.WriteLine(
                $"Resuming with {results.Count} completed recording(s); " +
                $"{failures.Count} prior failure(s) will be retried.");
        }

        for (int index = 0; index < inputs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RecordingInput input = inputs[index];

            Console.WriteLine(
                $"[{index + 1}/{inputs.Length}] {input.RecordingId}");

            if (results.ContainsKey(input.RecordingId))
            {
                bool traceIsRequired =
                    _options.TraceDirectory is not null;

                bool traceExists =
                    traceIsRequired &&
                    SortformerProbabilityTraceStore.Exists(
                        _options.TraceDirectory!,
                        input.RecordingId);

                if (!traceIsRequired || traceExists)
                {
                    Console.WriteLine("  checkpoint found; skipped");
                    continue;
                }

                Console.WriteLine(
                    "  checkpoint found, but probability trace is missing; rerunning");

                results.Remove(input.RecordingId);
            }

            try
            {
                RecordingBenchmarkResult result =
                    await RunRecordingAsync(
                        input,
                        cancellationToken);

                results[result.RecordingId] = result;
                failures.Remove(result.RecordingId);

                Console.WriteLine(
                    $"  strict F1={result.Strict.F1:P2}; " +
                    $"practical F1={result.Practical.F1:P2}; " +
                    $"reference={result.ReferenceRegions}; " +
                    $"predicted={result.PredictedRegions}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures[input.RecordingId] =
                    new RecordingBenchmarkFailure(
                        input.RecordingId,
                        exception.Message,
                        DateTimeOffset.UtcNow);

                Console.Error.WriteLine(
                    $"  FAILED: {exception.Message}");
            }

            await saveProgressAsync(
                CreateSnapshot(
                    inputs.Length,
                    results.Values,
                    failures.Values));
        }

        return CreateSnapshot(
            inputs.Length,
            results.Values,
            failures.Values);
    }

    public BenchmarkRunSnapshot CreateInitialSnapshot(
        IReadOnlyList<RecordingBenchmarkResult> successfulResults,
        IReadOnlyList<RecordingBenchmarkFailure> failures)
    {
        RecordingInput[] inputs = ResolveInputs();

        var selectedIds = inputs
            .Select(input => input.RecordingId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? unknownRecordingId =
            successfulResults
                .Select(result => result.RecordingId)
                .Concat(failures.Select(
                    failure => failure.RecordingId))
                .FirstOrDefault(recordingId =>
                    !selectedIds.Contains(recordingId));

        if (unknownRecordingId is not null)
        {
            throw new InvalidDataException(
                $"Checkpoint recording '{unknownRecordingId}' is not " +
                "part of the current benchmark selection.");
        }

        return CreateSnapshot(
            inputs.Length,
            successfulResults,
            failures);
    }

    private static BenchmarkRunSnapshot CreateSnapshot(
        int selectedRecordings,
        IEnumerable<RecordingBenchmarkResult> results,
        IEnumerable<RecordingBenchmarkFailure> failures) =>
        new(
            selectedRecordings,
            results
                .OrderBy(
                    result => result.RecordingId,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            failures
                .OrderBy(
                    failure => failure.RecordingId,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray());

    private async Task<RecordingBenchmarkResult>
        RunRecordingAsync(
            RecordingInput input,
            CancellationToken cancellationToken)
    {
        using Pcm16WaveFile wave =
            Pcm16WaveFile.Open(input.AudioPath);

        IReadOnlyList<SpeakerInterval> speakerIntervals =
            RttmReader.Read(input.RttmPath);

        string[] recordingIds =
            speakerIntervals
                .Select(interval => interval.RecordingId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (
            recordingIds.Length != 1 ||
            !string.Equals(
                recordingIds[0],
                input.RecordingId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"RTTM recording ID does not match '{input.RecordingId}'.");
        }

        IReadOnlyList<TimeInterval> reference =
            ReferenceOverlapBuilder
                .Build(speakerIntervals)
                .Select(interval =>
                    new TimeInterval(
                        Math.Max(
                            0,
                            interval.StartSeconds),
                        Math.Min(
                            wave.DurationSeconds,
                            interval.EndSeconds)))
                .Where(interval =>
                    interval.DurationSeconds > 0)
                .ToArray();

        var detector =
            new SpeakerOverlapDetector();

        var ringBuffer =
            _options.Realtime
                ? new Pcm16AudioRingBuffer(
                    wave.SampleRate,
                    TimeSpan.FromSeconds(30))
                : null;

        var finalRegions =
            new List<OverlapRegion>();

        var finalSpeakerActivities =
            new Dictionary<string, SpeakerActivity>(
                StringComparer.Ordinal);

        object activityGate = new();

        var finalizationLatencies =
            new List<double>();

        var probabilityBatches =
            new List<SortformerProbabilityBatch>();

        object probabilityGate = new();

        int bufferRequests = 0;
        int bufferAvailable = 0;
        double observedActivityEnd = 0;

        detector.RegionUpdated += (_sender, eventArgs) =>
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

            if (ringBuffer is not null)
            {
                bufferRequests++;

                if (ringBuffer.TryRead(
                        eventArgs.Region.AudioStartTime,
                        eventArgs.Region.AudioEndTime,
                        out _))
                {
                    bufferAvailable++;
                }
            }
        };

        using var audioProgress =
            new AudioProgressGate();

        await using IDiarizationEngine engine =
            CreateDiarizationEngine();

        switch (engine)
        {
            case SortformerDiarizationEngine sortformer:
                sortformer.AudioProgressReceived +=
                    (_, eventArgs) =>
                        audioProgress.Observe(
                            eventArgs.ChunksReceived);

                if (_options.TraceDirectory is not null)
                {
                    sortformer.ProbabilityBatchReceived +=
                        (_, eventArgs) =>
                        {
                            lock (probabilityGate)
                            {
                                probabilityBatches.Add(
                                    eventArgs.Batch);
                            }
                        };
                }

                break;

            case NemotronDiarizationEngine nemotron:
                nemotron.AudioProgressReceived +=
                    (_, eventArgs) =>
                        audioProgress.Observe(
                            eventArgs.ChunksReceived);
                break;

            default:
                throw new InvalidOperationException(
                    "Unsupported diarization engine.");
        }

        engine.ActivityReceived += (_, eventArgs) =>
        {
            SpeakerActivity activity =
                eventArgs.Activity;

            observedActivityEnd = Math.Max(
                observedActivityEnd,
                activity.EndTime.TotalSeconds);

            if (activity.IsFinal)
            {
                lock (activityGate)
                {
                    finalSpeakerActivities[
                        activity.ActivityId
                    ] = activity;
                }
            }

            detector.Process(activity);
        };

        await engine.StartAsync(
            new DiarizationConfiguration(
                wave.SampleRate,
                _options.MaximumSpeakers,
                PublishSpeakerProbabilities:
                    _options.TraceDirectory is not null),
            cancellationToken);

        long sequence = 0;
        double audioPositionSeconds = 0;
        var realtimeClock = Stopwatch.StartNew();

        try
        {
            foreach (byte[] data in
                wave.ReadChunks(
                    _options.ChunkDuration))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk =
                    new TranscriptionAudioChunk(
                        Sequence: ++sequence,
                        CapturedAt:
                            DateTimeOffset.UnixEpoch +
                            TimeSpan.FromSeconds(
                                audioPositionSeconds),
                        Data: data,
                        SampleRate: wave.SampleRate)
                    {
                        SessionStartTime =
                            TimeSpan.FromSeconds(
                                audioPositionSeconds),
                    };

                ringBuffer?.Append(chunk);

                await engine.SendAsync(
                    chunk,
                    cancellationToken);

                if (
                    !_options.Realtime &&
                    sequence %
                        MaximumUnacknowledgedChunks == 0)
                {
                    await audioProgress.WaitForAsync(
                        sequence,
                        cancellationToken);
                }

                audioPositionSeconds +=
                    chunk.Duration.TotalSeconds;

                if (_options.Realtime)
                {
                    TimeSpan delay =
                        TimeSpan.FromSeconds(
                            audioPositionSeconds) -
                        realtimeClock.Elapsed;

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(
                            delay,
                            cancellationToken);
                    }
                }
            }
        }
        finally
        {
            await engine.StopAsync(
                CancellationToken.None);
        }

        detector.Complete(
            TimeSpan.FromSeconds(
                wave.DurationSeconds));

        if (
            _options.DiarizationRttmDirectory
            is not null)
        {
            SpeakerActivity[] capturedActivities;

            lock (activityGate)
            {
                capturedActivities =
                    finalSpeakerActivities
                        .Values
                        .ToArray();
            }

            await DiarizationRttmWriter.WriteAsync(
                _options.DiarizationRttmDirectory,
                input.RecordingId,
                wave.DurationSeconds,
                capturedActivities,
                cancellationToken);
        }

        if (_options.TraceDirectory is not null)
        {
            SortformerProbabilityBatch[] capturedBatches;

            lock (probabilityGate)
            {
                capturedBatches = probabilityBatches.ToArray();
            }

            await SortformerProbabilityTraceStore.WriteAsync(
                _options.TraceDirectory,
                input.RecordingId,
                input.AudioPath,
                wave.DurationSeconds,
                wave.SampleRate,
                _options.MaximumSpeakers,
                capturedBatches,
                cancellationToken);
        }

        IReadOnlyList<TimeInterval> predicted =
            finalRegions
                .Select(region =>
                    new TimeInterval(
                        Math.Max(
                            0,
                            region.StartTime.TotalSeconds),
                        Math.Min(
                            wave.DurationSeconds,
                            region.EndTime.TotalSeconds)))
                .Where(interval =>
                    interval.DurationSeconds > 0)
                .ToArray();

        TimelineScore strict =
            BinaryTimelineScorer.Score(
                wave.DurationSeconds,
                reference,
                predicted);

        TimelineScore practical =
            BinaryTimelineScorer.Score(
                wave.DurationSeconds,
                reference,
                predicted,
                _options.PracticalCollar.TotalSeconds);

        BoundaryMatchSummary boundaryMatches =
            BinaryTimelineScorer.MatchBoundaries(
                reference,
                predicted);

        return new RecordingBenchmarkResult(
            input.RecordingId,
            wave.DurationSeconds,
            reference.Count,
            predicted.Count,
            strict,
            practical,
            boundaryMatches.MatchedRegions,
            boundaryMatches.BoundaryErrorsSeconds,
            finalizationLatencies,
            _options.Realtime
                ? bufferRequests
                : null,
            _options.Realtime
                ? bufferAvailable
                : null,
            reference,
            predicted);
    }

    private IDiarizationEngine CreateDiarizationEngine()
    {
        return _engineFactory(
            _options.Engine);
    }

    private RecordingInput[] ResolveInputs()
    {
        if (!Directory.Exists(_options.AudioDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Audio directory not found: {_options.AudioDirectory}");
        }

        if (!Directory.Exists(_options.RttmDirectory))
        {
            throw new DirectoryNotFoundException(
                $"RTTM directory not found: {_options.RttmDirectory}");
        }

        IEnumerable<RecordingInput> inputs =
            Directory
                .EnumerateFiles(
                    _options.AudioDirectory,
                    "*.wav",
                    SearchOption.AllDirectories)
                .Select(audioPath =>
                {
                    string recordingId =
                        Path.GetFileNameWithoutExtension(
                            audioPath);

                    string rttmPath =
                        Path.Combine(
                            _options.RttmDirectory,
                            recordingId + ".rttm");

                    return new RecordingInput(
                        recordingId,
                        audioPath,
                        rttmPath);
                })
                .Where(input =>
                    _options.IncludedRecordingIds.Count == 0 ||
                    _options.IncludedRecordingIds.Contains(
                        input.RecordingId))
                .OrderBy(input =>
                    input.RecordingId,
                    StringComparer.OrdinalIgnoreCase);

        if (_options.MaximumFiles is int maximumFiles)
        {
            inputs = inputs.Take(maximumFiles);
        }

        RecordingInput[] resolved = inputs.ToArray();

        if (resolved.Length == 0)
        {
            throw new InvalidOperationException(
                "No WAV files matched the benchmark selection.");
        }

        string[] missingRttm =
            resolved
                .Where(input =>
                    !File.Exists(input.RttmPath))
                .Select(input =>
                    input.RttmPath)
                .ToArray();

        if (missingRttm.Length > 0)
        {
            throw new FileNotFoundException(
                "Missing matching RTTM files, including: " +
                missingRttm[0]);
        }

        return resolved;
    }

    private sealed record RecordingInput(
        string RecordingId,
        string AudioPath,
        string RttmPath);
}
