using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransGo.OverlapBenchmark;

public sealed record RecordingBenchmarkFailure(
    [property: JsonPropertyName("recording_id")]
    string RecordingId,

    [property: JsonPropertyName("error")]
    string Error,

    [property: JsonPropertyName("failed_at_utc")]
    DateTimeOffset FailedAtUtc);

public sealed record BenchmarkRunSnapshot(
    int SelectedRecordings,
    IReadOnlyList<RecordingBenchmarkResult> SuccessfulResults,
    IReadOnlyList<RecordingBenchmarkFailure> Failures);

public sealed record BenchmarkCheckpointConfiguration(
    string AudioDirectory,
    string RttmDirectory,
    int? MaximumFiles,
    IReadOnlyList<string> IncludedRecordingIds,
    double ChunkMilliseconds,
    double PracticalCollarMilliseconds,
    int MaximumSpeakers,
    bool Realtime)
{
    public static BenchmarkCheckpointConfiguration FromOptions(
        BenchmarkOptions options) =>
        new(
            options.AudioDirectory,
            options.RttmDirectory,
            options.MaximumFiles,
            options.IncludedRecordingIds
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            options.ChunkDuration.TotalMilliseconds,
            options.PracticalCollar.TotalMilliseconds,
            options.MaximumSpeakers,
            options.Realtime);

    public bool IsCompatibleWith(
        BenchmarkCheckpointConfiguration other) =>
        string.Equals(
            AudioDirectory,
            other.AudioDirectory,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            RttmDirectory,
            other.RttmDirectory,
            StringComparison.OrdinalIgnoreCase) &&
        MaximumFiles == other.MaximumFiles &&
        IncludedRecordingIds.SequenceEqual(
            other.IncludedRecordingIds,
            StringComparer.OrdinalIgnoreCase) &&
        ChunkMilliseconds == other.ChunkMilliseconds &&
        PracticalCollarMilliseconds ==
            other.PracticalCollarMilliseconds &&
        MaximumSpeakers == other.MaximumSpeakers &&
        Realtime == other.Realtime;
}

public sealed record BenchmarkCheckpoint(
    [property: JsonPropertyName("schema_version")]
    int SchemaVersion,

    [property: JsonPropertyName("updated_at_utc")]
    DateTimeOffset UpdatedAtUtc,

    [property: JsonPropertyName("configuration")]
    BenchmarkCheckpointConfiguration Configuration,

    [property: JsonPropertyName("selected_recordings")]
    int SelectedRecordings,

    [property: JsonPropertyName("successful_results")]
    IReadOnlyList<RecordingBenchmarkResult> SuccessfulResults,

    [property: JsonPropertyName("failures")]
    IReadOnlyList<RecordingBenchmarkFailure> Failures);

