using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TransGo.Diarization.Sortformer;

namespace TransGo.OverlapBenchmark;

public sealed record SortformerProbabilityTraceHeader(
    [property: JsonPropertyName("schema_version")]
    int SchemaVersion,

    [property: JsonPropertyName("recording_id")]
    string RecordingId,

    [property: JsonPropertyName("dataset")]
    string Dataset,

    [property: JsonPropertyName("model")]
    string Model,

    [property: JsonPropertyName("streaming_profile")]
    string StreamingProfile,

    [property: JsonPropertyName("audio_sha256")]
    string AudioSha256,

    [property: JsonPropertyName("audio_duration_seconds")]
    double AudioDurationSeconds,

    [property: JsonPropertyName("audio_sample_rate")]
    int AudioSampleRate,

    [property: JsonPropertyName("maximum_speakers")]
    int MaximumSpeakers,

    [property: JsonPropertyName("frame_duration_seconds")]
    double FrameDurationSeconds,

    [property: JsonPropertyName("generated_at_utc")]
    DateTimeOffset GeneratedAtUtc);

public sealed record SortformerProbabilityTrace(
    [property: JsonPropertyName("header")]
    SortformerProbabilityTraceHeader Header,

    [property: JsonPropertyName("batches")]
    IReadOnlyList<SortformerProbabilityBatch> Batches);

public static class SortformerProbabilityTraceStore
{
    public const int CurrentSchemaVersion = 1;

    public const string ModelName =
        "nvidia/diar_streaming_sortformer_4spk-v2.1";

    public const string StreamingProfile =
        "chunk6-right7-fifo188-cache-period144-cache188";

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

    public static string GetTracePath(
        string traceDirectory,
        string recordingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);

        string safeRecordingId = Path.GetFileName(recordingId);

        if (!string.Equals(
                safeRecordingId,
                recordingId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The recording ID cannot contain a path.",
                nameof(recordingId));
        }

        return Path.Combine(
            traceDirectory,
            safeRecordingId + ".sortformer-trace.json.gz");
    }

    public static bool Exists(
        string traceDirectory,
        string recordingId)
    {
        string path = GetTracePath(
            traceDirectory,
            recordingId);

        return File.Exists(path) &&
            new FileInfo(path).Length > 0;
    }

    public static async Task WriteAsync(
        string traceDirectory,
        string recordingId,
        string audioPath,
        double audioDurationSeconds,
        int audioSampleRate,
        int maximumSpeakers,
        IReadOnlyList<SortformerProbabilityBatch> batches,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batches);

        ValidateBatches(
            batches,
            maximumSpeakers,
            out double frameDurationSeconds);

        Directory.CreateDirectory(traceDirectory);

        string audioHash =
            await CalculateSha256Async(
                audioPath,
                cancellationToken);

        var trace = new SortformerProbabilityTrace(
            new SortformerProbabilityTraceHeader(
                CurrentSchemaVersion,
                recordingId,
                "VoxConverse v0.3",
                ModelName,
                StreamingProfile,
                audioHash,
                audioDurationSeconds,
                audioSampleRate,
                maximumSpeakers,
                frameDurationSeconds,
                DateTimeOffset.UtcNow),
            batches);

        string path = GetTracePath(
            traceDirectory,
            recordingId);

        string temporaryPath =
            path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await using (
                FileStream file = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true))
            {
                await using var gzip = new GZipStream(
                    file,
                    CompressionLevel.SmallestSize,
                    leaveOpen: false);

                await JsonSerializer.SerializeAsync(
                    gzip,
                    trace,
                    JsonOptions,
                    cancellationToken);

                await gzip.FlushAsync(cancellationToken);
            }

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

    public static async Task<SortformerProbabilityTrace> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream file = File.OpenRead(path);
        await using var gzip = new GZipStream(
            file,
            CompressionMode.Decompress);

        SortformerProbabilityTrace trace =
            await JsonSerializer.DeserializeAsync<
                SortformerProbabilityTrace>(
                gzip,
                JsonOptions,
                cancellationToken) ??
            throw new InvalidDataException(
                $"Sortformer trace is empty: {path}");

        if (trace.Header.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Trace schema {trace.Header.SchemaVersion} is not " +
                $"supported: {path}");
        }

        ValidateBatches(
            trace.Batches,
            trace.Header.MaximumSpeakers,
            out double frameDurationSeconds);

        if (Math.Abs(
                frameDurationSeconds -
                trace.Header.FrameDurationSeconds) > 0.0000001)
        {
            throw new InvalidDataException(
                $"Trace frame duration does not match its header: {path}");
        }

        return trace;
    }

    private static void ValidateBatches(
        IReadOnlyList<SortformerProbabilityBatch> batches,
        int maximumSpeakers,
        out double frameDurationSeconds)
    {
        if (maximumSpeakers <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSpeakers));
        }

        if (batches.Count == 0)
        {
            throw new InvalidDataException(
                "A Sortformer probability trace cannot be empty.");
        }

        long expectedStart = 0;
        frameDurationSeconds = batches[0].FrameDurationSeconds;

        foreach (SortformerProbabilityBatch batch in batches)
        {
            if (batch.StartFrameIndex != expectedStart)
            {
                throw new InvalidDataException(
                    "Sortformer probability batches are not continuous.");
            }

            if (
                !double.IsFinite(batch.FrameDurationSeconds) ||
                batch.FrameDurationSeconds <= 0 ||
                Math.Abs(
                    batch.FrameDurationSeconds -
                    frameDurationSeconds) > 0.0000001)
            {
                throw new InvalidDataException(
                    "Sortformer probability frame durations are inconsistent.");
            }

            if (batch.FrameCount <= 0 ||
                batch.SpeakerCount != maximumSpeakers)
            {
                throw new InvalidDataException(
                    "A Sortformer probability batch has invalid dimensions.");
            }

            if (batch.Probabilities.Any(frame =>
                    frame.Length != maximumSpeakers ||
                    frame.Any(value =>
                        !float.IsFinite(value) ||
                        value < 0 ||
                        value > 1)))
            {
                throw new InvalidDataException(
                    "A Sortformer probability batch contains invalid values.");
            }

            expectedStart += batch.FrameCount;
        }
    }

    private static async Task<string> CalculateSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash =
            await SHA256.HashDataAsync(
                stream,
                cancellationToken);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
