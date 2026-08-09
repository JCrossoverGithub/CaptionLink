using System.Globalization;
using System.Text.Json.Serialization;
using TransGo.OverlapBenchmark;

namespace TransGo.OverlapTuner;

public sealed record TuningConfiguration(
    [property: JsonPropertyName("start_threshold")]
    double StartThreshold,

    [property: JsonPropertyName("stop_threshold")]
    double StopThreshold,

    [property: JsonPropertyName("consecutive_start_frames")]
    int ConsecutiveStartFrames,

    [property: JsonPropertyName("minimum_overlap_ms")]
    int MinimumOverlapMilliseconds,

    [property: JsonPropertyName("start_hysteresis_ms")]
    int StartHysteresisMilliseconds,

    [property: JsonPropertyName("end_hysteresis_ms")]
    int EndHysteresisMilliseconds,

    [property: JsonPropertyName("merge_gap_ms")]
    int MergeGapMilliseconds)
{
    public static TuningConfiguration Baseline { get; } =
        new(0.50, 0.35, 1, 160, 160, 720, 200);

    [JsonIgnore]
    public string Id => string.Format(
        CultureInfo.InvariantCulture,
        "start={0:0.00};stop={1:0.00};onset={2};minimum={3};" +
        "start_h={4};end_h={5};merge={6}",
        StartThreshold,
        StopThreshold,
        ConsecutiveStartFrames,
        MinimumOverlapMilliseconds,
        StartHysteresisMilliseconds,
        EndHysteresisMilliseconds,
        MergeGapMilliseconds);
}

public sealed record TuningCandidateResult(
    [property: JsonPropertyName("configuration")]
    TuningConfiguration Configuration,

    [property: JsonPropertyName("strict")]
    TimelineScore Strict,

    [property: JsonPropertyName("practical")]
    TimelineScore Practical,

    [property: JsonPropertyName("under_0_5s_regions")]
    int UnderHalfSecondRegions,

    [property: JsonPropertyName("under_0_5s_detected")]
    int UnderHalfSecondDetected,

    [property: JsonPropertyName("under_0_5s_recall")]
    double UnderHalfSecondRecall,

    [property: JsonPropertyName("p95_finalization_latency_ms")]
    double? P95FinalizationLatencyMilliseconds,

    [property: JsonPropertyName("false_overlap_seconds_per_hour")]
    double FalseOverlapSecondsPerHour,

    [property: JsonPropertyName("missed_overlap_seconds_per_hour")]
    double MissedOverlapSecondsPerHour,

    [property: JsonPropertyName("meets_guardrails")]
    bool MeetsGuardrails = false);

public sealed record TuningGuardrails(
    [property: JsonPropertyName("minimum_strict_precision")]
    double MinimumStrictPrecision,

    [property: JsonPropertyName("minimum_strict_recall")]
    double MinimumStrictRecall,

    [property: JsonPropertyName("minimum_under_0_5s_recall")]
    double MinimumUnderHalfSecondRecall,

    [property: JsonPropertyName("maximum_p95_finalization_latency_ms")]
    double MaximumP95FinalizationLatencyMilliseconds);

public sealed record TuningReport(
    [property: JsonPropertyName("generated_at_utc")]
    DateTimeOffset GeneratedAtUtc,

    [property: JsonPropertyName("recordings")]
    int Recordings,

    [property: JsonPropertyName("audio_hours")]
    double AudioHours,

    [property: JsonPropertyName("model")]
    string Model,

    [property: JsonPropertyName("streaming_profile")]
    string StreamingProfile,

    [property: JsonPropertyName("practical_collar_ms")]
    double PracticalCollarMilliseconds,

    [property: JsonPropertyName("guardrails")]
    TuningGuardrails Guardrails,

    [property: JsonPropertyName("baseline")]
    TuningCandidateResult Baseline,

    [property: JsonPropertyName("recommended")]
    TuningCandidateResult Recommended,

    [property: JsonPropertyName("candidates")]
    IReadOnlyList<TuningCandidateResult> Candidates);

public sealed record RecommendedOverlapSettings(
    [property: JsonPropertyName("source")]
    string Source,

    [property: JsonPropertyName("configuration")]
    TuningConfiguration Configuration,

    [property: JsonPropertyName("strict_f1")]
    double StrictF1,

    [property: JsonPropertyName("practical_f1")]
    double PracticalF1,

    [property: JsonPropertyName("requires_production_verification")]
    bool RequiresProductionVerification = true);

