using System.Text.Json.Serialization;

namespace CaptionLink.Diarization.Sortformer;

/// <summary>
/// Describes the local Sortformer service health response.
/// </summary>
public sealed record SortformerServiceHealth(
    [property: JsonPropertyName("status")]
        string? Status,

    [property: JsonPropertyName("model_loaded")]
        bool ModelLoaded,

    [property: JsonPropertyName("model")]
        string? Model,

    [property: JsonPropertyName("gpu")]
        string? Gpu,

    [property: JsonPropertyName("maximum_speakers")]
        int MaximumSpeakers)
{
    public bool IsReady =>
        ModelLoaded &&
        string.Equals(
            Status,
            "ready",
            StringComparison.OrdinalIgnoreCase);
}
