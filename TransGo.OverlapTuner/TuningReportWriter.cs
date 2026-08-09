using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TransGo.OverlapTuner;

public sealed class TuningReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

    public TuningReportWriter(string outputDirectory)
    {
        OutputDirectory = outputDirectory;
        ReportPath = Path.Combine(
            outputDirectory,
            "overlap-tuning-results.json");
        CsvPath = Path.Combine(
            outputDirectory,
            "overlap-tuning-results.csv");
        RecommendedSettingsPath = Path.Combine(
            outputDirectory,
            "recommended-overlap-settings.json");
    }

    public string OutputDirectory { get; }
    public string ReportPath { get; }
    public string CsvPath { get; }
    public string RecommendedSettingsPath { get; }

    public async Task WriteAsync(
        TuningReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(OutputDirectory);

        await WriteAtomicAsync(
            ReportPath,
            JsonSerializer.Serialize(report, JsonOptions),
            cancellationToken);

        await WriteAtomicAsync(
            CsvPath,
            BuildCsv(report.Candidates),
            cancellationToken);

        var recommended = new RecommendedOverlapSettings(
            "VoxConverse development-set cached Sortformer replay",
            report.Recommended.Configuration,
            report.Recommended.Strict.F1,
            report.Recommended.Practical.F1);

        await WriteAtomicAsync(
            RecommendedSettingsPath,
            JsonSerializer.Serialize(recommended, JsonOptions),
            cancellationToken);
    }

    private static string BuildCsv(
        IEnumerable<TuningCandidateResult> results)
    {
        var builder = new StringBuilder();

        builder.AppendLine(
            "rank,meets_guardrails,start_threshold,stop_threshold," +
            "consecutive_start_frames,minimum_overlap_ms," +
            "start_hysteresis_ms,end_hysteresis_ms,merge_gap_ms," +
            "strict_precision,strict_recall,strict_f1," +
            "practical_precision,practical_recall,practical_f1," +
            "under_0_5s_recall,p95_finalization_latency_ms," +
            "false_overlap_seconds_per_hour,missed_overlap_seconds_per_hour");

        int rank = 0;

        foreach (TuningCandidateResult result in results)
        {
            TuningConfiguration configuration =
                result.Configuration;

            builder.AppendLine(string.Join(
                ',',
                (++rank).ToString(CultureInfo.InvariantCulture),
                result.MeetsGuardrails ? "true" : "false",
                Format(configuration.StartThreshold),
                Format(configuration.StopThreshold),
                configuration.ConsecutiveStartFrames.ToString(
                    CultureInfo.InvariantCulture),
                configuration.MinimumOverlapMilliseconds.ToString(
                    CultureInfo.InvariantCulture),
                configuration.StartHysteresisMilliseconds.ToString(
                    CultureInfo.InvariantCulture),
                configuration.EndHysteresisMilliseconds.ToString(
                    CultureInfo.InvariantCulture),
                configuration.MergeGapMilliseconds.ToString(
                    CultureInfo.InvariantCulture),
                Format(result.Strict.Precision),
                Format(result.Strict.Recall),
                Format(result.Strict.F1),
                Format(result.Practical.Precision),
                Format(result.Practical.Recall),
                Format(result.Practical.F1),
                Format(result.UnderHalfSecondRecall),
                result.P95FinalizationLatencyMilliseconds is double p95
                    ? Format(p95)
                    : string.Empty,
                Format(result.FalseOverlapSecondsPerHour),
                Format(result.MissedOverlapSecondsPerHour)));
        }

        return builder.ToString();
    }

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

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
                new UTF8Encoding(false),
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
}
