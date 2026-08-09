using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using TransGo.Core.Audio;
using TransGo.Core.Transcription;
using TransGo.Remote.Protocol;
using TransGo.Remote.Protocol.Messages;

namespace TransGo.Speech.Remote;

public sealed class RemoteTranscriptionEngine : ITranscriptionEngine
{
    private static readonly TimeSpan StartTimeout =
        TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StopTimeout =
        TimeSpan.FromSeconds(15);

    private readonly RemoteGatewayOptions? _configuredOptions;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveTask;
    private TaskCompletionSource<SessionEndedMessage>? _sessionEnded;
    private Pcm16MonoResampler? _resampler;
    private string? _sessionId;
    private int _sourceSampleRate;
    private long _outgoingSequence;
    private Exception? _terminalException;
    private int _isRunning;
    private bool _disposed;

    public RemoteTranscriptionEngine()
    {
    }

    public RemoteTranscriptionEngine(RemoteGatewayOptions options)
    {
        _configuredOptions = options ??
            throw new ArgumentNullException(nameof(options));
    }

    public event EventHandler<TranscriptResultEventArgs>? ResultReceived;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public async Task StartAsync(
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        await _lifecycleGate.WaitAsync(cancellationToken);

        try
        {
            ThrowIfDisposed();

            if (IsRunning || _socket is not null)
            {
                throw new InvalidOperationException(
                    "Remote transcription is already running.");
            }

            if (configuration.SampleRate < RemoteProtocol.RequiredSampleRateHz)
            {
                throw new NotSupportedException(
                    $"Remote transcription requires source audio at " +
                    $"{RemoteProtocol.RequiredSampleRateHz} Hz or higher.");
            }

            RemoteGatewayOptions options =
                _configuredOptions ?? RemoteGatewayOptions.FromEnvironment();

            var socket = new ClientWebSocket();
            var sessionCancellation =
                new CancellationTokenSource();

            socket.Options.SetRequestHeader(
                "Authorization",
                $"Bearer {options.Token}");

            using var startCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            startCancellation.CancelAfter(StartTimeout);

            try
            {
                await socket.ConnectAsync(
                    options.WebSocketUri,
                    startCancellation.Token);

                string requestId = Guid.NewGuid().ToString("N");

                var startMessage = new StartSessionMessage(
                    ProtocolVersion: RemoteProtocol.CurrentVersion,
                    RequestId: requestId,
                    Client: new RemoteClientInfo(
                        Name: "TransGo Desktop",
                        Version: GetClientVersion(),
                        Platform: RuntimeInformation.OSDescription),
                    Audio: new RemoteAudioFormat(
                        Encoding: RemoteProtocol.RequiredAudioEncoding,
                        SampleRateHz: RemoteProtocol.RequiredSampleRateHz,
                        Channels: RemoteProtocol.RequiredChannels,
                        BitsPerSample: RemoteProtocol.RequiredBitsPerSample),
                    Options: new RemoteSessionOptions(
                        Language: configuration.LanguageCode,
                        EnableInterimResults:
                            configuration.EnableInterimResults));

                await SendMessageAsync(
                    socket,
                    startMessage,
                    startCancellation.Token);

                byte[] response = await ReceiveTextMessageAsync(
                    socket,
                    startCancellation.Token);

                string messageType = ProtocolJson.GetMessageType(response);

                if (messageType == RemoteMessageTypes.Error)
                {
                    ErrorMessage error =
                        ProtocolJson.Deserialize<ErrorMessage>(response);

                    throw CreateGatewayException(error);
                }

                if (messageType != RemoteMessageTypes.SessionStarted)
                {
                    throw new InvalidOperationException(
                        $"Expected {RemoteMessageTypes.SessionStarted}; " +
                        $"received {messageType}.");
                }

                SessionStartedMessage started =
                    ProtocolJson.Deserialize<SessionStartedMessage>(response);

                ValidateSessionStarted(started, requestId);

                _socket = socket;
                _sessionCancellation = sessionCancellation;
                _sessionId = started.SessionId;
                _sourceSampleRate = configuration.SampleRate;
                _resampler = new Pcm16MonoResampler(
                    configuration.SampleRate,
                    RemoteProtocol.RequiredSampleRateHz);
                _outgoingSequence = 0;
                _terminalException = null;
                _sessionEnded =
                    new TaskCompletionSource<SessionEndedMessage>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                Interlocked.Exchange(ref _isRunning, 1);

                _receiveTask = ReceiveLoopAsync(
                    socket,
                    sessionCancellation.Token);
            }
            catch
            {
                sessionCancellation.Cancel();
                sessionCancellation.Dispose();
                socket.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Timed out while connecting to the remote GPU gateway.",
                exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask SendAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(chunk);

        ClientWebSocket socket = _socket ??
            throw CreateNotConnectedException();

        Pcm16MonoResampler resampler = _resampler ??
            throw CreateNotConnectedException();

        if (!IsRunning)
        {
            throw CreateNotConnectedException();
        }

        if (chunk.SampleRate != _sourceSampleRate)
        {
            throw new InvalidOperationException(
                $"Remote transcription started with {_sourceSampleRate} Hz " +
                $"audio but received a {chunk.SampleRate} Hz chunk.");
        }

        byte[] pcm = resampler.Resample(chunk.Data.Span);
        long timestampMilliseconds = Math.Max(
            0,
            (long)Math.Round(chunk.SessionStartTime.TotalMilliseconds));

        await _sendGate.WaitAsync(cancellationToken);

        try
        {
            if (!IsRunning ||
                !ReferenceEquals(socket, _socket) ||
                socket.State != WebSocketState.Open)
            {
                throw CreateNotConnectedException();
            }

            long sequence = _outgoingSequence++;

            byte[] packet = AudioChunkCodec.Encode(
                sequence,
                timestampMilliseconds,
                pcm);

            await socket.SendAsync(
                new ArraySegment<byte>(packet),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is WebSocketException or IOException)
        {
            MarkDisconnected(exception);
            throw CreateNotConnectedException(exception);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);

        try
        {
            if (_socket is null)
            {
                return;
            }

            Exception? stopFailure = null;

            try
            {
                if (IsRunning &&
                    _sessionId is not null &&
                    _socket.State == WebSocketState.Open)
                {
                    await SendControlMessageAsync(
                        _socket,
                        new StopSessionMessage(
                            SessionId: _sessionId,
                            Reason: "client_stopped"),
                        cancellationToken);

                    Task<SessionEndedMessage>? endedTask =
                        _sessionEnded?.Task;

                    if (endedTask is not null)
                    {
                        await endedTask.WaitAsync(
                            StopTimeout,
                            cancellationToken);
                    }
                }
                else if (_terminalException is not null)
                {
                    stopFailure = CreateNotConnectedException(
                        _terminalException);
                }
            }
            catch (Exception exception)
            {
                stopFailure = exception;
            }
            finally
            {
                await CleanupSessionAsync();
            }

            if (stopFailure is not null)
            {
                throw stopFailure;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] json = await ReceiveTextMessageAsync(
                    socket,
                    cancellationToken);

                string messageType = ProtocolJson.GetMessageType(json);

                switch (messageType)
                {
                    case RemoteMessageTypes.Caption:
                        HandleCaption(
                            ProtocolJson.Deserialize<CaptionMessage>(json));
                        break;

                    case RemoteMessageTypes.Error:
                    {
                        ErrorMessage error =
                            ProtocolJson.Deserialize<ErrorMessage>(json);

                        if (error.IsFatal)
                        {
                            throw CreateGatewayException(error);
                        }

                        break;
                    }

                    case RemoteMessageTypes.SessionEnded:
                    {
                        SessionEndedMessage ended =
                            ProtocolJson.Deserialize<SessionEndedMessage>(json);

                        ValidateSessionId(ended.SessionId);
                        _sessionEnded?.TrySetResult(ended);
                        return;
                    }

                    default:
                        throw new InvalidOperationException(
                            $"Remote GPU gateway sent unexpected message " +
                            $"type '{messageType}'.");
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _terminalException = exception;
            _sessionEnded?.TrySetException(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private void HandleCaption(CaptionMessage caption)
    {
        ValidateSessionId(caption.SessionId);

        TimeSpan? startTime = caption.StartTimeMilliseconds >= 0
            ? TimeSpan.FromMilliseconds(caption.StartTimeMilliseconds)
            : null;

        TimeSpan? endTime = caption.EndTimeMilliseconds >= 0
            ? TimeSpan.FromMilliseconds(caption.EndTimeMilliseconds)
            : null;

        var result = new TranscriptResult(
            SegmentId: caption.SegmentId,
            Sequence: caption.Sequence,
            Text: caption.Text,
            IsFinal: caption.IsFinal,
            Stability: null,
            ResultEndTime: endTime)
        {
            ResultStartTime = startTime,
            ProviderProcessingMilliseconds =
                caption.Latency?.EngineProcessingMilliseconds,
        };

        ResultReceived?.Invoke(
            this,
            new TranscriptResultEventArgs(result));
    }

    private async Task SendControlMessageAsync(
        ClientWebSocket socket,
        IRemoteProtocolMessage message,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);

        try
        {
            await SendMessageAsync(
                socket,
                message,
                cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task SendMessageAsync(
        ClientWebSocket socket,
        IRemoteProtocolMessage message,
        CancellationToken cancellationToken)
    {
        byte[] json = ProtocolJson.Serialize(message);

        await socket.SendAsync(
            new ArraySegment<byte>(json),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task<byte[]> ReceiveTextMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();

        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException(
                    "Remote transcription gateway is not connected. " +
                    $"Close status: {result.CloseStatus}; " +
                    $"{result.CloseStatusDescription}");
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException(
                    "Remote GPU gateway sent a non-text control message.");
            }

            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return message.ToArray();
    }

    private void ValidateSessionId(string sessionId)
    {
        if (!string.Equals(
                sessionId,
                _sessionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Remote GPU gateway returned a different session ID.");
        }
    }

    private static void ValidateSessionStarted(
        SessionStartedMessage started,
        string requestId)
    {
        if (!string.Equals(
                started.RequestId,
                requestId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Remote GPU gateway returned a different request ID.");
        }

        if (started.ProtocolVersion != RemoteProtocol.CurrentVersion ||
            started.AcceptedAudio.Encoding !=
                RemoteProtocol.RequiredAudioEncoding ||
            started.AcceptedAudio.SampleRateHz !=
                RemoteProtocol.RequiredSampleRateHz ||
            started.AcceptedAudio.Channels !=
                RemoteProtocol.RequiredChannels ||
            started.AcceptedAudio.BitsPerSample !=
                RemoteProtocol.RequiredBitsPerSample)
        {
            throw new InvalidOperationException(
                "Remote GPU gateway accepted an incompatible protocol " +
                "or audio format.");
        }

        if (string.IsNullOrWhiteSpace(started.SessionId))
        {
            throw new InvalidOperationException(
                "Remote GPU gateway returned an empty session ID.");
        }
    }

    private void MarkDisconnected(Exception exception)
    {
        _terminalException = exception;
        Interlocked.Exchange(ref _isRunning, 0);

        try
        {
            _socket?.Abort();
        }
        catch
        {
        }
    }

    private InvalidOperationException CreateNotConnectedException(
        Exception? innerException = null)
    {
        return new InvalidOperationException(
            "Remote transcription gateway is not connected.",
            innerException ?? _terminalException);
    }

    private static InvalidOperationException CreateGatewayException(
        ErrorMessage error)
    {
        return new InvalidOperationException(
            $"Remote GPU gateway error [{error.Code}]: {error.Message}");
    }

    private async Task CleanupSessionAsync()
    {
        Interlocked.Exchange(ref _isRunning, 0);

        CancellationTokenSource? cancellation =
            _sessionCancellation;
        ClientWebSocket? socket = _socket;
        Task? receiveTask = _receiveTask;

        cancellation?.Cancel();

        try
        {
            socket?.Abort();
        }
        catch
        {
        }

        if (receiveTask is not null)
        {
            try
            {
                await receiveTask;
            }
            catch
            {
            }
        }

        socket?.Dispose();
        cancellation?.Dispose();

        _socket = null;
        _sessionCancellation = null;
        _receiveTask = null;
        _sessionEnded = null;
        _resampler = null;
        _sessionId = null;
        _sourceSampleRate = 0;
        _outgoingSequence = 0;
    }

    private static string GetClientVersion()
    {
        return Assembly.GetEntryAssembly()?
            .GetName()
            .Version?
            .ToString() ?? "1.0.0";
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await StopAsync();
        }
        finally
        {
            _disposed = true;
            _lifecycleGate.Dispose();
            _sendGate.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
