using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TransGo.Core.Audio;

namespace TransGo.Speech.Parakeet;

/// <summary>
/// Sends TransGo PCM16 audio to the local Parakeet service
/// and receives JSON status or transcript messages.
/// </summary>
public sealed class ParakeetServiceClient
    : IAsyncDisposable
{
    private static readonly Uri DefaultServiceUri =
        new("ws://localhost:8765/stream");

    private readonly object _gate = new();

    /*
     * ClientWebSocket supports one send operation at a time.
     * This semaphore prevents overlapping control/audio sends.
     */
    private readonly SemaphoreSlim _sendGate =
        new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveTask;

    private bool _disposed;

    public event EventHandler<ParakeetServiceMessageEventArgs>?
        MessageReceived;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _socket?.State ==
                    WebSocketState.Open;
            }
        }
    }

    public async Task ConnectAsync(
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }

        ClientWebSocket socket;
        CancellationTokenSource sessionCancellation;

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (_socket is not null)
            {
                throw new InvalidOperationException(
                    "The Parakeet service client is already active.");
            }

            socket =
                new ClientWebSocket();

            sessionCancellation =
                new CancellationTokenSource();

            _socket = socket;
            _sessionCancellation =
                sessionCancellation;
        }

        try
        {
            await socket.ConnectAsync(
                DefaultServiceUri,
                cancellationToken);

            Task receiveTask =
                ReceiveLoopAsync(
                    socket,
                    sessionCancellation.Token);

            lock (_gate)
            {
                _receiveTask =
                    receiveTask;
            }

            await SendControlMessageAsync(
                new
                {
                    type = "start",
                    sample_rate = sampleRate,
                    channels = 1,
                    bits_per_sample = 16
                },
                cancellationToken);
        }
        catch
        {
            sessionCancellation.Cancel();
            sessionCancellation.Dispose();
            socket.Dispose();

            lock (_gate)
            {
                _socket = null;
                _sessionCancellation = null;
                _receiveTask = null;
            }

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

        await SendFrameAsync(
            socket,
            chunk.Data,
            WebSocketMessageType.Binary,
            cancellationToken);
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        ClientWebSocket? socket;
        CancellationTokenSource? sessionCancellation;
        Task? receiveTask;

        lock (_gate)
        {
            socket = _socket;
            sessionCancellation =
                _sessionCancellation;
            receiveTask = _receiveTask;
        }

        if (socket is null)
        {
            return;
        }

        try
        {
            if (socket.State ==
                WebSocketState.Open)
            {
                await SendControlMessageAsync(
                    new
                    {
                        type = "stop"
                    },
                    cancellationToken);
            }

            if (receiveTask is not null)
            {
                await receiveTask.WaitAsync(
                    cancellationToken);
            }
        }
        finally
        {
            sessionCancellation?.Cancel();

            if (
                socket.State == WebSocketState.Open ||
                socket.State ==
                    WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "TransGo session stopped.",
                        CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // The server may already have closed.
                }
            }

            sessionCancellation?.Dispose();
            socket.Dispose();

            lock (_gate)
            {
                if (ReferenceEquals(
                        _socket,
                        socket))
                {
                    _socket = null;
                    _sessionCancellation = null;
                    _receiveTask = null;
                }
            }
        }
    }

    private async Task SendControlMessageAsync(
        object message,
        CancellationToken cancellationToken)
    {
        byte[] json =
            JsonSerializer.SerializeToUtf8Bytes(
                message);

        ClientWebSocket socket =
            GetConnectedSocket();

        await SendFrameAsync(
            socket,
            json,
            WebSocketMessageType.Text,
            cancellationToken);
    }

    private async Task SendFrameAsync(
        ClientWebSocket socket,
        ReadOnlyMemory<byte> data,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(
            cancellationToken);

        try
        {
            if (socket.State !=
                WebSocketState.Open)
            {
                throw new InvalidOperationException(
                    "The Parakeet WebSocket is not open.");
            }

            await socket.SendAsync(
                data,
                messageType,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var receiveBuffer =
            new byte[16 * 1024];

        try
        {
            while (
                socket.State ==
                WebSocketState.Open)
            {
                using var messageBuffer =
                    new MemoryStream();

                WebSocketReceiveResult result;

                do
                {
                    result =
                        await socket.ReceiveAsync(
                            new ArraySegment<byte>(
                                receiveBuffer),
                            cancellationToken);

                    if (
                        result.MessageType ==
                        WebSocketMessageType.Close)
                    {
                        return;
                    }

                    messageBuffer.Write(
                        receiveBuffer,
                        0,
                        result.Count);
                }
                while (!result.EndOfMessage);

                if (
                    result.MessageType !=
                    WebSocketMessageType.Text)
                {
                    continue;
                }

                string json =
                    Encoding.UTF8.GetString(
                        messageBuffer.ToArray());

                PublishMessage(json);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (WebSocketException exception)
        {
            Debug.WriteLine(
                "Parakeet WebSocket receive failed: " +
                exception);
        }
    }

    private void PublishMessage(
        string json)
    {
        Debug.WriteLine(
            $"Parakeet service: {json}");

        EventHandler<ParakeetServiceMessageEventArgs>?
            handlers =
                MessageReceived;

        if (handlers is null)
        {
            return;
        }

        var eventArgs =
            new ParakeetServiceMessageEventArgs(
                json);

        foreach (
            EventHandler<ParakeetServiceMessageEventArgs>
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
                    "Parakeet message subscriber failed: " +
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
                    "The Parakeet service is not connected.");
            }

            return _socket;
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
                "Parakeet requires mono PCM16 audio.",
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

            _sendGate.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}