public sealed record ValidationAcceptanceCriteria(
    [property: JsonPropertyName("minimum_strict_f1")]
    double MinimumStrictF1,

    [property: JsonPropertyName("minimum_strict_precision")]
    double MinimumStrictPrecision,

    [property: JsonPropertyName("minimum_strict_recall")]
    double MinimumStrictRecall,

    [property: JsonPropertyName("maximum_under_0_5s_recall_drop")]
    double MaximumUnderHalfSecondRecallDrop,

    [property: JsonPropertyName("maximum_p95_finalization_latency_ms")]
    double MaximumP95FinalizationLatencyMilliseconds,

    [property: JsonPropertyName("require_strict_f1_improvement_over_baseline")]
    bool RequireStrictF1ImprovementOverBaseline);

public sealed record FrozenSelectionMetrics(
    [property: JsonPropertyName("source_rank")]
    int SourceRank,

    [property: JsonPropertyName("evaluated_configurations")]
    int EvaluatedConfigurations,

    [property: JsonPropertyName("strict_precision")]
    double StrictPrecision,

    [property: JsonPropertyName("strict_recall")]
    double StrictRecall,

    [property: JsonPropertyName("strict_f1")]
    double StrictF1,

    [property: JsonPropertyName("practical_f1")]
    double PracticalF1,

    [property: JsonPropertyName("under_0_5s_recall")]
    double UnderHalfSecondRecall,

    [property: JsonPropertyName("p95_finalization_latency_ms")]
    double P95FinalizationLatencyMilliseconds,

    [property: JsonPropertyName("false_overlap_seconds_per_hour")]
    double FalseOverlapSecondsPerHour,

    [property: JsonPropertyName("missed_overlap_seconds_per_hour")]
    double MissedOverlapSecondsPerHour);

public sealed record FrozenValidationCandidate(
    [property: JsonPropertyName("schema_version")]
    int SchemaVersion,

    [property: JsonPropertyName("candidate_id")]
    string CandidateId,

    [property: JsonPropertyName("selection_source")]
    string SelectionSource,

    [property: JsonPropertyName("frozen_at_utc")]
    DateTimeOffset FrozenAtUtc,

    [property: JsonPropertyName("configuration")]
    TuningConfiguration Configuration,

    [property: JsonPropertyName("selection_metrics")]
    FrozenSelectionMetrics SelectionMetrics,

    [property: JsonPropertyName("acceptance")]
    ValidationAcceptanceCriteria Acceptance);

public sealed record ValidationChecks(
    [property: JsonPropertyName("strict_f1_at_least_minimum")]
    bool StrictF1AtLeastMinimum,

    [property: JsonPropertyName("strict_precision_at_least_minimum")]
    bool StrictPrecisionAtLeastMinimum,

    [property: JsonPropertyName("strict_recall_at_least_minimum")]
    bool StrictRecallAtLeastMinimum,

    [property: JsonPropertyName("under_0_5s_recall_preserved")]
    bool UnderHalfSecondRecallPreserved,

    [property: JsonPropertyName("p95_finalization_latency_within_maximum")]
    bool P95FinalizationLatencyWithinMaximum,

    [property: JsonPropertyName("strict_f1_improves_over_baseline")]
    bool StrictF1ImprovesOverBaseline);

public sealed record ValidationMetricDeltas(
    [property: JsonPropertyName("strict_precision")]
    double StrictPrecision,

    [property: JsonPropertyName("strict_recall")]
    double StrictRecall,

    [property: JsonPropertyName("strict_f1")]
    double StrictF1,

    [property: JsonPropertyName("practical_f1")]
    double PracticalF1,

    [property: JsonPropertyName("under_0_5s_recall")]
    double UnderHalfSecondRecall,

    [property: JsonPropertyName("p95_finalization_latency_ms")]
    double? P95FinalizationLatencyMilliseconds,

    [property: JsonPropertyName("false_overlap_seconds_per_hour")]
    double FalseOverlapSecondsPerHour,

    [property: JsonPropertyName("missed_overlap_seconds_per_hour")]
    double MissedOverlapSecondsPerHour);

public sealed record ValidationReport(
    [property: JsonPropertyName("generated_at_utc")]
    DateTimeOffset GeneratedAtUtc,

    [property: JsonPropertyName("recordings")]
    int Recordings,

    [property: JsonPropertyName("audio_hours")]
    double AudioHours,

    [property: JsonPropertyName("model")]
    string Model,

    [property: JsonPropertyName("streaming_profile")]
    string StreamingProfile,

    [property: JsonPropertyName("practical_collar_ms")]
    double PracticalCollarMilliseconds,

    [property: JsonPropertyName("frozen_candidate")]
    FrozenValidationCandidate FrozenCandidate,

    [property: JsonPropertyName("baseline")]
    TuningCandidateResult Baseline,

    [property: JsonPropertyName("candidate")]
    TuningCandidateResult Candidate,

    [property: JsonPropertyName("candidate_minus_baseline")]
    ValidationMetricDeltas CandidateMinusBaseline,

    [property: JsonPropertyName("checks")]
    ValidationChecks Checks,

    [property: JsonPropertyName("passed")]
    bool Passed);
