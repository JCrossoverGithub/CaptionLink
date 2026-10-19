using System.Buffers.Binary;
using CaptionLink.Core.Audio;
using CaptionLink.Core.Diarization;
using CaptionLink.Core.Transcription;
using CaptionLink.Speech.GPULink;
using GPULink.Client;

namespace CaptionLink.Speech.GPULink.Provider;

/// <summary>Isolates the GPULink native network client from workload-specific caption handling.</summary>
public interface IGPULinkMultitalkerChannel : IAsyncDisposable
{
    Task SendTextAsync(string message, CancellationToken cancellationToken = default);
    Task SendBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
    Task<GPULinkMessage> ReceiveAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
}

public interface IGPULinkMultitalkerConnector
{
    Task<IGPULinkMultitalkerChannel> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Production connector: the reusable GPULink SDK owns HTTP and authenticated v2 relay.</summary>
public sealed class NativeMultitalkerConnector : IGPULinkMultitalkerConnector
{
    private readonly GPULinkClient _client;
    private readonly GPULinkSessionRequest _request;

    public NativeMultitalkerConnector(GPULinkClient client, string projectId,
        int minVramMiB = 0, string adapterId = "captionlink.multitalker.parakeet.v0")
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _request = new GPULinkSessionRequest(projectId, "speech.streaming.multitalker", adapterId, minVramMiB);
    }

    public async Task<IGPULinkMultitalkerChannel> OpenAsync(CancellationToken cancellationToken = default)
        => new NativeChannel(await _client.OpenSessionAsync(_request, ct: cancellationToken).ConfigureAwait(false));

    private sealed class NativeChannel(GPULinkSession session) : IGPULinkMultitalkerChannel
    {
        public Task SendTextAsync(string message, CancellationToken ct = default) => session.SendTextAsync(message, ct);
        public Task SendBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default) => session.SendBinaryAsync(message, ct);
        public Task<GPULinkMessage> ReceiveAsync(CancellationToken ct = default) => session.ReceiveAsync(ct);
        public Task CloseAsync() => session.CloseAsync();
        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}

/// <summary>
/// CaptionLink transcript provider using the independently tested GPULink client and Multitalker wire.
/// Currently accepts already-normalized 16 kHz mono PCM16. The Windows 48->16 kHz
/// resampling/composition step is intentionally not included in this candidate.
/// </summary>
public sealed class GPULinkMultitalkerTranscriptionEngine : ITranscriptionEngine, ISpeakerActivitySource
{
    private const int MaxPcmChunkBytes = 6_400; // 200 ms of 16kHz PCM16
    private static readonly TimeSpan ModelReadyTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(60);
    private readonly IGPULinkMultitalkerConnector _connector;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _send = new(1, 1);
    private IGPULinkMultitalkerChannel? _channel;
    private MultitalkerEventTracker? _events;
    private Task? _receiveTask;
    private TaskCompletionSource<bool>? _completion;
    private CancellationTokenSource? _receiveCancellation;
    private Exception? _terminalError;
    private uint _nextSequence;
    private uint _nextSample;
    private int _running;
    private bool _stopping;
    private bool _disposed;

    public GPULinkMultitalkerTranscriptionEngine(IGPULinkMultitalkerConnector connector)
        => _connector = connector ?? throw new ArgumentNullException(nameof(connector));

    public event EventHandler<TranscriptResultEventArgs>? ResultReceived;
    public event EventHandler<SpeakerActivityEventArgs>? ActivityReceived;
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public bool CompletedSuccessfully => _events?.IsCompleted == true;

