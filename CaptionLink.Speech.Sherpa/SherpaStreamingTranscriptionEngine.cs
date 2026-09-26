using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using SherpaOnnx;
using CaptionLink.Core.Audio;
using CaptionLink.Core.Transcription;

namespace CaptionLink.Speech.Sherpa;

public sealed class SherpaStreamingTranscriptionEngine
    : ITranscriptionEngine
{
    /*
     * Each CaptionLink chunk is approximately 100 ms.
     * A capacity of 100 permits about 10 seconds of buffering
     * during a temporary CPU slowdown.
     */
    private const int QueueCapacity = 100;

    private readonly object _gate = new();

    private Channel<TranscriptionAudioChunk>? _audioQueue;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _workerTask;

    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;

    private int? _configuredSampleRate;

    private long _resultSequence;
    private long _segmentSequence;

    private bool _disposed;

    public event EventHandler<TranscriptResultEventArgs>?
        ResultReceived;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _workerTask is
                {
                    IsCompleted: false
                };
            }
        }
    }

    public async Task StartAsync(
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ValidateConfiguration(configuration);

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (_workerTask is not null)
            {
                throw new InvalidOperationException(
                    "Sherpa transcription is already running.");
            }
        }

        /*
         * Loading the ONNX models can take noticeable time.
         * Perform that work away from the WPF UI thread.
         */
        OnlineRecognizer recognizer =
            await Task.Run(
                () =>
                    SherpaRecognizerFactory
                        .CreateDefaultEnglish(),
                cancellationToken);

        OnlineStream stream;

        try
        {
            stream =
                recognizer.CreateStream();
        }
        catch
        {
            DisposeIfSupported(recognizer);
            throw;
        }

        Channel<TranscriptionAudioChunk> audioQueue =
            Channel.CreateBounded<TranscriptionAudioChunk>(
                new BoundedChannelOptions(QueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait
                });

        var sessionCancellation =
            new CancellationTokenSource();

        lock (_gate)
        {
            try
            {
                ThrowIfDisposedLocked();

                if (_workerTask is not null)
                {
                    throw new InvalidOperationException(
                        "Sherpa transcription was started " +
                        "by another operation.");
                }

                _recognizer = recognizer;
                _stream = stream;
                _audioQueue = audioQueue;
                _sessionCancellation =
                    sessionCancellation;

                _configuredSampleRate =
                    configuration.SampleRate;

                _resultSequence = 0;
                _segmentSequence = 1;

                _workerTask =
                    Task.Run(
                        () => ProcessAudioAsync(
                            audioQueue,
                            recognizer,
                            stream,
                            configuration.SampleRate,
                            configuration.EnableInterimResults,
                            sessionCancellation.Token),
                        CancellationToken.None);
            }
            catch
            {
                sessionCancellation.Dispose();
                DisposeIfSupported(stream);
                DisposeIfSupported(recognizer);

                throw;
            }
        }
    }

    public ValueTask SendAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        ChannelWriter<TranscriptionAudioChunk> writer;
        int configuredSampleRate;

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (
                _audioQueue is null ||
                _configuredSampleRate is null ||
                _workerTask is null ||
                _workerTask.IsCompleted)
            {
                throw new InvalidOperationException(
                    "Sherpa transcription is not running.");
            }

            writer =
                _audioQueue.Writer;

            configuredSampleRate =
                _configuredSampleRate.Value;
        }

        ValidateChunk(
            chunk,
            configuredSampleRate);

        /*
         * Queue the audio instead of decoding it on the
         * WASAPI capture callback thread.
         */
        return writer.WriteAsync(
            chunk,
            cancellationToken);
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        Channel<TranscriptionAudioChunk>? audioQueue;
        CancellationTokenSource? sessionCancellation;
        Task? workerTask;

        OnlineRecognizer? recognizer;
        OnlineStream? stream;

        lock (_gate)
        {
            audioQueue = _audioQueue;
            sessionCancellation =
                _sessionCancellation;

            workerTask = _workerTask;
            recognizer = _recognizer;
            stream = _stream;

            if (workerTask is null)
            {
                return;
            }
        }

        Exception? failure = null;
        bool cancellationRequested = false;

        try
        {
            /*
             * Stop accepting new chunks while allowing the
             * recognition worker to process queued audio.
             */
            audioQueue?.Writer.TryComplete();

            try
            {
                await workerTask.WaitAsync(
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                cancellationRequested = true;

                sessionCancellation?.Cancel();

                /*
                 * Wait for the worker to exit before releasing
                 * Sherpa's native recognizer and stream.
                 */
                try
                {
                    await workerTask;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }
        finally
        {
            sessionCancellation?.Cancel();

            DisposeIfSupported(stream);
            DisposeIfSupported(recognizer);

            sessionCancellation?.Dispose();

            lock (_gate)
            {
                if (ReferenceEquals(
                        _workerTask,
                        workerTask))
                {
                    ClearSessionStateLocked();
                }
            }
        }

        if (cancellationRequested)
        {
            throw new OperationCanceledException(
                cancellationToken);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }
    }

    private async Task ProcessAudioAsync(
        Channel<TranscriptionAudioChunk> audioQueue,
        OnlineRecognizer recognizer,
        OnlineStream stream,
        int configuredSampleRate,
        bool enableInterimResults,
        CancellationToken cancellationToken)
    {
        string lastInterimText =
            string.Empty;

        try
        {
            await foreach (
                TranscriptionAudioChunk chunk
                in audioQueue.Reader.ReadAllAsync(
                    cancellationToken))
            {
                float[] samples =
                    ConvertPcm16ToFloat(
                        chunk.Data.Span);

                stream.AcceptWaveform(
                    chunk.SampleRate,
                    samples);

                DecodeAvailableAudio(
                    recognizer,
                    stream,
                    enableInterimResults,
                    ref lastInterimText);
            }

            /*
             * Add a short period of silence and mark the stream
             * complete so Sherpa can flush its remaining text.
             */
            var tailPadding =
                new float[
                    Math.Max(
                        1,
                        (int)(configuredSampleRate * 0.6))];

            stream.AcceptWaveform(
                configuredSampleRate,
                tailPadding);

            stream.InputFinished();

            while (recognizer.IsReady(stream))
            {
                recognizer.Decode(stream);
            }

            string finalText =
                recognizer
                    .GetResult(stream)
                    .Text
                    .Trim();

            if (!string.IsNullOrWhiteSpace(
                    finalText))
            {
                PublishResult(
                    finalText,
                    isFinal: true);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Normal cancellation during shutdown.
        }
        catch (Exception exception)
        {
            audioQueue.Writer.TryComplete(
                exception);

            throw;
        }
    }

    private void DecodeAvailableAudio(
        OnlineRecognizer recognizer,
        OnlineStream stream,
        bool enableInterimResults,
        ref string lastInterimText)
    {
        while (recognizer.IsReady(stream))
        {
            recognizer.Decode(stream);
        }

        string text =
            recognizer
                .GetResult(stream)
                .Text
                .Trim();

        if (
            enableInterimResults &&
            !string.IsNullOrWhiteSpace(text) &&
            !string.Equals(
                text,
                lastInterimText,
                StringComparison.Ordinal))
        {
            lastInterimText = text;

            PublishResult(
                text,
                isFinal: false);
        }

        if (!recognizer.IsEndpoint(stream))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            PublishResult(
                text,
                isFinal: true);
        }

        recognizer.Reset(stream);

        lastInterimText =
            string.Empty;
    }

    private static float[] ConvertPcm16ToFloat(
        ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length % sizeof(short) != 0)
        {
            throw new ArgumentException(
                "PCM16 audio must contain complete samples.",
                nameof(pcm16));
        }

        var samples =
            new float[
                pcm16.Length /
                sizeof(short)];

        for (
            int sampleIndex = 0;
            sampleIndex < samples.Length;
            sampleIndex++)
        {
            int byteOffset =
                sampleIndex * sizeof(short);

            short sample =
                BinaryPrimitives
                    .ReadInt16LittleEndian(
                        pcm16.Slice(
                            byteOffset,
                            sizeof(short)));

            samples[sampleIndex] =
                sample / 32768.0F;
        }

        return samples;
    }

    private void PublishResult(
        string text,
        bool isFinal)
    {
        long sequence =
            Interlocked.Increment(
                ref _resultSequence);

        long segmentNumber =
            Interlocked.Read(
                ref _segmentSequence);

        var result =
            new TranscriptResult(
                SegmentId:
                    $"segment-{segmentNumber:D6}",

                Sequence:
                    sequence,

                Text:
                    text,

                IsFinal:
                    isFinal,

                Stability:
                    null,

                ResultEndTime:
                    null);

        EventHandler<TranscriptResultEventArgs>? handlers =
            ResultReceived;

        if (handlers is not null)
        {
            var eventArgs =
                new TranscriptResultEventArgs(
                    result);

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
                    /*
                     * A UI error must not terminate local
                     * recognition.
                     */
                    Debug.WriteLine(
                        "Sherpa transcript subscriber " +
                        $"failed: {exception}");
                }
            }
        }

        if (isFinal)
        {
            Interlocked.Increment(
                ref _segmentSequence);
        }
    }

    private static void ValidateConfiguration(
        TranscriptionConfiguration configuration)
    {
        if (
            string.IsNullOrWhiteSpace(
                configuration.LanguageCode))
        {
            throw new ArgumentException(
                "A language code is required.",
                nameof(configuration));
        }

        if (
            !configuration.LanguageCode.StartsWith(
                "en",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "The currently installed Sherpa model " +
                "supports English transcription only.");
        }

        if (configuration.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "The sample rate must be positive.");
        }
    }

    private static void ValidateChunk(
        TranscriptionAudioChunk chunk,
        int configuredSampleRate)
    {
        if (
            chunk.SampleRate !=
            configuredSampleRate)
        {
            throw new ArgumentException(
                "The chunk sample rate does not match " +
                "the active Sherpa configuration.",
                nameof(chunk));
        }

        if (
            chunk.Channels != 1 ||
            chunk.BitsPerSample != 16 ||
            chunk.Encoding !=
                AudioSampleEncoding.PcmInteger)
        {
            throw new ArgumentException(
                "Sherpa requires mono PCM16 chunks.",
                nameof(chunk));
        }

        if (chunk.Data.IsEmpty)
        {
            throw new ArgumentException(
                "The audio chunk cannot be empty.",
                nameof(chunk));
        }
    }

    private static void DisposeIfSupported(
        object? value)
    {
        if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void ClearSessionStateLocked()
    {
        _audioQueue = null;
        _sessionCancellation = null;
        _workerTask = null;

        _recognizer = null;
        _stream = null;

        _configuredSampleRate = null;
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

            GC.SuppressFinalize(this);
        }
    }
}