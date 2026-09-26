using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CaptionLink.OverlapTuner;

public sealed class ValidationReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

    public ValidationReportWriter(string outputDirectory)
    {
        OutputDirectory = outputDirectory;
        ReportPath = Path.Combine(
            outputDirectory,
            "overlap-validation-results.json");
        CsvPath = Path.Combine(
            outputDirectory,
            "overlap-validation-comparison.csv");
    }

    public string OutputDirectory { get; }
    public string ReportPath { get; }
    public string CsvPath { get; }

    public async Task WriteAsync(
        ValidationReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(OutputDirectory);

        await WriteAtomicAsync(
            ReportPath,
            JsonSerializer.Serialize(report, JsonOptions),
            cancellationToken);

        await WriteAtomicAsync(
            CsvPath,
            BuildCsv(report),
            cancellationToken);
    }

    private static string BuildCsv(ValidationReport report)
    {
        var builder = new StringBuilder();

        builder.AppendLine(
            "configuration,candidate_id,start_threshold,stop_threshold," +
            "consecutive_start_frames,minimum_overlap_ms," +
            "start_hysteresis_ms,end_hysteresis_ms,merge_gap_ms," +
            "strict_precision,strict_recall,strict_f1," +
            "practical_precision,practical_recall,practical_f1," +
            "under_0_5s_recall,p95_finalization_latency_ms," +
            "false_overlap_seconds_per_hour,missed_overlap_seconds_per_hour," +
            "passes_acceptance");

        AppendRow(
            builder,
            "production_baseline",
            string.Empty,
            report.Baseline,
            passesAcceptance: null);

        AppendRow(
            builder,
            "frozen_candidate",
            report.FrozenCandidate.CandidateId,
            report.Candidate,
            report.Passed);

        return builder.ToString();
    }

    private static void AppendRow(
        StringBuilder builder,
        string label,
        string candidateId,
        TuningCandidateResult result,
        bool? passesAcceptance)
    {
        TuningConfiguration configuration =
            result.Configuration;

        builder.AppendLine(string.Join(
            ',',
            label,
            candidateId,
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
            Format(result.MissedOverlapSecondsPerHour),
            passesAcceptance?.ToString().ToLowerInvariant() ??
                string.Empty));
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
