using System.Text.Json.Serialization;

namespace CaptionLink.Speech.Parakeet;

public sealed class ParakeetServiceHealth
{
    [JsonPropertyName("status")]
    public string Status { get; init; } =
        string.Empty;

    [JsonPropertyName("model_loaded")]
    public bool ModelLoaded { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; } =
        string.Empty;

    [JsonPropertyName("profile")]
    public string Profile { get; init; } =
    string.Empty;

    [JsonPropertyName("cuda_available")]
    public bool CudaAvailable { get; init; }

    [JsonPropertyName("gpu")]
    public string? Gpu { get; init; }

    [JsonPropertyName("streaming")]
    public bool Streaming { get; init; }

    [JsonPropertyName("model_sample_rate")]
    public int ModelSampleRate { get; init; }

    [JsonPropertyName("model_frame_duration_seconds")]
    public double ModelFrameDurationSeconds { get; init; }

    public bool IsReady =>
        string.Equals(
            Status,
            "ready",
            StringComparison.OrdinalIgnoreCase) &&
        ModelLoaded &&
        CudaAvailable &&
        Streaming;
}