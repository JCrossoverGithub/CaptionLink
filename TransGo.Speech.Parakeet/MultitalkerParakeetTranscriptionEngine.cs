using System.Diagnostics;
using System.Text.Json;
using TransGo.Core.Audio;
using TransGo.Core.Diarization;
using TransGo.Core.Transcription;

namespace TransGo.Speech.Parakeet;

public sealed class MultitalkerParakeetTranscriptionEngine
    : ITranscriptionEngine,
      ISpeakerActivitySource
{
    private static readonly Uri ServiceUri =
        new("ws://localhost:8768/stream");

    private readonly object _gate = new();

    private readonly IMultitalkerParakeetServiceLauncher
        _serviceLauncher;

    private ParakeetServiceClient? _client;
    private int? _configuredSampleRate;

    private bool _isStarting;
    private bool _isRunning;
    private bool _disposed;

    public MultitalkerParakeetTranscriptionEngine()
        : this(
            new MultitalkerParakeetServiceLauncher())
    {
    }

    public MultitalkerParakeetTranscriptionEngine(
        IMultitalkerParakeetServiceLauncher serviceLauncher)
    {
        ArgumentNullException.ThrowIfNull(
            serviceLauncher);

        _serviceLauncher =
            serviceLauncher;
    }

    public event EventHandler<TranscriptResultEventArgs>?
        ResultReceived;

    public event EventHandler<SpeakerActivityEventArgs>?
        ActivityReceived;

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

    public async Task StartAsync(
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(
            configuration);

        ValidateConfiguration(
            configuration);

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (_isStarting || _isRunning)
            {
                throw new InvalidOperationException(
                    "Multitalker transcription is " +
                    "already running.");
            }

            _isStarting = true;
        }

        var client =
            new ParakeetServiceClient(
                ServiceUri);

        client.MessageReceived +=
            Client_MessageReceived;

        try
        {
            await _serviceLauncher
                .EnsureReadyAsync(
                    cancellationToken);

            Debug.WriteLine(
                "Local Multitalker Parakeet " +
                "service ready.");

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
        ArgumentNullException.ThrowIfNull(
            chunk);

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
                    "Multitalker transcription " +
                    "is not running.");
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
                    ? typeElement.GetString()
                      ?? string.Empty
                    : string.Empty;

            switch (messageType)
            {
                case "connected":
                    Debug.WriteLine(
                        "Connected to Multitalker service.");
                    break;

                case "started":
                    Debug.WriteLine(
                        "Multitalker session started.");
                    break;

                case "audio_received":
                    break;

                case "stopped":
                    Debug.WriteLine(
                        "Multitalker session stopped.");
                    break;

                case "error":
                    string errorMessage =
                        root.TryGetProperty(
                            "message",
                            out JsonElement messageElement)
                            ? messageElement.GetString()
                              ?? "Unknown Multitalker error."
                            : "Unknown Multitalker error.";

                    Debug.WriteLine(
                        "Multitalker service error: " +
                        errorMessage);
                    break;

                case "speaker_activity":
                    ProcessSpeakerActivityMessage(
                        root);
                    break;

                case "transcript":
                    ProcessTranscriptMessage(
                        root);
                    break;

                default:
                    Debug.WriteLine(
                        "Multitalker service message: " +
                        eventArgs.Json);
                    break;
            }
        }
        catch (JsonException exception)
        {
            Debug.WriteLine(
                "Invalid Multitalker service JSON: " +
                exception);
        }
    }

    private void ProcessSpeakerActivityMessage(
        JsonElement root)
    {
        string activityId =
            root.TryGetProperty(
                "activity_id",
                out JsonElement activityElement)
                ? activityElement.GetString()
                  ?? string.Empty
                : string.Empty;

        string speakerId =
            root.TryGetProperty(
                "speaker_id",
                out JsonElement speakerElement)
                ? speakerElement.GetString()
                  ?? string.Empty
                : string.Empty;

        if (
            string.IsNullOrWhiteSpace(
                activityId)
            ||
            string.IsNullOrWhiteSpace(
                speakerId))
        {
            return;
        }

        long sequence =
            root.TryGetProperty(
                "sequence",
                out JsonElement sequenceElement)
            &&
            sequenceElement.TryGetInt64(
                out long parsedSequence)
                ? parsedSequence
                : 0;

        TimeSpan? startTime =
            TryReadTimeSpanSeconds(
                root,
                "start_time_seconds");

        TimeSpan? endTime =
            TryReadTimeSpanSeconds(
                root,
                "end_time_seconds");

        if (
            startTime is null
            || endTime is null
            || endTime < startTime)
        {
            return;
        }

        bool isFinal =
            root.TryGetProperty(
                "is_final",
                out JsonElement finalElement)
            &&
            (
                finalElement.ValueKind ==
                    JsonValueKind.True
                ||
                finalElement.ValueKind ==
                    JsonValueKind.False
            )
            &&
            finalElement.GetBoolean();

        double? confidence =
            TryReadNonNegativeDouble(
                root,
                "confidence");

        if (confidence is > 1.0)
        {
            confidence = null;
        }

        var activity =
            new SpeakerActivity(
                ActivityId: activityId,
                Sequence: sequence,
                SpeakerId: speakerId,
                StartTime: startTime.Value,
                EndTime: endTime.Value,
                IsFinal: isFinal,
                Confidence: confidence);

        ActivityReceived?.Invoke(
            this,
            new SpeakerActivityEventArgs(
                activity));
    }

    private void ProcessTranscriptMessage(
        JsonElement root)
    {
        string text =
            root.TryGetProperty(
                "text",
                out JsonElement textElement)
                ? textElement.GetString()
                  ?? string.Empty
                : string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        bool isFinal =
            root.TryGetProperty(
                "is_final",
                out JsonElement finalElement)
            &&
            finalElement.GetBoolean();

        string segmentId =
            root.TryGetProperty(
                "segment_id",
                out JsonElement segmentElement)
                ? segmentElement.GetString()
                  ?? "multitalker-current"
                : "multitalker-current";

        string? speakerId =
            root.TryGetProperty(
                "speaker_id",
                out JsonElement speakerElement)
                ? speakerElement.GetString()
                : null;

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

                /*
                 * Multitalker owns speaker attribution.
                 * Its one-based identifier is the same
                 * identity consumed by the existing UI.
                 */
                SpeakerId =
                    string.IsNullOrWhiteSpace(
                        speakerId)
                        ? null
                        : speakerId,
            };

        ResultReceived?.Invoke(
            this,
            new TranscriptResultEventArgs(
                result));
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

    private static double?
        TryReadNonNegativeDouble(
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
                out double value)
            ||
            !double.IsFinite(value)
            ||
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
                "Multitalker Parakeet currently " +
                "supports English transcription.");
        }
    }

    private static void ValidateChunk(
        TranscriptionAudioChunk chunk,
        int configuredSampleRate)
    {
        if (
            chunk.SampleRate !=
            configuredSampleRate)
        {
            throw new ArgumentException(
                "The audio chunk sample rate " +
                "does not match the active session.",
                nameof(chunk));
        }

        if (
            chunk.Channels != 1 ||
            chunk.BitsPerSample != 16 ||
            chunk.Encoding !=
                AudioSampleEncoding.PcmInteger)
        {
            throw new ArgumentException(
                "Multitalker requires mono PCM16 audio.",
                nameof(chunk));
        }

        if (
            chunk.Data.IsEmpty ||
            chunk.Data.Length %
                sizeof(short) != 0)
        {
            throw new ArgumentException(
                "The audio chunk must contain " +
                "complete PCM16 samples.",
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
            await _serviceLauncher
                .DisposeAsync();

            lock (_gate)
            {
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }
    }
}
