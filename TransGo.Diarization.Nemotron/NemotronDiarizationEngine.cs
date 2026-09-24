using System.Diagnostics;
using System.Text.Json;
using TransGo.Core.Audio;
using TransGo.Core.Diarization;

namespace TransGo.Diarization.Nemotron;

/// <summary>
/// Streams normalized TransGo audio to the local Nemotron
/// service and publishes speaker-activity intervals.
/// </summary>
public sealed class NemotronDiarizationEngine
    : IDiarizationEngine
{
    private readonly object _gate = new();

    private readonly NemotronServiceLauncher
        _serviceLauncher = new();

    private NemotronServiceClient? _client;
    private int? _configuredSampleRate;

    private bool _isStarting;
    private bool _isRunning;
    private bool _disposed;

    public event EventHandler<SpeakerActivityEventArgs>?
        ActivityReceived;

    public event EventHandler<
        NemotronAudioProgressEventArgs>?
            AudioProgressReceived;

    /// <summary>
    /// Publishes raw model probabilities only when the session was
    /// started with PublishSpeakerProbabilities enabled. Production
    /// sessions leave this disabled to avoid serialization overhead.
    /// </summary>
    public event EventHandler<
        NemotronProbabilityBatchEventArgs>?
            ProbabilityBatchReceived;

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
        DiarizationConfiguration configuration,
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
                    "Nemotron diarization is already running.");
            }

            _isStarting = true;
        }

        var client =
            new NemotronServiceClient();

        client.MessageReceived +=
            Client_MessageReceived;

        try
        {
            NemotronServiceHealth health =
                await _serviceLauncher.EnsureReadyAsync(
                    cancellationToken);

            Debug.WriteLine(
                "Local Nemotron service ready. " +
                $"GPU: {health.Gpu ?? "unknown"}.");

            await client.ConnectAsync(
                configuration.SampleRate,
                configuration.MaximumSpeakers,
                configuration.PublishSpeakerProbabilities,
                cancellationToken);

            lock (_gate)
            {
                ThrowIfDisposedLocked();

                _client = client;
                _configuredSampleRate =
                    configuration.SampleRate;

                _isRunning = true;
            }

            Debug.WriteLine(
                "Nemotron diarization is running.");
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

        NemotronServiceClient client;
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
                    "Nemotron diarization is not running.");
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
        NemotronServiceClient? client;

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            client = _client;

            _client = null;
            _configuredSampleRate = null;
            _isRunning = false;
        }

        if (client is null)
        {
            return;
        }

        try
        {
            /*
             * Keep the message subscription active until StopAsync
             * receives the service's finalized speaker activities
             * and its final "stopped" message.
             */
            await client.StopAsync(
                cancellationToken);
        }
        finally
        {
            client.MessageReceived -=
                Client_MessageReceived;

            await client.DisposeAsync();
        }

        Debug.WriteLine(
            "Nemotron diarization stopped.");
    }

    private void Client_MessageReceived(
        object? sender,
        NemotronServiceMessageEventArgs e)
    {
        try
        {
            SpeakerActivity? activity =
                TryParseSpeakerActivity(
                    e.Json);

            if (activity is not null)
            {
                PublishActivity(activity);
                return;
            }

            NemotronProbabilityBatch? probabilityBatch =
                TryParseProbabilityBatch(e.Json);

            if (probabilityBatch is not null)
            {
                PublishProbabilityBatch(probabilityBatch);
                return;
            }

            long? chunksReceived =
                TryParseAudioProgress(
                    e.Json);

            if (chunksReceived is not null)
            {
                PublishAudioProgress(
                    chunksReceived.Value);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                "Could not parse a Nemotron service " +
                $"message: {exception}");
        }
    }

    private static SpeakerActivity?
        TryParseSpeakerActivity(
            string json)
    {
        using JsonDocument document =
            JsonDocument.Parse(json);

        JsonElement root =
            document.RootElement;

        if (
            !root.TryGetProperty(
                "type",
                out JsonElement typeElement)
            ||
            typeElement.ValueKind !=
                JsonValueKind.String
            ||
            !string.Equals(
                typeElement.GetString(),
                "speaker_activity",
                StringComparison.Ordinal))
        {
            return null;
        }

        string activityId =
            GetRequiredString(
                root,
                "activity_id");

        long sequence =
            GetRequiredInt64(
                root,
                "sequence");

        string speakerId =
            GetRequiredString(
                root,
                "speaker_id");

        double startSeconds =
            GetRequiredDouble(
                root,
                "start_time_seconds");

        double endSeconds =
            GetRequiredDouble(
                root,
                "end_time_seconds");

        bool isFinal =
            GetRequiredBoolean(
                root,
                "is_final");

        double? confidence =
            GetOptionalDouble(
                root,
                "confidence");

        if (sequence <= 0)
        {
            throw new InvalidOperationException(
                "The Nemotron activity sequence " +
                "must be positive.");
        }

        if (
            !double.IsFinite(startSeconds) ||
            startSeconds < 0)
        {
            throw new InvalidOperationException(
                "The Nemotron activity start time " +
                "is invalid.");
        }

        if (
            !double.IsFinite(endSeconds) ||
            endSeconds <= startSeconds)
        {
            throw new InvalidOperationException(
                "The Nemotron activity end time " +
                "is invalid.");
        }

        if (
            confidence is not null &&
            (
                !double.IsFinite(
                    confidence.Value)
                ||
                confidence.Value < 0
                ||
                confidence.Value > 1
            ))
        {
            throw new InvalidOperationException(
                "The Nemotron activity confidence " +
                "is invalid.");
        }

        return new SpeakerActivity(
            ActivityId:
                activityId,

            Sequence:
                sequence,

            SpeakerId:
                speakerId,

            StartTime:
                TimeSpan.FromSeconds(
                    startSeconds),

            EndTime:
                TimeSpan.FromSeconds(
                    endSeconds),

            IsFinal:
                isFinal,

            Confidence:
                confidence);
    }

    private void PublishActivity(
        SpeakerActivity activity)
    {
        EventHandler<SpeakerActivityEventArgs>?
            handlers =
                ActivityReceived;

        if (handlers is null)
        {
            return;
        }

        var eventArgs =
            new SpeakerActivityEventArgs(
                activity);

        foreach (
            EventHandler<SpeakerActivityEventArgs>
                handler
            in handlers.GetInvocationList())
        {
            try
            {
                handler(
                    this,
                    eventArgs);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "A Nemotron diarization event " +
                    $"handler failed: {exception}");
            }
        }
    }

    private static long? TryParseAudioProgress(
        string json)
    {
        using JsonDocument document =
            JsonDocument.Parse(json);

        JsonElement root =
            document.RootElement;

        if (
            !root.TryGetProperty(
                "type",
                out JsonElement typeElement)
            ||
            typeElement.ValueKind !=
                JsonValueKind.String
            ||
            !string.Equals(
                typeElement.GetString(),
                "audio_received",
                StringComparison.Ordinal))
        {
            return null;
        }

        long chunksReceived =
            GetRequiredInt64(
                root,
                "chunks_received");

        if (chunksReceived <= 0)
        {
            throw new InvalidOperationException(
                "The Nemotron processed chunk count " +
                "must be positive.");
        }

        return chunksReceived;
    }

    private static NemotronProbabilityBatch?
        TryParseProbabilityBatch(string json)
    {
        using JsonDocument document =
            JsonDocument.Parse(json);

        JsonElement root = document.RootElement;

        if (
            !root.TryGetProperty("type", out JsonElement typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(
                typeElement.GetString(),
                "speaker_probabilities",
                StringComparison.Ordinal))
        {
            return null;
        }

        long startFrameIndex =
            GetRequiredInt64(root, "start_frame_index");

        double frameDurationSeconds =
            GetRequiredDouble(root, "frame_duration_seconds");

        if (startFrameIndex < 0)
        {
            throw new InvalidOperationException(
                "The Nemotron probability frame index is invalid.");
        }

        if (
            !double.IsFinite(frameDurationSeconds) ||
            frameDurationSeconds <= 0)
        {
            throw new InvalidOperationException(
                "The Nemotron probability frame duration is invalid.");
        }

        if (
            !root.TryGetProperty(
                "probabilities",
                out JsonElement probabilitiesElement) ||
            probabilitiesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "The Nemotron probability matrix is missing.");
        }

        var frames =
            new List<float[]>();

        int? speakerCount = null;

        foreach (JsonElement frameElement in
            probabilitiesElement.EnumerateArray())
        {
            if (frameElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "A Nemotron probability frame is invalid.");
            }

            float[] frame = frameElement
                .EnumerateArray()
                .Select(value => value.GetSingle())
                .ToArray();

            if (frame.Length == 0)
            {
                throw new InvalidOperationException(
                    "A Nemotron probability frame is empty.");
            }

            if (speakerCount is not null &&
                speakerCount.Value != frame.Length)
            {
                throw new InvalidOperationException(
                    "Nemotron probability frames have different speaker counts.");
            }

            if (frame.Any(value =>
                    !float.IsFinite(value) ||
                    value < 0 ||
                    value > 1))
            {
                throw new InvalidOperationException(
                    "A Nemotron speaker probability is invalid.");
            }

            speakerCount = frame.Length;
            frames.Add(frame);
        }

        if (frames.Count == 0)
        {
            throw new InvalidOperationException(
                "The Nemotron probability batch is empty.");
        }

        return new NemotronProbabilityBatch(
            startFrameIndex,
            frameDurationSeconds,
            frames);
    }

    private void PublishProbabilityBatch(
        NemotronProbabilityBatch batch)
    {
        EventHandler<NemotronProbabilityBatchEventArgs>?
            handlers = ProbabilityBatchReceived;

        if (handlers is null)
        {
            return;
        }

        var eventArgs =
            new NemotronProbabilityBatchEventArgs(batch);

        foreach (
            EventHandler<NemotronProbabilityBatchEventArgs> handler
            in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "A Nemotron probability event handler failed: " +
                    exception);
            }
        }
    }

    private void PublishAudioProgress(
        long chunksReceived)
    {
        EventHandler<NemotronAudioProgressEventArgs>?
            handlers =
                AudioProgressReceived;

        if (handlers is null)
        {
            return;
        }

        var eventArgs =
            new NemotronAudioProgressEventArgs(
                chunksReceived);

        foreach (
            EventHandler<NemotronAudioProgressEventArgs>
                handler
            in handlers.GetInvocationList())
        {
            try
            {
                handler(
                    this,
                    eventArgs);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "A Nemotron audio-progress event " +
                    $"handler failed: {exception}");
            }
        }
    }

    private static string GetRequiredString(
        JsonElement root,
        string propertyName)
    {
        if (
            !root.TryGetProperty(
                propertyName,
                out JsonElement value)
            ||
            value.ValueKind !=
                JsonValueKind.String
            ||
            string.IsNullOrWhiteSpace(
                value.GetString()))
        {
            throw new InvalidOperationException(
                "The Nemotron message is missing " +
                $"'{propertyName}'.");
        }

        return value.GetString()!.Trim();
    }

    private static long GetRequiredInt64(
        JsonElement root,
        string propertyName)
    {
        if (
            !root.TryGetProperty(
                propertyName,
                out JsonElement value)
            ||
            value.ValueKind !=
                JsonValueKind.Number
            ||
            !value.TryGetInt64(
                out long result))
        {
            throw new InvalidOperationException(
                "The Nemotron message has an invalid " +
                $"'{propertyName}'.");
        }

        return result;
    }

    private static double GetRequiredDouble(
        JsonElement root,
        string propertyName)
    {
        if (
            !root.TryGetProperty(
                propertyName,
                out JsonElement value)
            ||
            value.ValueKind !=
                JsonValueKind.Number
            ||
            !value.TryGetDouble(
                out double result))
        {
            throw new InvalidOperationException(
                "The Nemotron message has an invalid " +
                $"'{propertyName}'.");
        }

        return result;
    }

    private static bool GetRequiredBoolean(
        JsonElement root,
        string propertyName)
    {
        if (
            !root.TryGetProperty(
                propertyName,
                out JsonElement value)
            ||
            (
                value.ValueKind !=
                    JsonValueKind.True
                &&
                value.ValueKind !=
                    JsonValueKind.False
            ))
        {
            throw new InvalidOperationException(
                "The Nemotron message has an invalid " +
                $"'{propertyName}'.");
        }

        return value.GetBoolean();
    }

    private static double? GetOptionalDouble(
        JsonElement root,
        string propertyName)
    {
        if (
            !root.TryGetProperty(
                propertyName,
                out JsonElement value)
            ||
            value.ValueKind ==
                JsonValueKind.Null)
        {
            return null;
        }

        if (
            value.ValueKind !=
                JsonValueKind.Number
            ||
            !value.TryGetDouble(
                out double result))
        {
            throw new InvalidOperationException(
                "The Nemotron message has an invalid " +
                $"'{propertyName}'.");
        }

        return result;
    }

    private static void ValidateConfiguration(
        DiarizationConfiguration configuration)
    {
        if (configuration.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "The sample rate must be positive.");
        }

        if (
            configuration.MaximumSpeakers <= 0 ||
            configuration.MaximumSpeakers > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Nemotron supports between one " +
                "and four speakers.");
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
            throw new InvalidOperationException(
                "The audio chunk sample rate does not " +
                "match the Nemotron configuration.");
        }

        if (
            chunk.Channels != 1 ||
            chunk.BitsPerSample != 16 ||
            chunk.Encoding !=
                AudioSampleEncoding.PcmInteger)
        {
            throw new ArgumentException(
                "Nemotron requires mono PCM16 audio.",
                nameof(chunk));
        }

        if (
            chunk.Data.IsEmpty ||
            chunk.Data.Length %
                sizeof(short) != 0)
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
