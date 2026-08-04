using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using TransGo.Core.Audio;
using TransGo.Core.Transcription;
using System.Collections.Generic;

using GoogleByteString =
    global::Google.Protobuf.ByteString;

using GoogleRecognitionConfig =
    global::Google.Cloud.Speech.V1.RecognitionConfig;

using GoogleSpeechClient =
    global::Google.Cloud.Speech.V1.SpeechClient;

using GoogleStreamingRecognitionConfig =
    global::Google.Cloud.Speech.V1.StreamingRecognitionConfig;

using GoogleStreamingRecognizeRequest =
    global::Google.Cloud.Speech.V1.StreamingRecognizeRequest;

using GoogleStreamingRecognizeResponse =
    global::Google.Cloud.Speech.V1.StreamingRecognizeResponse;

namespace TransGo.Speech.Google;

public sealed class GoogleStreamingTranscriptionEngine
    : ITranscriptionEngine
{
    /*
     * 200 chunks × 100 ms gives approximately 20 seconds
     * of buffering during a temporary network slowdown.
     */
    private const int QueueCapacity = 200;

    private readonly object _gate = new();
    private readonly GoogleSpeechClient _client;

    private Channel<TranscriptionAudioChunk>? _audioQueue;
    private GoogleSpeechClient.StreamingRecognizeStream? _stream;
    private CancellationTokenSource? _sessionCancellation;

    private Task? _writerTask;
    private Task? _readerTask;

    private int? _configuredSampleRate;

    private long _resultSequence;
    private long _segmentSequence;

    private bool _disposed;

    public GoogleStreamingTranscriptionEngine()
        : this(GoogleSpeechClientFactory.Create())
    {
    }

    internal GoogleStreamingTranscriptionEngine(
        GoogleSpeechClient client)
    {
        _client = client ??
            throw new ArgumentNullException(nameof(client));
    }

    public event EventHandler<TranscriptResultEventArgs>?
        ResultReceived;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _stream is not null;
            }
        }
    }

    public async Task StartAsync(
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ValidateConfiguration(configuration);

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

        GoogleSpeechClient.StreamingRecognizeStream stream;

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (_stream is not null)
            {
                throw new InvalidOperationException(
                    "Google transcription is already running.");
            }

            stream = _client.StreamingRecognize();

            _stream = stream;
            _audioQueue = audioQueue;
            _sessionCancellation = sessionCancellation;
            _configuredSampleRate = configuration.SampleRate;

            _resultSequence = 0;
            _segmentSequence = 1;
        }

        try
        {
            /*
             * The first request must contain configuration
             * and no audio content.
             */
            await stream
                .WriteAsync(CreateConfigurationRequest(configuration))
                .WaitAsync(cancellationToken);

            Task writerTask = WriteAudioAsync(
                audioQueue.Reader,
                stream,
                sessionCancellation.Token);

            Task readerTask = ReadResultsAsync(
                stream,
                sessionCancellation.Token);

            lock (_gate)
            {
                if (ReferenceEquals(_stream, stream))
                {
                    _writerTask = writerTask;
                    _readerTask = readerTask;
                }
            }
        }
        catch
        {
            audioQueue.Writer.TryComplete();
            sessionCancellation.Cancel();
            stream.Dispose();
            sessionCancellation.Dispose();

            lock (_gate)
            {
                if (ReferenceEquals(_stream, stream))
                {
                    ClearSessionStateLocked();
                }
            }

            throw;
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
                _configuredSampleRate is null)
            {
                throw new InvalidOperationException(
                    "Google transcription is not running.");
            }

            writer = _audioQueue.Writer;
            configuredSampleRate =
                _configuredSampleRate.Value;
        }

        ValidateChunk(
            chunk,
            configuredSampleRate);

        /*
         * This queues the chunk instead of performing a Google
         * network operation on the audio-capture thread.
         */
        return writer.WriteAsync(
            chunk,
            cancellationToken);
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        Channel<TranscriptionAudioChunk>? audioQueue;
        GoogleSpeechClient.StreamingRecognizeStream? stream;
        CancellationTokenSource? sessionCancellation;

        Task? writerTask;
        Task? readerTask;

        lock (_gate)
        {
            audioQueue = _audioQueue;
            stream = _stream;
            sessionCancellation = _sessionCancellation;
            writerTask = _writerTask;
            readerTask = _readerTask;

            if (stream is null)
            {
                return;
            }

            ClearSessionStateLocked();
        }

        Exception? failure = null;

        try
        {
            /*
             * Stop accepting new audio and allow the writer
             * to drain everything already queued.
             */
            audioQueue?.Writer.TryComplete();

            if (writerTask is not null)
            {
                try
                {
                    await writerTask.WaitAsync(
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            /*
             * Tell Google that no more requests will be sent.
             */
            try
            {
                await stream
                    .WriteCompleteAsync()
                    .WaitAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            /*
             * Allow Google to return any final transcript
             * results before the stream is disposed.
             */
            if (readerTask is not null)
            {
                try
                {
                    await readerTask.WaitAsync(
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
        }
        finally
        {
            sessionCancellation?.Cancel();
            stream.Dispose();
            sessionCancellation?.Dispose();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }
    }

    private static GoogleStreamingRecognizeRequest
        CreateConfigurationRequest(
            TranscriptionConfiguration configuration)
    {
        return new GoogleStreamingRecognizeRequest
        {
            StreamingConfig =
                new GoogleStreamingRecognitionConfig
                {
                    Config = new GoogleRecognitionConfig
                    {
                        Encoding =
                            GoogleRecognitionConfig
                                .Types
                                .AudioEncoding
                                .Linear16,

                        SampleRateHertz =
                            configuration.SampleRate,

                        LanguageCode =
                            configuration.LanguageCode,

                        AudioChannelCount = 1,

                        EnableAutomaticPunctuation = true
                    },

                    InterimResults =
                        configuration.EnableInterimResults,

                    SingleUtterance = false
                }
        };
    }

    private static async Task WriteAudioAsync(
        ChannelReader<TranscriptionAudioChunk> reader,
        GoogleSpeechClient.StreamingRecognizeStream stream,
        CancellationToken cancellationToken)
    {
        await foreach (
            TranscriptionAudioChunk chunk
            in reader.ReadAllAsync(cancellationToken))
        {
            var request =
                new GoogleStreamingRecognizeRequest
                {
                    AudioContent =
                        GoogleByteString.CopyFrom(
                            chunk.Data.ToArray())
                };

            await stream.WriteAsync(request);
        }
    }

    private async Task ReadResultsAsync(
        GoogleSpeechClient.StreamingRecognizeStream stream,
        CancellationToken cancellationToken)
    {
        await foreach (
            GoogleStreamingRecognizeResponse response
            in stream
                .GetResponseStream()
                .WithCancellation(cancellationToken))
        {
            /*
             * One Google response can contain:
             *
             * - a newly finalized portion;
             * - one or more consecutive interim portions.
             *
             * The interim portions must be combined into one
             * hypothesis before publishing them to the UI.
             */
            var interimParts =
                new List<string>();

            double? combinedStability =
                null;

            TimeSpan? interimEndTime =
                null;

            foreach (
                global::Google.Cloud.Speech.V1
                    .StreamingRecognitionResult result
                in response.Results)
            {
                if (result.Alternatives.Count == 0)
                {
                    continue;
                }

                string text =
                    result.Alternatives[0]
                        .Transcript
                        .Trim();

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (result.IsFinal)
                {
                    PublishGoogleResult(
                        text: text,
                        isFinal: true,
                        stability: null,
                        resultEndTime:
                            ConvertResultEndTime(result));

                    /*
                     * Any interim portions later in this response
                     * belong to the next unsettled segment.
                     */
                    Interlocked.Increment(
                        ref _segmentSequence);

                    continue;
                }

                interimParts.Add(text);

                if (result.Stability > 0)
                {
                    double stability =
                        result.Stability;

                    combinedStability =
                        combinedStability is null
                            ? stability
                            : Math.Min(
                                combinedStability.Value,
                                stability);
                }

                interimEndTime =
                    ConvertResultEndTime(result);
            }

            if (interimParts.Count == 0)
            {
                continue;
            }

            string combinedInterimText =
                string.Join(
                    " ",
                    interimParts)
                .Trim();

            PublishGoogleResult(
                text: combinedInterimText,
                isFinal: false,
                stability: combinedStability,
                resultEndTime: interimEndTime);
        }
    }

    private void PublishGoogleResult(
    string text,
    bool isFinal,
    double? stability,
    TimeSpan? resultEndTime)
    {
        long sequence =
            Interlocked.Increment(
                ref _resultSequence);

        long segmentNumber =
            Interlocked.Read(
                ref _segmentSequence);

        var transcriptResult =
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
                    stability,

                ResultEndTime:
                    resultEndTime);

        PublishResult(
            transcriptResult);
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
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                /*
                 * A UI subscriber must not terminate the
                 * Google response-reading loop.
                 */
                Debug.WriteLine(
                    $"Transcript subscriber failed: {exception}");
            }
        }
    }

    private static TimeSpan? ConvertResultEndTime(
        global::Google.Cloud.Speech.V1
            .StreamingRecognitionResult result)
    {
        if (result.ResultEndTime is null)
        {
            return null;
        }

        double seconds =
            result.ResultEndTime.Seconds +
            result.ResultEndTime.Nanos /
            1_000_000_000.0;

        return TimeSpan.FromSeconds(seconds);
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
                "the active transcription configuration.",
                nameof(chunk));
        }

        if (
            chunk.Channels != 1 ||
            chunk.BitsPerSample != 16 ||
            chunk.Encoding !=
                AudioSampleEncoding.PcmInteger)
        {
            throw new ArgumentException(
                "Google transcription requires mono PCM16 " +
                "audio chunks.",
                nameof(chunk));
        }

        if (chunk.Data.IsEmpty)
        {
            throw new ArgumentException(
                "The audio chunk cannot be empty.",
                nameof(chunk));
        }
    }

    private void ClearSessionStateLocked()
    {
        _audioQueue = null;
        _stream = null;
        _sessionCancellation = null;

        _writerTask = null;
        _readerTask = null;

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