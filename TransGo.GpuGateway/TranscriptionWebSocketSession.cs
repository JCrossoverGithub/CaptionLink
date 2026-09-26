using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using TransGo.Core.Audio;
using TransGo.Core.Transcription;
using TransGo.Remote.Protocol;
using TransGo.Remote.Protocol.Messages;

namespace TransGo.GpuGateway;

internal static class TranscriptionWebSocketSession
{
    private const int MaximumMessageBytes =
        RemoteProtocol.AudioChunkHeaderSize +
        RemoteProtocol.MaxAudioChunkPayloadBytes;

    public static async Task RunAsync(
        WebSocket socket,
        Func<ITranscriptionEngine> engineFactory,
        CancellationToken requestAborted)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(engineFactory);

        Channel<IRemoteProtocolMessage> outbound =
            Channel.CreateUnbounded<IRemoteProtocolMessage>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });

        using var sessionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                requestAborted);

        Task sendTask =
            SendLoopAsync(
                socket,
                outbound.Reader,
                sessionCancellation.Token);

        ITranscriptionEngine? engine = null;

        EventHandler<TranscriptResultEventArgs>?
            resultHandler = null;

        string? sessionId = null;

        WebSocketCloseStatus closeStatus =
            WebSocketCloseStatus.NormalClosure;

        string closeDescription = "Session ended.";

        try
        {
            ReceivedMessage firstMessage =
                await ReceiveMessageAsync(
                    socket,
                    MaximumMessageBytes,
                    sessionCancellation.Token);

            if (firstMessage.MessageType ==
                WebSocketMessageType.Close)
            {
                closeDescription = "Client closed the connection.";
                return;
            }

            if (firstMessage.MessageType !=
                WebSocketMessageType.Text)
            {
                throw new RemoteProtocolException(
                    RemoteErrorCodes.SessionNotStarted,
                    "The first message must be start_session.");
            }

            string firstMessageType =
                ProtocolJson.GetMessageType(
                    firstMessage.Payload);

            if (firstMessageType !=
                RemoteMessageTypes.StartSession)
            {
                throw new RemoteProtocolException(
                    RemoteErrorCodes.SessionNotStarted,
                    "The first message must be start_session.");
            }

            StartSessionMessage startMessage =
                ProtocolJson.Deserialize<StartSessionMessage>(
                    firstMessage.Payload);

            ProtocolValidationResult validation =
                ProtocolValidation.Validate(startMessage);

            if (!validation.IsValid)
            {
                throw new RemoteProtocolException(
                    validation.ErrorCode
                        ?? RemoteErrorCodes.InvalidMessage,
                    validation.ErrorMessage
                        ?? "The start_session message is invalid.");
            }

            sessionId =
                Guid.NewGuid().ToString("N");

            long captionSequence = -1;

            var latencyTracker =
                new GatewayLatencyTracker();

            engine =
                engineFactory();

            resultHandler =
                (_, eventArgs) =>
                {
                    TranscriptResult result =
                        eventArgs.Result;

                    long startMilliseconds =
                        ToMilliseconds(
                            result.ResultStartTime);

                    long endMilliseconds =
                        result.ResultEndTime.HasValue
                            ? ToMilliseconds(
                                result.ResultEndTime)
                            : startMilliseconds;

                    CaptionLatencyMetrics? latency =
                        latencyTracker.CreateCaptionMetrics(
                            endMilliseconds,
                            result.ProviderProcessingMilliseconds,
                            Stopwatch.GetTimestamp());

                    outbound.Writer.TryWrite(
                        new CaptionMessage(
                            SessionId: sessionId,
                            Sequence:
                                Interlocked.Increment(
                                    ref captionSequence),
                            SegmentId: result.SegmentId,
                            Text: result.Text,
                            IsFinal: result.IsFinal,
                            StartTimeMilliseconds:
                                startMilliseconds,
                            EndTimeMilliseconds:
                                endMilliseconds,
                            EmittedAtUtc:
                                DateTimeOffset.UtcNow)
                        {
                            Latency = latency
                        });
                };

            engine.ResultReceived += resultHandler;

            var configuration =
                new TranscriptionConfiguration(
                    LanguageCode:
                        startMessage.Options.Language,
                    SampleRate:
                        startMessage.Audio.SampleRateHz,
                    EnableInterimResults:
                        startMessage.Options
                            .EnableInterimResults);

            await engine.StartAsync(
                configuration,
                sessionCancellation.Token);

            DateTimeOffset captureOriginUtc =
                DateTimeOffset.UtcNow;

            outbound.Writer.TryWrite(
                new SessionStartedMessage(
                    ProtocolVersion:
                        RemoteProtocol.CurrentVersion,
                    RequestId:
                        startMessage.RequestId,
                    SessionId:
                        sessionId,
                    AcceptedAudio:
                        startMessage.Audio,
                    Engine:
                        "parakeet-accurate",
                    ServerTimeUtc:
                        DateTimeOffset.UtcNow));

            long expectedAudioSequence = 0;
            long audioChunksReceived = 0;
            long audioBytesReceived = 0;
            string endReason = "client_disconnected";

            while (socket.State == WebSocketState.Open)
            {
                ReceivedMessage message =
                    await ReceiveMessageAsync(
                        socket,
                        MaximumMessageBytes,
                        sessionCancellation.Token);

                if (message.MessageType ==
                    WebSocketMessageType.Close)
                {
                    endReason = "client_disconnected";
                    break;
                }

                if (message.MessageType ==
                    WebSocketMessageType.Text)
                {
                    string messageType =
                        ProtocolJson.GetMessageType(
                            message.Payload);

                    if (messageType !=
                        RemoteMessageTypes.StopSession)
                    {
                        throw new RemoteProtocolException(
                            RemoteErrorCodes.InvalidMessage,
                            $"Unexpected control message " +
                            $"'{messageType}'.");
                    }

                    StopSessionMessage stopMessage =
                        ProtocolJson.Deserialize<
                            StopSessionMessage>(
                                message.Payload);

                    if (!string.Equals(
                            stopMessage.SessionId,
                            sessionId,
                            StringComparison.Ordinal))
                    {
                        throw new RemoteProtocolException(
                            RemoteErrorCodes.InvalidMessage,
                            "The stop_session session ID " +
                            "does not match the active session.");
                    }

                    endReason =
                        string.IsNullOrWhiteSpace(
                            stopMessage.Reason)
                            ? "client_request"
                            : stopMessage.Reason;

                    break;
                }

                RemoteAudioChunk remoteChunk =
                    AudioChunkCodec.Decode(
                        message.Payload);

                long gatewayReceivedTimestamp =
                    Stopwatch.GetTimestamp();

                if (remoteChunk.Sequence !=
                    expectedAudioSequence)
                {
                    throw new RemoteProtocolException(
                        RemoteErrorCodes.OutOfOrderAudio,
                        $"Expected audio sequence " +
                        $"{expectedAudioSequence}, but received " +
                        $"{remoteChunk.Sequence}.");
                }

                var audioChunk =
                    new TranscriptionAudioChunk(
                        Sequence:
                            remoteChunk.Sequence,
                        CapturedAt:
                            captureOriginUtc +
                            TimeSpan.FromMilliseconds(
                                remoteChunk
                                    .CaptureTimestampMilliseconds),
                        Data:
                            remoteChunk.PcmBytes,
                        SampleRate:
                            RemoteProtocol
                                .RequiredSampleRateHz)
                    {
                        SessionStartTime =
                            TimeSpan.FromMilliseconds(
                                remoteChunk
                                    .CaptureTimestampMilliseconds)
                    };

                latencyTracker.RecordReceived(
                    remoteChunk.Sequence,
                    remoteChunk.CaptureTimestampMilliseconds,
                    ToMilliseconds(audioChunk.Duration),
                    gatewayReceivedTimestamp);

                latencyTracker.RecordDispatchStarted(
                    remoteChunk.Sequence,
                    Stopwatch.GetTimestamp());

                await engine.SendAsync(
                    audioChunk,
                    sessionCancellation.Token);

                expectedAudioSequence++;
                audioChunksReceived++;
                audioBytesReceived +=
                    remoteChunk.PcmBytes.LongLength;
            }

            await engine.StopAsync(
                sessionCancellation.Token);

            outbound.Writer.TryWrite(
                new SessionEndedMessage(
                    SessionId:
                        sessionId,
                    Reason:
                        endReason,
                    AudioChunksReceived:
                        audioChunksReceived,
                    AudioBytesReceived:
                        audioBytesReceived,
                    EndedAtUtc:
                        DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException)
            when (requestAborted.IsCancellationRequested)
        {
            closeDescription = "Connection cancelled.";
        }
        catch (RemoteProtocolException exception)
        {
            DetachResultHandler(
                engine,
                ref resultHandler);

            outbound.Writer.TryWrite(
                new ErrorMessage(
                    SessionId:
                        sessionId,
                    Code:
                        exception.Code,
                    Message:
                        exception.Message,
                    IsFatal:
                        true));

            closeStatus =
                WebSocketCloseStatus.PolicyViolation;

            closeDescription = exception.Code;
        }
        catch (JsonException exception)
        {
            DetachResultHandler(
                engine,
                ref resultHandler);

            outbound.Writer.TryWrite(
                new ErrorMessage(
                    SessionId:
                        sessionId,
                    Code:
                        RemoteErrorCodes.InvalidMessage,
                    Message:
                        exception.Message,
                    IsFatal:
                        true));

            closeStatus =
                WebSocketCloseStatus.PolicyViolation;

            closeDescription =
                RemoteErrorCodes.InvalidMessage;
        }
        catch (Exception exception)
        {
            DetachResultHandler(
                engine,
                ref resultHandler);

            outbound.Writer.TryWrite(
                new ErrorMessage(
                    SessionId:
                        sessionId,
                    Code:
                        engine is null
                            ? RemoteErrorCodes.EngineUnavailable
                            : RemoteErrorCodes.InternalError,
                    Message:
                        exception.Message,
                    IsFatal:
                        true));

            closeStatus =
                WebSocketCloseStatus.InternalServerError;

            closeDescription =
                RemoteErrorCodes.InternalError;
        }
        finally
        {
            if (engine is not null)
            {
                try
                {
                    if (engine.IsRunning)
                    {
                        await engine.StopAsync();
                    }
                }
                catch
                {
                    // The connection is already ending.
                }

                DetachResultHandler(
                    engine,
                    ref resultHandler);

                await engine.DisposeAsync();
            }

            outbound.Writer.TryComplete();

            try
            {
                await sendTask;
            }
            catch
            {
                // The peer may already have disconnected.
            }

            if (socket.State is
                WebSocketState.Open or
                WebSocketState.CloseReceived)
            {
                using var closeCancellation =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(2));

                try
                {
                    await socket.CloseAsync(
                        closeStatus,
                        closeDescription,
                        closeCancellation.Token);
                }
                catch
                {
                    // No further recovery is possible here.
                }
            }

            sessionCancellation.Cancel();
        }
    }

    private static async Task SendLoopAsync(
        WebSocket socket,
        ChannelReader<IRemoteProtocolMessage> reader,
        CancellationToken cancellationToken)
    {
        await foreach (
            IRemoteProtocolMessage message
            in reader.ReadAllAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }

            byte[] json =
                ProtocolJson.Serialize(message);

            await socket.SendAsync(
                new ArraySegment<byte>(json),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
    }

    private static async Task<ReceivedMessage>
        ReceiveMessageAsync(
            WebSocket socket,
            int maximumBytes,
            CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8 * 1024];

        using var payload = new MemoryStream();

        WebSocketMessageType? messageType = null;

        while (true)
        {
            WebSocketReceiveResult result =
                await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

            if (result.MessageType ==
                WebSocketMessageType.Close)
            {
                return new ReceivedMessage(
                    WebSocketMessageType.Close,
                    [],
                    result.CloseStatus,
                    result.CloseStatusDescription);
            }

            if (messageType is null)
            {
                messageType = result.MessageType;
            }
            else if (messageType != result.MessageType)
            {
                throw new RemoteProtocolException(
                    RemoteErrorCodes.InvalidMessage,
                    "A fragmented WebSocket message changed type.");
            }

            if (payload.Length + result.Count >
                maximumBytes)
            {
                string errorCode =
                    messageType ==
                    WebSocketMessageType.Binary
                        ? RemoteErrorCodes.InvalidAudioFormat
                        : RemoteErrorCodes.InvalidMessage;

                throw new RemoteProtocolException(
                    errorCode,
                    $"WebSocket message exceeds " +
                    $"{maximumBytes} bytes.");
            }

            payload.Write(
                buffer,
                0,
                result.Count);

            if (result.EndOfMessage)
            {
                return new ReceivedMessage(
                    messageType.Value,
                    payload.ToArray(),
                    null,
                    null);
            }
        }
    }

    private static void DetachResultHandler(
        ITranscriptionEngine? engine,
        ref EventHandler<TranscriptResultEventArgs>?
            resultHandler)
    {
        if (engine is not null &&
            resultHandler is not null)
        {
            engine.ResultReceived -= resultHandler;
            resultHandler = null;
        }
    }

    private static long ToMilliseconds(
        TimeSpan? value)
    {
        if (!value.HasValue)
        {
            return 0;
        }

        return Math.Max(
            0,
            (long)Math.Round(
                value.Value.TotalMilliseconds));
    }

    private sealed record ReceivedMessage(
        WebSocketMessageType MessageType,
        byte[] Payload,
        WebSocketCloseStatus? CloseStatus,
        string? CloseStatusDescription);
}
