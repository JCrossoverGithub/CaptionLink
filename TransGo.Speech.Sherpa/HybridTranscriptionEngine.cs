using System.Diagnostics;
using System.Threading.Channels;
using TransGo.Core.Audio;
using TransGo.Core.Transcription;

namespace TransGo.Speech.Sherpa;

/// <summary>
/// Displays immediate Sherpa Streaming results, then uses
/// Whisper Turbo to correct each completed utterance.
/// </summary>
public sealed class HybridTranscriptionEngine
    : ITranscriptionEngine
{
    private const int CorrectionQueueCapacity = 8;

    private static readonly TimeSpan MinimumUtteranceDuration =
        TimeSpan.FromMilliseconds(700);

    private readonly object _gate = new();

    private readonly SherpaStreamingTranscriptionEngine
        _streamingEngine = new();

    private readonly HybridUtteranceAudioBuffer
        _utteranceBuffer = new();

    private Channel<CorrectionRequest>? _correctionQueue;
    private CancellationTokenSource? _correctionCancellation;
    private Task? _correctionWorker;
    private WhisperUtteranceDecoder? _whisperDecoder;

    private long _resultSequence;
    private long _tailSegmentSequence;

    private bool _isStarting;
    private bool _isRunning;
    private bool _disposed;

    private sealed record CorrectionRequest(
        TranscriptResult StreamingResult,
        HybridUtteranceAudio Audio);

    public HybridTranscriptionEngine()
    {
        _streamingEngine.ResultReceived +=
            StreamingEngine_ResultReceived;
    }

    public event EventHandler<TranscriptResultEventArgs>?
        ResultReceived;

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
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (_isStarting || _isRunning)
            {
                throw new InvalidOperationException(
                    "Hybrid transcription is already running.");
            }

            _isStarting = true;
        }

        WhisperUtteranceDecoder? decoder = null;
        bool streamingStarted = false;

        try
        {
            /*
             * Load Whisper before accepting audio so the first
             * completed utterance can be corrected immediately.
             */
            decoder =
                await Task.Run(
                    static () =>
                        new WhisperUtteranceDecoder(),
                    cancellationToken);

            var correctionQueue =
                Channel.CreateBounded<CorrectionRequest>(
                    new BoundedChannelOptions(
                        CorrectionQueueCapacity)
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        FullMode =
                            BoundedChannelFullMode.Wait
                    });

            var correctionCancellation =
                new CancellationTokenSource();

            await _streamingEngine.StartAsync(
                configuration,
                cancellationToken);

            streamingStarted = true;

            _utteranceBuffer.Reset();

            Task correctionWorker =
                Task.Run(
                    () => ProcessCorrectionsAsync(
                        correctionQueue.Reader,
                        decoder,
                        correctionCancellation.Token),
                    CancellationToken.None);

            lock (_gate)
            {
                ThrowIfDisposedLocked();

                _correctionQueue =
                    correctionQueue;

                _correctionCancellation =
                    correctionCancellation;

                _correctionWorker =
                    correctionWorker;

                _whisperDecoder =
                    decoder;

                _resultSequence = 0;
                _tailSegmentSequence = 0;

                _isRunning = true;
            }
        }
        catch
        {
            if (streamingStarted)
            {
                await TryStopStreamingAsync();
            }

            decoder?.Dispose();
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

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (!_isRunning)
            {
                throw new InvalidOperationException(
                    "Hybrid transcription is not running.");
            }
        }

        /*
         * Add the audio before sending it to Sherpa. Sherpa may
         * synchronously publish an endpoint while processing
         * this chunk, so the buffer must already contain it.
         */
        _utteranceBuffer.Add(chunk);

        await _streamingEngine.SendAsync(
            chunk,
            cancellationToken);
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        Channel<CorrectionRequest>? correctionQueue;
        CancellationTokenSource? correctionCancellation;
        Task? correctionWorker;
        WhisperUtteranceDecoder? decoder;

        lock (_gate)
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;

            correctionQueue =
                _correctionQueue;

            correctionCancellation =
                _correctionCancellation;

            correctionWorker =
                _correctionWorker;

            decoder =
                _whisperDecoder;
        }

        Exception? failure = null;

        /*
         * Stopping Sherpa may produce one last final endpoint.
         * Keep the correction queue open until that completes.
         */
        try
        {
            await _streamingEngine.StopAsync(
                cancellationToken);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        /*
         * Capture any audio remaining when the user stops in
         * the middle of an utterance.
         */
        HybridUtteranceAudio? remaining =
            _utteranceBuffer.TakeAndReset(
                MinimumUtteranceDuration);

        if (
            remaining is not null &&
            correctionQueue is not null)
        {
            string segmentId =
                $"hybrid-tail-" +
                $"{Interlocked.Increment(ref _tailSegmentSequence):D6}";

            var placeholder =
                new TranscriptResult(
                    SegmentId: segmentId,
                    Sequence: NextSequence(),
                    Text: string.Empty,
                    IsFinal: true,
                    Stability: null,
                    ResultEndTime: null);

            if (!correctionQueue.Writer.TryWrite(
                    new CorrectionRequest(
                        placeholder,
                        remaining)))
            {
                Debug.WriteLine(
                    "The final Whisper correction could not " +
                    "be queued.");
            }
        }

        correctionQueue?.Writer.TryComplete();

        if (correctionWorker is not null)
        {
            try
            {
                await correctionWorker.WaitAsync(
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                correctionCancellation?.Cancel();

                try
                {
                    await correctionWorker;
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

                failure ??=
                    new OperationCanceledException(
                        cancellationToken);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        correctionCancellation?.Cancel();
        correctionCancellation?.Dispose();
        decoder?.Dispose();

        lock (_gate)
        {
            _correctionQueue = null;
            _correctionCancellation = null;
            _correctionWorker = null;
            _whisperDecoder = null;
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private void StreamingEngine_ResultReceived(
        object? sender,
        TranscriptResultEventArgs eventArgs)
    {
        TranscriptResult incoming =
            eventArgs.Result;

        /*
         * Publish the streaming result immediately.
         */
        var streamingResult =
            new TranscriptResult(
                SegmentId:
                    incoming.SegmentId,

                Sequence:
                    NextSequence(),

                Text:
                    incoming.Text,

                IsFinal:
                    incoming.IsFinal,

                Stability:
                    incoming.Stability,

                ResultEndTime:
                    incoming.ResultEndTime);

        PublishResult(streamingResult);

        if (!incoming.IsFinal)
        {
            return;
        }

        /*
         * A final streaming result represents an endpoint.
         * Remove the corresponding audio from the buffer and
         * queue it for one high-accuracy Whisper decode.
         */
        HybridUtteranceAudio? utterance =
            _utteranceBuffer.TakeAndReset(
                MinimumUtteranceDuration);

        if (utterance is null)
        {
            return;
        }

        ChannelWriter<CorrectionRequest>? writer;

        lock (_gate)
        {
            writer =
                _correctionQueue?.Writer;
        }

        if (
            writer is null ||
            !writer.TryWrite(
                new CorrectionRequest(
                    streamingResult,
                    utterance)))
        {
            Debug.WriteLine(
                "Whisper correction queue is full. " +
                "The streaming caption remains visible.");
        }
    }

    private async Task ProcessCorrectionsAsync(
        ChannelReader<CorrectionRequest> reader,
        WhisperUtteranceDecoder decoder,
        CancellationToken cancellationToken)
    {
        await foreach (
            CorrectionRequest request
            in reader.ReadAllAsync(
                cancellationToken))
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            string correctedText;

            try
            {
                correctedText =
                    decoder.Decode(
                        request.Audio);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "Whisper utterance decoding failed: " +
                    exception);

                continue;
            }

            if (string.IsNullOrWhiteSpace(
                    correctedText))
            {
                continue;
            }

            /*
             * Reuse the Sherpa segment ID. The UI can therefore
             * replace the rough final segment with this more
             * accurate Whisper result.
             */
            var correctedResult =
                new TranscriptResult(
                    SegmentId:
                        request.StreamingResult.SegmentId,

                    Sequence:
                        NextSequence(),

                    Text:
                        correctedText,

                    IsFinal:
                        true,

                    Stability:
                        null,

                    ResultEndTime:
                        request.StreamingResult.ResultEndTime);

            PublishResult(correctedResult);
        }
    }

    private long NextSequence()
    {
        return Interlocked.Increment(
            ref _resultSequence);
    }

    private void PublishResult(
        TranscriptResult result)
    {
        EventHandler<TranscriptResultEventArgs>? handlers =
            ResultReceived;

        if (handlers is null)
        {
            return;
        }

        var eventArgs =
            new TranscriptResultEventArgs(result);

        foreach (
            EventHandler<TranscriptResultEventArgs> handler
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
                    "Hybrid transcript subscriber failed: " +
                    exception);
            }
        }
    }

    private async Task TryStopStreamingAsync()
    {
        try
        {
            await _streamingEngine.StopAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                "Streaming cleanup failed: " +
                exception);
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
            _streamingEngine.ResultReceived -=
                StreamingEngine_ResultReceived;

            await _streamingEngine.DisposeAsync();

            _utteranceBuffer.Dispose();

            lock (_gate)
            {
                _disposed = true;
            }

            GC.SuppressFinalize(this);
        }
    }
}