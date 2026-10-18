using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CaptionLink.Core.Diarization;
using CaptionLink.Core.Transcription;

namespace CaptionLink.Speech.GPULink;

/// <summary>
/// Workload-specific payloads transported over GPULink's generic data plane.
/// This class neither authenticates to GPULink nor launches a worker.
/// The wire forms match CaptionLink's independently reviewed Multitalker adapter.
/// </summary>
public static class MultitalkerWire
{
    public const int SampleRate = 16_000;
    public const int MaxSpeakers = 4;
    public const uint MaxSamples = 600u * SampleRate;
    public const int MaxFrameBytes = 65_536;
    public const string StartType = "captionlink.multitalker.start.v0";
    public const string ReadyType = "captionlink.multitalker.model_ready.v0";
    public const string EndType = "gpulink.session.end.v1";
    public const string CompletedType = "gpulink.session.completed.v1";

    public static string StartJson =>
        "{\"type\":\"captionlink.multitalker.start.v0\",\"sample_rate\":16000,\"channels\":1,\"bits_per_sample\":16}";

    public static string EndJson => "{\"type\":\"gpulink.session.end.v1\"}";

    /// <summary>
    /// PCM16 LE, mono, at 16kHz. The 16-byte ASR1 envelope contains unsigned
    /// LE sequence, sample-offset, and sample-count integers.
    /// GPULink's data-plane frame bound is larger; this adapter's is 65536.
    /// </summary>
    public static byte[] EncodePcm(uint sequence, uint sampleOffset, ReadOnlySpan<byte> pcm16)
    {
        if (sequence == uint.MaxValue || pcm16.Length < 2 || pcm16.Length % 2 != 0 ||
            pcm16.Length > MaxFrameBytes - 16)
        {
            throw new ArgumentOutOfRangeException(nameof(pcm16), "Invalid adapter audio chunk.");
        }
        uint samples = (uint)(pcm16.Length / 2);
        if (sampleOffset > MaxSamples || samples > MaxSamples - sampleOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleOffset), "Adapter audio exceeds 600 seconds.");
        }

        byte[] frame = new byte[16 + pcm16.Length];
        "ASR1"u8.CopyTo(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), sampleOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), samples);
        pcm16.CopyTo(frame.AsSpan(16));
        return frame;
    }

    public static MultitalkerEvent ParseEvent(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxFrameBytes)
            throw new FormatException("Multitalker event exceeds the adapter frame bound.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("Multitalker event must be an object.");
            // Reject duplicate JSON keys instead of accepting a parser-dependent last value.
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
                if (!keys.Add(property.Name))
                    throw new FormatException("Duplicate Multitalker event field.");

            string type = RequiredString(root, "type", 80);
            switch (type)
            {
                case ReadyType:
                    if (RequiredInt(root, "sample_rate") != SampleRate ||
                        RequiredInt(root, "max_speakers") != MaxSpeakers)
                        throw new FormatException("Incompatible Multitalker readiness.");
                    return new MultitalkerEvent(MultitalkerEventKind.Ready, null, null);
                case CompletedType:
                    return new MultitalkerEvent(MultitalkerEventKind.Completed, null, null);
                case "transcript":
                {
                    string speaker = Speaker(root);
                    string segment = RequiredString(root, "segment_id", 160);
                    string text = RequiredString(root, "text", 8192, allowEmpty: true);
                    long sequence = RequiredPositiveLong(root, "sequence");
                    bool isFinal = RequiredBool(root, "is_final");
                    double start = RequiredTime(root, "result_start_time_seconds");
                    double end = RequiredTime(root, "result_end_time_seconds");
                    if (end < start) throw new FormatException("Transcript interval is reversed.");
                    var transcript = new TranscriptResult(segment, sequence, text, isFinal, null,
                        TimeSpan.FromSeconds(end))
                    {
                        ResultStartTime = TimeSpan.FromSeconds(start),
                        SpeakerId = speaker
                    };
                    return new MultitalkerEvent(MultitalkerEventKind.Transcript, transcript, null);
                }
                case "speaker_activity":
                {
                    string speaker = Speaker(root);
                    string activityId = RequiredString(root, "activity_id", 160);
                    long sequence = RequiredPositiveLong(root, "sequence");
                    bool isFinal = RequiredBool(root, "is_final");
                    double start = RequiredTime(root, "start_time_seconds");
                    double end = RequiredTime(root, "end_time_seconds");
                    if (end < start) throw new FormatException("Speaker interval is reversed.");
                    double? confidence = OptionalConfidence(root, "confidence");
                    var activity = new SpeakerActivity(activityId, sequence, speaker,
                        TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), isFinal,
                        confidence);
                    return new MultitalkerEvent(MultitalkerEventKind.SpeakerActivity, null, activity);
                }
                default:
                    throw new FormatException("Unknown Multitalker event type.");
            }
        }
        catch (JsonException exception)
        {
            throw new FormatException("Malformed Multitalker JSON event.", exception);
        }
    }

    private static string Speaker(JsonElement root)
    {
        string value = RequiredString(root, "speaker_id", 9);
        if (value is not ("speaker-1" or "speaker-2" or "speaker-3" or "speaker-4"))
            throw new FormatException("Invalid Multitalker speaker identifier.");
        return value;
    }

    private static string RequiredString(JsonElement obj, string key, int maximum, bool allowEmpty = false)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.String)
            throw new FormatException($"Missing {key}.");
        string value = el.GetString()!;
        if (value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new FormatException($"Invalid {key}.");
        return value;
    }

    private static int RequiredInt(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number ||
            !el.TryGetInt32(out int number))
            throw new FormatException($"Invalid {key}.");
        return number;
    }

    private static long RequiredPositiveLong(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number ||
            !el.TryGetInt64(out long number) || number <= 0)
            throw new FormatException($"Invalid {key}.");
        return number;
    }

    private static bool RequiredBool(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) ||
            el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException($"Invalid {key}.");
        return el.GetBoolean();
    }

    private static double RequiredTime(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind != JsonValueKind.Number ||
            !el.TryGetDouble(out double value) || !double.IsFinite(value) || value < 0 || value > 600)
            throw new FormatException($"Invalid {key}.");
        return value;
    }

    private static double? OptionalConfidence(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement el) || el.ValueKind == JsonValueKind.Null)
            return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double value) ||
            !double.IsFinite(value) || value < 0 || value > 1)
            throw new FormatException($"Invalid {key}.");
        return value;
    }
}

