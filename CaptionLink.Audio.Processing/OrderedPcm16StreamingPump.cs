using System.Threading.Channels;
using CaptionLink.Core.Audio;

namespace CaptionLink.Audio.Processing;

/// <summary>
/// Bounded, single-consumer audio path between a synchronous 48 kHz capture
/// callback and an asynchronous 16 kHz transcription sender.
///
/// A full queue fails closed; it never discards samples or accumulates an
/// unbounded backlog. Each instance is single-use and belongs to one capture
/// session. This class does not send END or claim remote completion.
/// </summary>
public sealed class OrderedPcm16StreamingPump : IAsyncDisposable
{
    private const int MaximumInputBytes = 32_000;
    private readonly Channel<TranscriptionAudioChunk> _queue;
    private readonly Func<TranscriptionAudioChunk, CancellationToken, ValueTask> _send;
    private readonly StreamingPcm16Resampler48To16 _resampler = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly Task _consumer;
    private int _state; // 0 = accepting, 1 = draining, 2 = aborted/faulted
    private int _disposed;

    public OrderedPcm16StreamingPump(
        Func<TranscriptionAudioChunk, CancellationToken, ValueTask> send,
        int capacityChunks = 12)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        if (capacityChunks is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(capacityChunks));
        _queue = Channel.CreateBounded<TranscriptionAudioChunk>(
            new BoundedChannelOptions(capacityChunks)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// Enqueues without waiting on network I/O. Caller must catch failures and
    /// stop/cancel the caption session; silent audio drops are prohibited.
    /// Makes an owned copy because capture buffers may be reused after return.
    /// </summary>
    public void Enqueue(TranscriptionAudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.SampleRate != StreamingPcm16Resampler48To16.InputSampleRate ||
            chunk.Data.IsEmpty || (chunk.Data.Length & 1) != 0 ||
            chunk.Data.Length > MaximumInputBytes)
            throw new ArgumentException("Expected bounded 48 kHz mono PCM16.", nameof(chunk));
        if (Volatile.Read(ref _state) != 0 || Volatile.Read(ref _disposed) != 0)
            throw new InvalidOperationException("Audio pump is no longer accepting input.");

        var copied = chunk with { Data = chunk.Data.ToArray() };
        if (_queue.Writer.TryWrite(copied)) return;

        var full = new InvalidOperationException("Audio pump queue exhausted; cancel the caption session rather than dropping audio.");
        Abort(full);
        throw full;
    }

    /// <summary>
    /// Drains accepted audio before caller invokes transcription StopAsync.
    /// Draining is NOT an acknowledgment of remote model completion.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) == 2)
            throw new InvalidOperationException("Audio pump was aborted.");
        _queue.Writer.TryComplete();
        await _consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Aborts immediately; never drains as a success path.</summary>
    public void Abort(Exception? reason = null)
    {
        Interlocked.Exchange(ref _state, 2);
        _queue.Writer.TryComplete(reason);
        _abort.Cancel();
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (TranscriptionAudioChunk incoming in
                _queue.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
            {
                TranscriptionAudioChunk? converted = _resampler.Process(incoming);
                if (converted is not null)
                    await _send(converted, _abort.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            Abort(failure);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Abort();
        try { await _consumer.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested) { }
        catch (Exception) { /* failure remains visible to DrainAsync */ }
        finally { _abort.Dispose(); }
    }
}
