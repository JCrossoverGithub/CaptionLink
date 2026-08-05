using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using SherpaOnnx;
using TransGo.Core.Audio;
using TransGo.Core.Transcription;

namespace TransGo.Speech.Sherpa;

public sealed class WhisperTurboTranscriptionEngine
    : ITranscriptionEngine
{
    private const int QueueCapacity = 100;

    private static readonly TimeSpan WindowDuration =
        TimeSpan.FromSeconds(4);

    private static readonly TimeSpan HopDuration =
        TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MinimumFinalDuration =
        TimeSpan.FromSeconds(1);

    private readonly object _gate = new();

    private Channel<TranscriptionAudioChunk>? _audioQueue;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _workerTask;
    private OfflineRecognizer? _recognizer;

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
                    "Whisper Turbo transcription is already running.");
            }
        }

        /*
         * Model loading takes a few seconds, so keep it off
         * the WPF UI thread.
         */
        OfflineRecognizer recognizer =
            await Task.Run(
                SherpaWhisperRecognizerFactory.CreateEnglishCpu,
                cancellationToken);

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
                        "Whisper Turbo was started by another operation.");
                }

                _recognizer = recognizer;
                _audioQueue = audioQueue;
                _sessionCancellation = sessionCancellation;

                _configuredSampleRate =
                    configuration.SampleRate;

                _resultSequence = 0;
                _segmentSequence = 1;

                _workerTask =
                    Task.Run(
                        () => ProcessAudioAsync(
                            audioQueue.Reader,
                            recognizer,
                            configuration.SampleRate,
                            configuration.EnableInterimResults,
                            sessionCancellation.Token),
                        CancellationToken.None);
            }
            catch
            {
                sessionCancellation.Dispose();
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
                    "Whisper Turbo transcription is not running.");
            }

            writer = _audioQueue.Writer;
            configuredSampleRate =
                _configuredSampleRate.Value;
        }

        ValidateChunk(
            chunk,
            configuredSampleRate);

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
        OfflineRecognizer? recognizer;

        lock (_gate)
        {
            audioQueue = _audioQueue;
            sessionCancellation = _sessionCancellation;
            workerTask = _workerTask;
            recognizer = _recognizer;

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
             * Stop accepting new audio and allow the worker
             * to process everything already queued.
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
        ChannelReader<TranscriptionAudioChunk> reader,
        OfflineRecognizer recognizer,
        int sampleRate,
        bool enableInterimResults,
        CancellationToken cancellationToken)
    {
        var rollingBuffer =
            new WhisperRollingAudioBuffer(
                sampleRate,
                WindowDuration,
                HopDuration);

        string currentHypothesis =
            string.Empty;

        string lastPublishedInterim =
            string.Empty;

        await foreach (
            TranscriptionAudioChunk chunk
            in reader.ReadAllAsync(cancellationToken))
        {
            IReadOnlyList<float[]> windows =
                rollingBuffer.AddPcm16(
                    chunk.Data.Span);

            foreach (float[] window in windows)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                string text =
                    DecodeWindow(
                        recognizer,
                        sampleRate,
                        window);

                ProcessDecodedWindow(
                    text,
                    enableInterimResults,
                    ref currentHypothesis,
                    ref lastPublishedInterim);
            }
        }

        /*
         * Decode the remaining tail when listening stops.
         */
        float[] remaining =
            rollingBuffer.DrainRemaining(
                MinimumFinalDuration);

        if (remaining.Length > 0)
        {
            string text =
                DecodeWindow(
                    recognizer,
                    sampleRate,
                    remaining);

            ProcessDecodedWindow(
                text,
                enableInterimResults,
                ref currentHypothesis,
                ref lastPublishedInterim);
        }

        if (!string.IsNullOrWhiteSpace(
                currentHypothesis))
        {
            PublishResult(
                currentHypothesis,
                isFinal: true);
        }
    }

    private static string DecodeWindow(
        OfflineRecognizer recognizer,
        int sampleRate,
        float[] samples)
    {
        OfflineStream stream =
            recognizer.CreateStream();

        try
        {
            stream.AcceptWaveform(
                sampleRate,
                samples);

            recognizer.Decode(
                new List<OfflineStream>
                {
                    stream
                });

            return NormalizeWhitespace(
                stream.Result.Text);
        }
        finally
        {
            DisposeIfSupported(stream);
        }
    }

    private void ProcessDecodedWindow(
        string currentText,
        bool enableInterimResults,
        ref string previousText,
        ref string lastPublishedInterim)
    {
        if (string.IsNullOrWhiteSpace(currentText))
        {
            if (!string.IsNullOrWhiteSpace(previousText))
            {
                PublishResult(
                    previousText,
                    isFinal: true);

                previousText =
                    string.Empty;

                lastPublishedInterim =
                    string.Empty;
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(previousText))
        {
            string stablePrefix =
                ExtractStablePrefix(
                    previousText,
                    currentText);

            if (!string.IsNullOrWhiteSpace(
                    stablePrefix))
            {
                PublishResult(
                    stablePrefix,
                    isFinal: true);

                lastPublishedInterim =
                    string.Empty;
            }
        }

        previousText =
            currentText;

        if (
            enableInterimResults &&
            !string.Equals(
                currentText,
                lastPublishedInterim,
                StringComparison.Ordinal))
        {
            PublishResult(
                currentText,
                isFinal: false);

            lastPublishedInterim =
                currentText;
        }
    }

    private static string ExtractStablePrefix(
        string previousText,
        string currentText)
    {
        string[] previousWords =
            SplitWords(previousText);

        string[] currentWords =
            SplitWords(currentText);

        int overlapLength =
            FindOverlapLength(
                previousWords,
                currentWords);

        /*
         * No shared overlap usually means the previous phrase
         * has ended and the new window contains a new phrase.
         */
        if (overlapLength == 0)
        {
            return previousText;
        }

        int stableWordCount =
            previousWords.Length -
            overlapLength;

        if (stableWordCount <= 0)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            previousWords[..stableWordCount]);
    }

    private static int FindOverlapLength(
        string[] previousWords,
        string[] currentWords)
    {
        int maximumOverlap =
            Math.Min(
                previousWords.Length,
                currentWords.Length);

        /*
         * Require at least two matching words to reduce
         * accidental overlap on common words such as "the".
         */
        for (
            int overlapLength = maximumOverlap;
            overlapLength >= 2;
            overlapLength--)
        {
            bool matches = true;

            for (
                int index = 0;
                index < overlapLength;
                index++)
            {
                string previousWord =
                    NormalizeWord(
                        previousWords[
                            previousWords.Length -
                            overlapLength +
                            index]);

                string currentWord =
                    NormalizeWord(
                        currentWords[index]);

                if (!string.Equals(
                        previousWord,
                        currentWord,
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return overlapLength;
            }
        }

        return 0;
    }

    private static string[] SplitWords(
        string text)
    {
        return text.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
    }

    private static string NormalizeWord(
        string word)
    {
        return new string(
            word
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
    }

    private static string NormalizeWhitespace(
        string text)
    {
        return string.Join(
            " ",
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
    }

    private void PublishResult(
        string text,
        bool isFinal)
    {
        string normalizedText =
            NormalizeWhitespace(text);

        if (string.IsNullOrWhiteSpace(
                normalizedText))
        {
            return;
        }

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
                    normalizedText,

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
                        "Whisper transcript subscriber " +
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
                "The installed Whisper Turbo model is " +
                "currently configured for English.");
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
        if (chunk.SampleRate != configuredSampleRate)
        {
            throw new ArgumentException(
                "The audio chunk sample rate does not match " +
                "the active Whisper configuration.",
                nameof(chunk));
        }

        if (
            chunk.Channels != 1 ||
            chunk.BitsPerSample != 16 ||
            chunk.Encoding !=
                AudioSampleEncoding.PcmInteger)
        {
            throw new ArgumentException(
                "Whisper Turbo requires mono PCM16 audio.",
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