public sealed class BenchmarkCheckpointStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy =
                JsonNamingPolicy.SnakeCaseLower,
        };

    private readonly BenchmarkOptions _options;
    private readonly BenchmarkCheckpointConfiguration _configuration;

    public BenchmarkCheckpointStore(
        BenchmarkOptions options)
    {
        _options = options;
        _configuration =
            BenchmarkCheckpointConfiguration.FromOptions(options);

        CheckpointPath = Path.Combine(
            options.OutputDirectory,
            "overlap-benchmark.checkpoint.json");

        ReportPath = Path.Combine(
            options.OutputDirectory,
            "overlap-benchmark.json");

        CsvPath = Path.Combine(
            options.OutputDirectory,
            "overlap-benchmark.csv");

        FailuresPath = Path.Combine(
            options.OutputDirectory,
            "overlap-benchmark-failures.csv");
    }

    public string CheckpointPath { get; }

    public string ReportPath { get; }

    public string CsvPath { get; }

    public string FailuresPath { get; }

    public async Task<BenchmarkCheckpoint?> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(CheckpointPath))
        {
            Console.WriteLine(
                "No checkpoint was found; starting a new benchmark run.");
            return null;
        }

        await using FileStream stream = File.OpenRead(CheckpointPath);

        BenchmarkCheckpoint checkpoint =
            await JsonSerializer.DeserializeAsync<BenchmarkCheckpoint>(
                stream,
                JsonOptions,
                cancellationToken) ??
            throw new InvalidDataException(
                $"Checkpoint is empty or invalid: {CheckpointPath}");

        if (checkpoint.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Checkpoint schema {checkpoint.SchemaVersion} is not supported. " +
                $"Expected {CurrentSchemaVersion}.");
        }

        if (!checkpoint.Configuration.IsCompatibleWith(_configuration))
        {
            throw new InvalidOperationException(
                "The checkpoint was created with different benchmark options. " +
                "Use the original command or choose a different output directory.");
        }

        return checkpoint;
    }

    public async Task SaveAsync(
        BenchmarkRunSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(_options.OutputDirectory);

        AggregateBenchmarkReport report =
            BenchmarkReportBuilder.Build(
                snapshot.SuccessfulResults,
                _options,
                snapshot.SelectedRecordings,
                snapshot.Failures);

        var checkpoint = new BenchmarkCheckpoint(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            _configuration,
            snapshot.SelectedRecordings,
            snapshot.SuccessfulResults,
            snapshot.Failures);

        await WriteAtomicAsync(
            ReportPath,
            JsonSerializer.Serialize(report, JsonOptions),
            cancellationToken);

        await WriteAtomicAsync(
            CsvPath,
            BuildResultsCsv(snapshot.SuccessfulResults),
            cancellationToken);

        await WriteAtomicAsync(
            FailuresPath,
            BuildFailuresCsv(snapshot.Failures),
            cancellationToken);

        // Write the checkpoint last. If the process stops between files,
        // resume will never skip work whose reports were not persisted.
        await WriteAtomicAsync(
            CheckpointPath,
            JsonSerializer.Serialize(checkpoint, JsonOptions),
            cancellationToken);
    }

    private static async Task WriteAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        string temporaryPath =
            path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            File.Move(
                temporaryPath,
                path,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string BuildResultsCsv(
        IEnumerable<RecordingBenchmarkResult> results)
    {
        var builder = new StringBuilder();

        builder.AppendLine(
            "recording_id,duration_seconds,reference_regions,predicted_regions," +
            "strict_precision,strict_recall,strict_f1," +
            "practical_precision,practical_recall,practical_f1," +
            "matched_regions,median_boundary_error_ms,p95_boundary_error_ms," +
            "median_finalization_latency_ms,p95_finalization_latency_ms," +
            "buffer_windows_requested,buffer_windows_available");

        foreach (RecordingBenchmarkResult result in results)
        {
            double[] boundaryErrors =
                result.BoundaryErrorsSeconds.Order().ToArray();

            double[] latencies =
                result.FinalizationLatenciesSeconds.Order().ToArray();

            string[] values =
            [
                EscapeCsv(result.RecordingId),
                Format(result.DurationSeconds),
                result.ReferenceRegions.ToString(
                    CultureInfo.InvariantCulture),
                result.PredictedRegions.ToString(
                    CultureInfo.InvariantCulture),
                Format(result.Strict.Precision),
                Format(result.Strict.Recall),
                Format(result.Strict.F1),
                Format(result.Practical.Precision),
                Format(result.Practical.Recall),
                Format(result.Practical.F1),
                result.MatchedRegions.ToString(
                    CultureInfo.InvariantCulture),
                FormatNullable(
                    BenchmarkReportBuilder.Percentile(
                        boundaryErrors,
                        0.5) * 1000),
                FormatNullable(
                    BenchmarkReportBuilder.Percentile(
                        boundaryErrors,
                        0.95) * 1000),
                FormatNullable(
                    BenchmarkReportBuilder.Percentile(
                        latencies,
                        0.5) * 1000),
                FormatNullable(
                    BenchmarkReportBuilder.Percentile(
                        latencies,
                        0.95) * 1000),
                result.BufferWindowsRequested?.ToString(
                    CultureInfo.InvariantCulture) ?? "",
                result.BufferWindowsAvailable?.ToString(
                    CultureInfo.InvariantCulture) ?? "",
            ];

            builder.AppendLine(string.Join(',', values));
        }

        return builder.ToString();
    }

    private static string BuildFailuresCsv(
        IEnumerable<RecordingBenchmarkFailure> failures)
    {
        var builder = new StringBuilder();
        builder.AppendLine("recording_id,failed_at_utc,error");

        foreach (RecordingBenchmarkFailure failure in failures)
        {
            builder.AppendLine(
                string.Join(
                    ',',
                    EscapeCsv(failure.RecordingId),
                    EscapeCsv(
                        failure.FailedAtUtc.ToString(
                            "O",
                            CultureInfo.InvariantCulture)),
                    EscapeCsv(failure.Error)));
        }

        return builder.ToString();
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private static string Format(double value) =>
        value.ToString(
            "0.######",
            CultureInfo.InvariantCulture);

    private static string FormatNullable(double? value) =>
        value is double number
            ? Format(number)
            : "";
}
