using System.Text.Json;

namespace TransGo.OverlapTuner;

public static class FrozenValidationCandidateStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

    public static async Task<FrozenValidationCandidate> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Frozen validation candidate not found: {path}",
                path);
        }

        await using FileStream stream = File.OpenRead(path);

        FrozenValidationCandidate candidate =
            await JsonSerializer.DeserializeAsync<FrozenValidationCandidate>(
                stream,
                JsonOptions,
                cancellationToken) ??
            throw new InvalidDataException(
                "The frozen validation candidate is empty or invalid.");

        Validate(candidate);
        return candidate;
    }

    private static void Validate(FrozenValidationCandidate candidate)
    {
        if (candidate.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported frozen-candidate schema version " +
                $"'{candidate.SchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(candidate.CandidateId) ||
            string.IsNullOrWhiteSpace(candidate.SelectionSource) ||
            candidate.FrozenAtUtc == default)
        {
            throw new InvalidDataException(
                "The frozen candidate must identify its ID, selection source, " +
                "and freeze timestamp.");
        }

        if (candidate.Configuration is null ||
            candidate.SelectionMetrics is null ||
            candidate.Acceptance is null)
        {
            throw new InvalidDataException(
                "The frozen candidate must contain a configuration, " +
                "selection metrics, and acceptance criteria.");
        }

        TuningConfiguration configuration = candidate.Configuration;

        if (!double.IsFinite(configuration.StartThreshold) ||
            !double.IsFinite(configuration.StopThreshold) ||
            configuration.StartThreshold < 0 ||
            configuration.StartThreshold > 1 ||
            configuration.StopThreshold < 0 ||
            configuration.StopThreshold >
                configuration.StartThreshold ||
            configuration.ConsecutiveStartFrames <= 0 ||
            configuration.MinimumOverlapMilliseconds < 0 ||
            configuration.StartHysteresisMilliseconds < 0 ||
            configuration.EndHysteresisMilliseconds < 0 ||
            configuration.MergeGapMilliseconds < 0 ||
            configuration.EndHysteresisMilliseconds <
                configuration.MergeGapMilliseconds)
        {
            throw new InvalidDataException(
                "The frozen candidate contains an invalid detector configuration.");
        }

        if (configuration == TuningConfiguration.Baseline)
        {
            throw new InvalidDataException(
                "The frozen candidate must differ from the production baseline.");
        }

        FrozenSelectionMetrics metrics =
            candidate.SelectionMetrics;

        if (metrics.SourceRank <= 0 ||
            metrics.EvaluatedConfigurations <= 0 ||
            !IsProbability(metrics.StrictPrecision) ||
            !IsProbability(metrics.StrictRecall) ||
            !IsProbability(metrics.StrictF1) ||
            !IsProbability(metrics.PracticalF1) ||
            !IsProbability(metrics.UnderHalfSecondRecall) ||
            !double.IsFinite(
                metrics.P95FinalizationLatencyMilliseconds) ||
            metrics.P95FinalizationLatencyMilliseconds < 0 ||
            !double.IsFinite(metrics.FalseOverlapSecondsPerHour) ||
            metrics.FalseOverlapSecondsPerHour < 0 ||
            !double.IsFinite(metrics.MissedOverlapSecondsPerHour) ||
            metrics.MissedOverlapSecondsPerHour < 0)
        {
            throw new InvalidDataException(
                "The frozen candidate contains invalid selection metrics.");
        }

        ValidationAcceptanceCriteria acceptance = candidate.Acceptance;

        if (!IsProbability(acceptance.MinimumStrictF1) ||
            !IsProbability(acceptance.MinimumStrictPrecision) ||
            !IsProbability(acceptance.MinimumStrictRecall) ||
            !IsProbability(acceptance.MaximumUnderHalfSecondRecallDrop) ||
            !double.IsFinite(
                acceptance.MaximumP95FinalizationLatencyMilliseconds) ||
            acceptance.MaximumP95FinalizationLatencyMilliseconds < 0)
        {
            throw new InvalidDataException(
                "The frozen candidate contains invalid acceptance criteria.");
        }
    }

    private static bool IsProbability(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 1;
}
