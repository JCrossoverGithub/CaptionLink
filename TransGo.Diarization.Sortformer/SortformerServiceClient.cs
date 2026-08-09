using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TransGo.Core.Audio;

namespace TransGo.Diarization.Sortformer;

internal sealed class SortformerServiceMessageEventArgs
    : EventArgs
{
    public SortformerServiceMessageEventArgs(
        string json)
    {
        Json = string.IsNullOrWhiteSpace(json)
            ? throw new ArgumentException(
                "The service message cannot be empty.",
                nameof(json))
            : json;
    }

    public string Json { get; }
}

/// <summary>
/// Maintains one WebSocket session with the local Sortformer
/// diarization service.
/// </summary>
internal sealed class SortformerServiceClient
    : IAsyncDisposable
{
    private static readonly Uri DefaultServiceUri =
        new("ws://127.0.0.1:8766/stream");

    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate =
        new(1, 1);

    private readonly Uri _serviceUri;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;

    private TaskCompletionSource _stoppedCompletion =
        CreateStoppedCompletion();

    private bool _disposed;

    public SortformerServiceClient()
        : this(DefaultServiceUri)
    {
    }

    internal SortformerServiceClient(
        Uri serviceUri)
    {
        _serviceUri = serviceUri ??
            throw new ArgumentNullException(
                nameof(serviceUri));
    }

    public event EventHandler<
        SortformerServiceMessageEventArgs>?
            MessageReceived;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return
                    !_disposed &&
                    _socket?.State ==
                        WebSocketState.Open;
            }
        }
    }

    public async Task ConnectAsync(
        int sampleRate,
        int maximumSpeakers,
        bool publishSpeakerProbabilities,
        CancellationToken cancellationToken = default)
    {
        ValidateStartConfiguration(
            sampleRate,
            maximumSpeakers);

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (_socket is not null)
            {
                throw new InvalidOperationException(
                    "The Sortformer service client is " +
                    "already connected.");
            }
        }

        var socket =
            new ClientWebSocket();

        try
        {
            await socket.ConnectAsync(
                _serviceUri,
                cancellationToken);

            string connectedJson =
                await ReceiveRequiredTextAsync(
                    socket,
                    cancellationToken);

            EnsureMessageType(
                connectedJson,
                "connected");

            string startJson =
                JsonSerializer.Serialize(
                    new
                    {
                        type = "start",
                        sample_rate = sampleRate,
                        channels = 1,
                        bits_per_sample = 16,
                        maximum_speakers =
                            maximumSpeakers,
                        publish_speaker_probabilities =
                            publishSpeakerProbabilities,
                    });

            await SendTextAsync(
                socket,
                startJson,
                cancellationToken);

            string startedJson =
                await ReceiveRequiredTextAsync(
                    socket,
                    cancellationToken);

            EnsureMessageType(
                startedJson,
                "started");

            var receiveCancellation =
                new CancellationTokenSource();

            TaskCompletionSource
                stoppedCompletion =
                    CreateStoppedCompletion();

            lock (_gate)
            {
                ThrowIfDisposedLocked();

                _socket = socket;
                _receiveCancellation =
                    receiveCancellation;

                _stoppedCompletion =
                    stoppedCompletion;

                _receiveTask =
                    ReceiveLoopAsync(
                        socket,
                        stoppedCompletion,
                        receiveCancellation.Token);
            }

            Debug.WriteLine(
                "Connected to the local Sortformer service.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask SendAudioAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ValidateChunk(chunk);

        ClientWebSocket socket =
            GetConnectedSocket();

        byte[] audioBytes =
            chunk.Data.ToArray();

        await _sendGate.WaitAsync(
            cancellationToken);

        try
        {
            await socket.SendAsync(
                new ArraySegment<byte>(
                    audioBytes),

                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        ClientWebSocket? socket;
        Task? receiveTask;
        CancellationTokenSource? receiveCancellation;
        TaskCompletionSource stoppedCompletion;

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            socket = _socket;
            receiveTask = _receiveTask;
            receiveCancellation =
                _receiveCancellation;

            stoppedCompletion =
                _stoppedCompletion;
        }

        if (socket is null)
        {
            return;
        }

        try
        {
            if (socket.State == WebSocketState.Open)
            {
                string stopJson =
                    JsonSerializer.Serialize(
                        new
                        {
                            type = "stop",
                        });

                await _sendGate.WaitAsync(
                    cancellationToken);

                try
                {
                    await SendTextAsync(
                        socket,
                        stopJson,
                        cancellationToken);
                }
                finally
                {
                    _sendGate.Release();
                }

                await stoppedCompletion.Task.WaitAsync(
                    cancellationToken);
            }

            if (
                socket.State ==
                    WebSocketState.Open ||
                socket.State ==
                    WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "TransGo diarization stopped.",
                        cancellationToken);
                }
                catch (
                    WebSocketException exception)
                {
                    Debug.WriteLine(
                        "Sortformer close handshake failed: " +
                        exception);
                }
            }
        }
        finally
        {
            receiveCancellation?.Cancel();

            if (receiveTask is not null)
            {
                try
                {
                    await receiveTask;
                }
                catch (
                    OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(
                        "Sortformer receive loop ended " +
                        $"with an error: {exception}");
                }
            }

            lock (_gate)
            {
                if (ReferenceEquals(
                    _socket,
                    socket))
                {
                    _socket = null;
                    _receiveTask = null;

                    _receiveCancellation?.Dispose();
                    _receiveCancellation = null;

                    _stoppedCompletion =
                        CreateStoppedCompletion();
                }
            }

            socket.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        TaskCompletionSource stoppedCompletion,
        CancellationToken cancellationToken)
    {
        bool stoppedReceived = false;

        try
        {
            while (
                socket.State ==
                    WebSocketState.Open ||
                socket.State ==
                    WebSocketState.CloseReceived)
            {
                ReceivedMessage message =
                    await ReceiveMessageAsync(
                        socket,
                        cancellationToken);

                if (
                    message.MessageType ==
                        WebSocketMessageType.Close)
                {
                    if (!stoppedReceived)
                    {
                        string closeDetail =
                            string.IsNullOrWhiteSpace(
                                message.CloseStatusDescription)
                                ? message.CloseStatus is null
                                    ? "without a close status"
                                    : $"with close status " +
                                      $"{message.CloseStatus}"
                                : message.CloseStatusDescription;

                        stoppedCompletion.TrySetException(
                            new InvalidOperationException(
                                "The Sortformer service closed " +
                                "before reporting that it stopped: " +
                                closeDetail));
                    }

                    break;
                }

                if (
                    message.MessageType !=
                        WebSocketMessageType.Text ||
                    string.IsNullOrWhiteSpace(
                        message.Text))
                {
                    continue;
                }

                string json =
                    message.Text;

                PublishMessage(json);

                string? messageType =
                    TryGetMessageType(json);

                if (
                    string.Equals(
                        messageType,
                        "stopped",
                        StringComparison.Ordinal))
                {
                    stoppedReceived = true;
                    stoppedCompletion.TrySetResult();
                }
                else if (
                    string.Equals(
                        messageType,
                        "error",
                        StringComparison.Ordinal))
                {
                    string errorMessage =
                        TryGetErrorMessage(json) ??
                        "The Sortformer service reported an error.";

                    stoppedCompletion.TrySetException(
                        new InvalidOperationException(
                            errorMessage));
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            stoppedCompletion.TrySetException(
                exception);

            Debug.WriteLine(
                "The Sortformer receive loop failed: " +
                exception);
        }
        finally
        {
            if (!stoppedReceived)
            {
                stoppedCompletion.TrySetException(
                    new InvalidOperationException(
                        "The Sortformer service disconnected " +
                        "before reporting that it stopped."));
            }
        }
    }

    private void PublishMessage(
        string json)
    {
        Debug.WriteLine(
            $"Sortformer service: {json}");

        EventHandler<
            SortformerServiceMessageEventArgs>?
                handlers =
                    MessageReceived;

        if (handlers is null)
        {
            return;
        }

        var eventArgs =
            new SortformerServiceMessageEventArgs(
                json);

        foreach (
            EventHandler<
                SortformerServiceMessageEventArgs>
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
                    "Sortformer message subscriber failed: " +
                    exception);
            }
        }
    }

    private ClientWebSocket GetConnectedSocket()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (
                _socket is null ||
                _socket.State !=
                    WebSocketState.Open)
            {
                throw new InvalidOperationException(
                    "The Sortformer service is not connected.");
            }

            return _socket;
        }
    }

    private static async Task SendTextAsync(
        ClientWebSocket socket,
        string text,
        CancellationToken cancellationToken)
    {
        byte[] bytes =
            Encoding.UTF8.GetBytes(text);

        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task<string>
        ReceiveRequiredTextAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
    {
        ReceivedMessage message =
            await ReceiveMessageAsync(
                socket,
                cancellationToken);

        if (
            message.MessageType !=
                WebSocketMessageType.Text ||
            string.IsNullOrWhiteSpace(
                message.Text))
        {
            throw new InvalidOperationException(
                "The Sortformer service did not return " +
                "the expected JSON message.");
        }

        return message.Text;
    }

    private static async Task<ReceivedMessage>
        ReceiveMessageAsync(
            ClientWebSocket socket,
            CancellationToken cancellationToken)
    {
        var buffer =
            new byte[16 * 1024];

        using var messageBuffer =
            new MemoryStream();

        WebSocketMessageType? messageType =
            null;

        while (true)
        {
            WebSocketReceiveResult result =
                await socket.ReceiveAsync(
                    new ArraySegment<byte>(
                        buffer),

                    cancellationToken);

            messageType ??=
                result.MessageType;

            if (
                result.MessageType ==
                    WebSocketMessageType.Close)
            {
                return new ReceivedMessage(
                    WebSocketMessageType.Close,
                    null,
                    result.CloseStatus,
                    result.CloseStatusDescription);
            }

            if (
                result.MessageType !=
                    messageType.Value)
            {
                throw new InvalidOperationException(
                    "The Sortformer service changed message " +
                    "type within one WebSocket message.");
            }

            messageBuffer.Write(
                buffer,
                0,
                result.Count);

            if (result.EndOfMessage)
            {
                break;
            }
        }

        string? text =
            messageType ==
                WebSocketMessageType.Text
                ? Encoding.UTF8.GetString(
                    messageBuffer.ToArray())
                : null;

        return new ReceivedMessage(
            messageType.Value,
            text,
            null,
            null);
    }

    private static void EnsureMessageType(
        string json,
        string expectedType)
    {
        string? actualType =
            TryGetMessageType(json);

        if (
            !string.Equals(
                actualType,
                expectedType,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Expected Sortformer service message " +
                $"'{expectedType}', but received " +
                $"'{actualType ?? "unknown"}'.");
        }
    }

    private static string? TryGetMessageType(
        string json)
    {
        using JsonDocument document =
            JsonDocument.Parse(json);

        return
            document.RootElement.TryGetProperty(
                "type",
                out JsonElement typeElement)
            &&
            typeElement.ValueKind ==
                JsonValueKind.String
                ? typeElement.GetString()
                : null;
    }

    private static string? TryGetErrorMessage(
        string json)
    {
        using JsonDocument document =
            JsonDocument.Parse(json);

        return
            document.RootElement.TryGetProperty(
                "message",
                out JsonElement messageElement)
            &&
            messageElement.ValueKind ==
                JsonValueKind.String
                ? messageElement.GetString()
                : null;
    }

    private static void ValidateStartConfiguration(
        int sampleRate,
        int maximumSpeakers)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                "The sample rate must be positive.");
        }

        if (
            maximumSpeakers <= 0 ||
            maximumSpeakers > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSpeakers),
                "Sortformer supports between one " +
                "and four speakers.");
        }
    }

    private static void ValidateChunk(
        TranscriptionAudioChunk chunk)
    {
        if (chunk.SampleRate <= 0)
        {
            throw new ArgumentException(
                "The audio sample rate must be positive.",
                nameof(chunk));
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

    private static TaskCompletionSource
        CreateStoppedCompletion()
    {
        return new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
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

            _sendGate.Dispose();

            GC.SuppressFinalize(this);
        }
    }

    private sealed record ReceivedMessage(
        WebSocketMessageType MessageType,
        string? Text,
        WebSocketCloseStatus? CloseStatus,
        string? CloseStatusDescription);
}
