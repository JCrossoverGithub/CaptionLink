namespace TransGo.Remote.Protocol.Messages;

public interface IRemoteProtocolMessage
{
    string Type { get; }
}

public sealed record RemoteClientInfo(
    string Name,
    string Version,
    string Platform);

public sealed record RemoteAudioFormat(
    string Encoding,
    int SampleRateHz,
    int Channels,
    int BitsPerSample);

public sealed record RemoteSessionOptions(
    string Language,
    bool EnableInterimResults);

public sealed record StartSessionMessage(
    int ProtocolVersion,
    string RequestId,
    RemoteClientInfo Client,
    RemoteAudioFormat Audio,
    RemoteSessionOptions Options) : IRemoteProtocolMessage
{
    public string Type => RemoteMessageTypes.StartSession;
}

public sealed record SessionStartedMessage(
    int ProtocolVersion,
    string RequestId,
    string SessionId,
    RemoteAudioFormat AcceptedAudio,
    string Engine,
    DateTimeOffset ServerTimeUtc) : IRemoteProtocolMessage
{
    public string Type => RemoteMessageTypes.SessionStarted;
}

public sealed record StopSessionMessage(
    string SessionId,
    string Reason) : IRemoteProtocolMessage
{
    public string Type => RemoteMessageTypes.StopSession;
}

public sealed record CaptionLatencyMetrics(
    long AudioChunkSequence,
    long AudioEndTimeMilliseconds,
    double GatewayReceiveToResultMilliseconds,
    double GatewayDispatchToResultMilliseconds,
    double? EngineProcessingMilliseconds);

public sealed record CaptionMessage(
    string SessionId,
    long Sequence,
    string SegmentId,
    string Text,
    bool IsFinal,
    long StartTimeMilliseconds,
    long EndTimeMilliseconds,
    DateTimeOffset EmittedAtUtc) : IRemoteProtocolMessage
{
    public string Type => RemoteMessageTypes.Caption;

    public CaptionLatencyMetrics? Latency { get; init; }
}

public sealed record SessionEndedMessage(
    string SessionId,
    string Reason,
    long AudioChunksReceived,
    long AudioBytesReceived,
    DateTimeOffset EndedAtUtc) : IRemoteProtocolMessage
{
    public string Type => RemoteMessageTypes.SessionEnded;
}

public sealed record ErrorMessage(
    string? SessionId,
    string Code,
    string Message,
    bool IsFatal) : IRemoteProtocolMessage
{
    public string Type => RemoteMessageTypes.Error;
}
