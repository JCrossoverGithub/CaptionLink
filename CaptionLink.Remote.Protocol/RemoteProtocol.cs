namespace CaptionLink.Remote.Protocol;

public static class RemoteProtocol
{
    public const int CurrentVersion = 1;
    public const string WebSocketPath = "/v1/transcription";

    public const string RequiredAudioEncoding = "pcm_s16le";
    public const int RequiredSampleRateHz = 16_000;
    public const int RequiredChannels = 1;
    public const int RequiredBitsPerSample = 16;

    public const int AudioChunkHeaderSize = 24;
    public const int MaxAudioChunkPayloadBytes = 64 * 1024;
    public const int RecommendedAudioChunkMilliseconds = 100;
}

public static class RemoteMessageTypes
{
    public const string StartSession = "start_session";
    public const string SessionStarted = "session_started";
    public const string StopSession = "stop_session";
    public const string Caption = "caption";
    public const string SessionEnded = "session_ended";
    public const string Error = "error";
}

public static class RemoteErrorCodes
{
    public const string UnsupportedProtocol = "unsupported_protocol";
    public const string InvalidMessage = "invalid_message";
    public const string InvalidAudioFormat = "invalid_audio_format";
    public const string Unauthorized = "unauthorized";
    public const string SessionNotStarted = "session_not_started";
    public const string OutOfOrderAudio = "out_of_order_audio";
    public const string EngineUnavailable = "engine_unavailable";
    public const string InternalError = "internal_error";
}