    public async Task StartAsync(TranscriptionConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SampleRate != MultitalkerWire.SampleRate)
            throw new NotSupportedException("GPULink Multitalker currently requires normalized 16 kHz PCM16 mono input.");
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_channel is not null) throw new InvalidOperationException("GPULink Multitalker is already started.");
            IGPULinkMultitalkerChannel channel = await _connector.OpenAsync(cancellationToken).ConfigureAwait(false);
            var events = new MultitalkerEventTracker();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(ModelReadyTimeout);
                await channel.SendTextAsync(MultitalkerWire.StartJson, deadline.Token).ConfigureAwait(false);
                GPULinkMessage first = await channel.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (first.IsBinary || events.Accept(first.Text).Kind != MultitalkerEventKind.Ready)
                    throw new InvalidDataException("Expected Multitalker model readiness, not relay readiness or output.");
                _events = events;
                _channel = channel;
                _nextSample = 0;
                _nextSequence = 0;
                _terminalError = null;
                _stopping = false;
                _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _receiveCancellation = new CancellationTokenSource();
                Volatile.Write(ref _running, 1);
                _receiveTask = PumpAsync(channel, events, _completion, _receiveCancellation.Token);
            }
            catch
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask SendAsync(TranscriptionAudioChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.SampleRate != MultitalkerWire.SampleRate || chunk.Data.Length == 0 || (chunk.Data.Length & 1) != 0)
            throw new ArgumentException("Input must be nonempty, 16 kHz mono PCM16 with complete samples.", nameof(chunk));
        // No unbounded pending send queue: concurrent producers are rejected.
        if (!await _send.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Concurrent audio sends are not supported.");
        try
        {
            var channel = _channel;
            if (channel is null || !IsRunning || _stopping)
                throw new InvalidOperationException("GPULink Multitalker session is not accepting audio.", _terminalError);
            uint samples = checked((uint)(chunk.Data.Length / 2));
            if (samples > MultitalkerWire.MaxSamples - _nextSample)
                throw new InvalidOperationException("600-second Multitalker session limit exceeded.");
            for (int pos = 0; pos < chunk.Data.Length; pos += MaxPcmChunkBytes)
            {
                int length = Math.Min(MaxPcmChunkBytes, chunk.Data.Length - pos);
                byte[] frame = MultitalkerWire.EncodePcm(_nextSequence, _nextSample, chunk.Data.Span.Slice(pos, length));
                await channel.SendBinaryAsync(frame, cancellationToken).ConfigureAwait(false);
                _nextSequence++;
                _nextSample += (uint)(length / 2);
            }
        }
        finally { _send.Release(); }
    }

    /// <summary>Normal Stop requires a real END/completion ACK. Disconnect is never success.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = _channel;
            if (channel is null) return;
            _stopping = true;
            try
            {
                if (_terminalError is not null) throw new IOException("GPULink Multitalker failed before completion.", _terminalError);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(CompletionTimeout);
                await _send.WaitAsync(deadline.Token).ConfigureAwait(false);
                try { await channel.SendTextAsync(MultitalkerWire.EndJson, deadline.Token).ConfigureAwait(false); }
                finally { _send.Release(); }
                if (_completion is null) throw new InvalidOperationException("Missing completion tracker.");
                await _completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                if (_events?.IsCompleted != true)
                    throw new IOException("GPULink closed without authoritative workload completion.");
            }
            finally { await CleanupAsync().ConfigureAwait(false); }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task PumpAsync(IGPULinkMultitalkerChannel channel, MultitalkerEventTracker events,
        TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                GPULinkMessage message = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (message.IsBinary) throw new InvalidDataException("Unexpected binary Multitalker response.");
                MultitalkerEvent accepted = events.Accept(message.Text);
                switch (accepted.Kind)
                {
                    case MultitalkerEventKind.Transcript:
                        ResultReceived?.Invoke(this, new TranscriptResultEventArgs(accepted.Transcript!));
                        break;
                    case MultitalkerEventKind.SpeakerActivity:
                        ActivityReceived?.Invoke(this, new SpeakerActivityEventArgs(accepted.Activity!));
                        break;
                    case MultitalkerEventKind.Completed:
                        Volatile.Write(ref _running, 0);
                        completion.TrySetResult(true);
                        return;
                    default:
                        throw new InvalidDataException("Unexpected model readiness event after startup.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _terminalError = exception;
            Volatile.Write(ref _running, 0);
            completion.TrySetException(exception);
            try { await channel.CloseAsync().ConfigureAwait(false); } catch { /* cleanup attempted */ }
        }
    }

    /// <summary>Disconnect/cancel path: never sends END or claims completion.</summary>
    private async Task CleanupAsync()
    {
        var channel = _channel;
        _channel = null;
        Volatile.Write(ref _running, 0);
        _receiveCancellation?.Cancel();
        if (channel is not null)
        {
            try { await channel.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                if (_receiveTask is not null)
                {
                    try { await _receiveTask.ConfigureAwait(false); }
                    catch { /* terminal exception held for caller */ }
                }
            }
        }
        _receiveCancellation?.Dispose();
        _receiveCancellation = null;
        _receiveTask = null;
        _completion = null;
        _stopping = false;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            // Window shutdown cancels without inferring a completed workload.
            await CleanupAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
            // Do not dispose the semaphores: concurrent callers may still be exiting.
        }
    }
}
