using System.Diagnostics;
using System.Text.Json;
using TransGo.Core.Audio;
using TransGo.Core.Transcription;

namespace TransGo.Speech.Parakeet;

/// <summary>
/// Sends normalized TransGo audio to the persistent local
/// Parakeet service.
/// </summary>
public sealed class ParakeetStreamingTranscriptionEngine
    : ITranscriptionEngine
{
    private readonly object _gate = new();

    private readonly IParakeetServiceLauncher
    _serviceLauncher;

    private ParakeetServiceClient? _client;
    private int? _configuredSampleRate;

    private readonly ParakeetStreamingProfile
    _streamingProfile;

    private bool _isStarting;
    private bool _isRunning;
    private bool _disposed;

    public event EventHandler<TranscriptResultEventArgs>?
        ResultReceived;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _isRunning;
            }
        }
    }

    public ParakeetStreamingTranscriptionEngine(
        ParakeetStreamingProfile streamingProfile,
        IParakeetServiceLauncher serviceLauncher)
    {
        ArgumentNullException.ThrowIfNull(
            serviceLauncher);

        _streamingProfile = streamingProfile;
        _serviceLauncher = serviceLauncher;
    }

    public async Task StartAsync(
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ValidateConfiguration(configuration);

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (_isStarting || _isRunning)
            {
                throw new InvalidOperationException(
                    "Parakeet transcription is already running.");
            }

            _isStarting = true;
        }

        var client =
            new ParakeetServiceClient();

        client.MessageReceived +=
            Client_MessageReceived;

        try
        {
            ParakeetServiceHealth health =
                await _serviceLauncher.EnsureReadyAsync(
                    _streamingProfile,
                    cancellationToken);

            Debug.WriteLine(
                "Local Parakeet service ready. " +
                $"Profile: {health.Profile}. " +
                $"GPU: {health.Gpu}");

            await client.ConnectAsync(
                configuration.SampleRate,
                cancellationToken);

            lock (_gate)
            {
                ThrowIfDisposedLocked();

                _client = client;
                _configuredSampleRate =
                    configuration.SampleRate;

                _isRunning = true;
            }
        }
        catch
        {
            client.MessageReceived -=
                Client_MessageReceived;

            await client.DisposeAsync();

            throw;
        }
        finally
        {
            lock (_gate)
            {
                _isStarting = false;
            }
        }
    }

    public async ValueTask SendAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        ParakeetServiceClient client;
        int configuredSampleRate;

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (
                !_isRunning ||
                _client is null ||
                _configuredSampleRate is null)
            {
                throw new InvalidOperationException(
                    "Parakeet transcription is not running.");
            }

            client = _client;
            configuredSampleRate =
                _configuredSampleRate.Value;
        }

        ValidateChunk(
            chunk,
            configuredSampleRate);

        await client.SendAudioAsync(
            chunk,
            cancellationToken);
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        ParakeetServiceClient? client;

        lock (_gate)
        {
            client = _client;

            if (client is null)
            {
                return;
            }

            /*
             * Prevent new audio from being sent while the
             * WebSocket session is shutting down.
             */
            _isRunning = false;
            _client = null;
            _configuredSampleRate = null;
        }

        try
        {
            await client.StopAsync(
                cancellationToken);
        }
        finally
        {
            client.MessageReceived -=
                Client_MessageReceived;

            await client.DisposeAsync();
        }
    }

    private void Client_MessageReceived(
        object? sender,
        ParakeetServiceMessageEventArgs eventArgs)
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(
                    eventArgs.Json);

            JsonElement root =
                document.RootElement;

            string messageType =
                root.TryGetProperty(
                    "type",
                    out JsonElement typeElement)
                    ? typeElement.GetString() ??
                      string.Empty
                    : string.Empty;

            switch (messageType)
            {
                case "connected":
                    Debug.WriteLine(
                        "Connected to the local Parakeet service.");
                    break;

                case "started":
                    Debug.WriteLine(
                        "The Parakeet audio session started.");
                    break;

                case "audio_received":
                    LogAudioProgress(root);
                    break;

                case "stopped":
                    LogAudioProgress(root);

                    Debug.WriteLine(
                        "The Parakeet audio session stopped.");
                    break;

                case "error":
                    string errorMessage =
                        root.TryGetProperty(
                            "message",
                            out JsonElement messageElement)
                            ? messageElement.GetString() ??
                              "Unknown Parakeet service error."
                            : "Unknown Parakeet service error.";

                    Debug.WriteLine(
                        $"Parakeet service error: {errorMessage}");
                    break;

                case "transcript":
                    /*
                     * The Python service does not emit transcript
                     * messages yet. We will implement this path
                     * when streaming inference is added.
                     */
                    ProcessTranscriptMessage(root);
                    break;

                default:
                    Debug.WriteLine(
                        $"Parakeet service message: {eventArgs.Json}");
                    break;
            }
        }
        catch (JsonException exception)
        {
            Debug.WriteLine(
                "Invalid Parakeet service JSON: " +
                exception);
        }
    }

    private static void LogAudioProgress(
        JsonElement root)
    {
        int chunksReceived =
            root.TryGetProperty(
                "chunks_received",
                out JsonElement chunksElement)
                ? chunksElement.GetInt32()
                : 0;

        long bytesReceived =
            root.TryGetProperty(
                "bytes_received",
                out JsonElement bytesElement)
                ? bytesElement.GetInt64()
                : 0;

        double durationSeconds =
            root.TryGetProperty(
                "audio_duration_seconds",
                out JsonElement durationElement)
                ? durationElement.GetDouble()
                : 0;

        Debug.WriteLine(
            "Parakeet received " +
            $"{chunksReceived} chunks, " +
            $"{bytesReceived} bytes, " +
            $"{durationSeconds:0.000} seconds of audio.");
    }

    private void ProcessTranscriptMessage(
        JsonElement root)
    {
        string text =
            root.TryGetProperty(
                "text",
                out JsonElement textElement)
                ? textElement.GetString() ??
                  string.Empty
                : string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        bool isFinal =
            root.TryGetProperty(
                "is_final",
                out JsonElement finalElement) &&
            finalElement.GetBoolean();

        string segmentId =
            root.TryGetProperty(
                "segment_id",
                out JsonElement segmentElement)
                ? segmentElement.GetString() ??
                  "parakeet-current"
                : "parakeet-current";

        long sequence =
            root.TryGetProperty(
                "sequence",
                out JsonElement sequenceElement)
                ? sequenceElement.GetInt64()
                : 0;

        TimeSpan? resultStartTime =
            TryReadTimeSpanSeconds(
                root,
                "result_start_time_seconds");

        TimeSpan? resultEndTime =
            TryReadTimeSpanSeconds(
                root,
                "result_end_time_seconds");

        double? processingMilliseconds =
            TryReadNonNegativeDouble(
                root,
                "processing_duration_milliseconds");

        var result =
            new TranscriptResult(
                SegmentId: segmentId,
                Sequence: sequence,
                Text: text.Trim(),
                IsFinal: isFinal,
                Stability: null,
                ResultEndTime: resultEndTime)
            {
                ResultStartTime =
                    resultStartTime,
                ProviderProcessingMilliseconds =
                    processingMilliseconds,
            };

        ResultReceived?.Invoke(
            this,
            new TranscriptResultEventArgs(result));
    }

    private static TimeSpan?
    TryReadTimeSpanSeconds(
        JsonElement root,
        string propertyName)
    {
        if (
            !root.TryGetProperty(
                propertyName,
                out JsonElement valueElement)
            ||
            valueElement.ValueKind !=
                JsonValueKind.Number
            ||
            !valueElement.TryGetDouble(
                out double seconds)
            ||
            !double.IsFinite(seconds)
            ||
            seconds < 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(
            seconds);
    }

    private static double? TryReadNonNegativeDouble(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(
                propertyName,
                out JsonElement valueElement) ||
            valueElement.ValueKind != JsonValueKind.Number ||
            !valueElement.TryGetDouble(out double value) ||
            !double.IsFinite(value) ||
            value < 0)
        {
            return null;
        }

        return value;
    }

    private static void ValidateConfiguration(
        TranscriptionConfiguration configuration)
    {
        if (configuration.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "The audio sample rate must be positive.");
        }

        if (
            !configuration.LanguageCode.StartsWith(
                "en",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "The installed Parakeet model currently " +
                "supports English transcription.");
        }
    }

    private static void ValidateChunk(
        TranscriptionAudioChunk chunk,
        int configuredSampleRate)
    {
        if (chunk.SampleRate != configuredSampleRate)
        {
            throw new ArgumentException(
                "The chunk sample rate does not match the " +
                "active Parakeet session.",
                nameof(chunk));
        }

        if (
            chunk.Channels != 1 ||
            chunk.BitsPerSample != 16 ||
            chunk.Encoding !=
                AudioSampleEncoding.PcmInteger)
        {
            throw new ArgumentException(
                "Parakeet requires mono PCM16 audio.",
                nameof(chunk));
        }

        if (
            chunk.Data.IsEmpty ||
            chunk.Data.Length % sizeof(short) != 0)
        {
            throw new ArgumentException(
                "The audio chunk must contain complete " +
                "PCM16 samples.",
                nameof(chunk));
        }
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            await StopAsync();
        }
        finally
        {
            await _serviceLauncher.DisposeAsync();

            lock (_gate)
            {
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }
    }
}
