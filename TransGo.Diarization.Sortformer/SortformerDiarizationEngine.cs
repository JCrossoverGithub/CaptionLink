using System.Diagnostics;
using System.Text.Json;
using TransGo.Core.Audio;
using TransGo.Core.Diarization;

namespace TransGo.Diarization.Sortformer;

/// <summary>
/// Streams normalized TransGo audio to the local Sortformer
/// service and publishes speaker-activity intervals.
/// </summary>
public sealed class SortformerDiarizationEngine
    : IDiarizationEngine
{
    private readonly object _gate = new();

    private SortformerServiceClient? _client;
    private int? _configuredSampleRate;

    private bool _isStarting;
    private bool _isRunning;
    private bool _disposed;

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
                    "Sortformer diarization is already running.");
            }

            _isStarting = true;
        }

        var client =
            new SortformerServiceClient();

        client.MessageReceived +=
            Client_MessageReceived;

        try
        {
            await client.ConnectAsync(
                configuration.SampleRate,
                configuration.MaximumSpeakers,
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
                "Sortformer diarization is running.");
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

        SortformerServiceClient client;
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
                    "Sortformer diarization is not running.");
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
        SortformerServiceClient? client;

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
            "Sortformer diarization stopped.");
    }

    private void Client_MessageReceived(
        object? sender,
        SortformerServiceMessageEventArgs e)
    {
        try
        {
            SpeakerActivity? activity =
                TryParseSpeakerActivity(
                    e.Json);

            if (activity is not null)
            {
                PublishActivity(activity);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                "Could not parse a Sortformer service " +
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
                "The Sortformer activity sequence " +
                "must be positive.");
        }

        if (
            !double.IsFinite(startSeconds) ||
            startSeconds < 0)
        {
            throw new InvalidOperationException(
                "The Sortformer activity start time " +
                "is invalid.");
        }

        if (
            !double.IsFinite(endSeconds) ||
            endSeconds <= startSeconds)
        {
            throw new InvalidOperationException(
                "The Sortformer activity end time " +
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
                "The Sortformer activity confidence " +
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
                    "A Sortformer diarization event " +
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
                "The Sortformer message is missing " +
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
                "The Sortformer message has an invalid " +
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
                "The Sortformer message has an invalid " +
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
                "The Sortformer message has an invalid " +
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
                "The Sortformer message has an invalid " +
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
            configuration.MaximumSpeakers > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "Sortformer supports between one " +
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
                "match the Sortformer configuration.");
        }

        if (
            chunk.Channels != 1 ||
            chunk.BitsPerSample != 16 ||
            chunk.Encoding !=
                AudioSampleEncoding.PcmInteger)
        {
            throw new ArgumentException(
                "Sortformer requires mono PCM16 audio.",
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
            lock (_gate)
            {
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }
    }
}