public enum MultitalkerEventKind { Ready, Transcript, SpeakerActivity, Completed }

public sealed record MultitalkerEvent(
    MultitalkerEventKind Kind,
    TranscriptResult? Transcript,
    SpeakerActivity? Activity);

/// <summary>
/// Stateful per-stream checks; speakers are allowed to overlap in time.
/// Successful session completion is a separate, explicit protocol event.
/// </summary>
public sealed class MultitalkerEventTracker
{
    private long _transcriptSequence;
    private long _activitySequence;
    private readonly Dictionary<string, (string Speaker, bool Final)> _segments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Speaker, bool Final)> _activities = new(StringComparer.Ordinal);
    private bool _ready;
    private bool _completed;

    public bool IsReady => _ready;
    public bool IsCompleted => _completed;

    public MultitalkerEvent Accept(string json)
    {
        MultitalkerEvent message = MultitalkerWire.ParseEvent(json);
        if (_completed) throw new InvalidOperationException("Event after explicit completion.");
        switch (message.Kind)
        {
            case MultitalkerEventKind.Ready:
                if (_ready) throw new InvalidOperationException("Duplicate model readiness.");
                _ready = true;
                break;
            case MultitalkerEventKind.Completed:
                if (!_ready) throw new InvalidOperationException("Completion before model readiness.");
                _completed = true;
                break;
            case MultitalkerEventKind.Transcript:
                RequireReady();
                TranscriptResult result = message.Transcript!;
                if (result.Sequence <= _transcriptSequence)
                    throw new InvalidOperationException("Non-monotonic transcript sequence.");
                CheckRevision(_segments, result.SegmentId, result.SpeakerId!, result.IsFinal);
                _transcriptSequence = result.Sequence;
                break;
            case MultitalkerEventKind.SpeakerActivity:
                RequireReady();
                SpeakerActivity activity = message.Activity!;
                if (activity.Sequence <= _activitySequence)
                    throw new InvalidOperationException("Non-monotonic speaker activity sequence.");
                CheckRevision(_activities, activity.ActivityId, activity.SpeakerId, activity.IsFinal);
                _activitySequence = activity.Sequence;
                break;
        }
        return message;
    }

    private void RequireReady()
    {
        if (!_ready) throw new InvalidOperationException("Output before model readiness.");
    }

    private static void CheckRevision(
        Dictionary<string, (string Speaker, bool Final)> observed,
        string id, string speaker, bool final)
    {
        if (observed.TryGetValue(id, out var old) && (old.Speaker != speaker || old.Final))
            throw new InvalidOperationException("Invalid speaker revision or update after final.");
        observed[id] = (speaker, final);
    }
}
