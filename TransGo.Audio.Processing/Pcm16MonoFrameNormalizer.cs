using System.Buffers.Binary;
using TransGo.Core.Audio;

namespace TransGo.Audio.Processing;

public sealed class Pcm16MonoFrameNormalizer
    : IAudioFrameNormalizer
{
    private const int ChunkDurationMilliseconds = 100;

    private readonly object _gate = new();
    private readonly MemoryStream _pendingAudio = new();

    private int? _sampleRate;
    private long _chunkSequence;
    private DateTimeOffset? _nextChunkCapturedAt;
    private TimeSpan _nextChunkSessionStart;
    private bool _disposed;


    public event EventHandler<TranscriptionAudioChunkEventArgs>?
        ChunkAvailable;

    public void Process(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        List<TranscriptionAudioChunk> completedChunks;

        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateFrame(frame);

            if (_sampleRate is null)
            {
                _sampleRate = frame.SampleRate;
            }
            else if (_sampleRate.Value != frame.SampleRate)
            {
                throw new InvalidOperationException(
                    "The input sample rate changed during an active " +
                    "normalization session. Reset the normalizer first.");
            }

            byte[] monoPcm16 = ConvertToMonoPcm16(frame);

            _pendingAudio.Position = _pendingAudio.Length;
            _pendingAudio.Write(
                monoPcm16,
                0,
                monoPcm16.Length);

            SetInitialTimestamp(frame);

            int samplesPerChunk =
                frame.SampleRate *
                ChunkDurationMilliseconds /
                1000;

            int bytesPerChunk =
                samplesPerChunk * sizeof(short);

            int availableBytes =
                checked((int)_pendingAudio.Length);

            int completeChunkCount =
                availableBytes / bytesPerChunk;

            completedChunks =
                new List<TranscriptionAudioChunk>(
                    completeChunkCount);

            byte[] pendingBuffer =
                _pendingAudio.GetBuffer();

            for (
                int chunkIndex = 0;
                chunkIndex < completeChunkCount;
                chunkIndex++)
            {
                var chunkData =
                    new byte[bytesPerChunk];

                Buffer.BlockCopy(
                    pendingBuffer,
                    chunkIndex * bytesPerChunk,
                    chunkData,
                    0,
                    bytesPerChunk);

                long sequence =
                    ++_chunkSequence;

                DateTimeOffset capturedAt =
                    _nextChunkCapturedAt!.Value;

                var completedChunk =
                new TranscriptionAudioChunk(
                    Sequence: sequence,
                    CapturedAt: capturedAt,
                    Data: chunkData,
                    SampleRate: frame.SampleRate)
                {
                    SessionStartTime =
                        _nextChunkSessionStart,
                };

                completedChunks.Add(
                    completedChunk);

                _nextChunkCapturedAt =
                    capturedAt.Add(
                        completedChunk.Duration);

                _nextChunkSessionStart =
                    completedChunk.SessionEndTime;
            }

            PreserveIncompleteRemainder(
                pendingBuffer,
                availableBytes,
                completeChunkCount * bytesPerChunk);
        }

        /*
         * Raise events outside the lock so consumers cannot block
         * or re-enter the normalizer while its state is locked.
         */
        foreach (
            TranscriptionAudioChunk chunk
            in completedChunks)
        {
            ChunkAvailable?.Invoke(
                this,
                new TranscriptionAudioChunkEventArgs(chunk));
        }
    }

    private void SetInitialTimestamp(AudioFrame frame)
    {
        if (_nextChunkCapturedAt is not null)
        {
            return;
        }

        int bytesPerSample =
            frame.BitsPerSample / 8;

        int bytesPerSecond = checked(
            frame.SampleRate *
            frame.Channels *
            bytesPerSample);

        TimeSpan frameDuration =
            TimeSpan.FromSeconds(
                frame.Data.Length /
                (double)bytesPerSecond);

        /*
         * AudioFrame.CapturedAt represents approximately when the
         * WASAPI callback arrived. Subtracting the frame duration
         * provides a reasonable estimate of when its audio began.
         */
        _nextChunkCapturedAt =
            frame.CapturedAt - frameDuration;
    }

    private void PreserveIncompleteRemainder(
        byte[] pendingBuffer,
        int availableBytes,
        int consumedBytes)
    {
        int remainingBytes =
            availableBytes - consumedBytes;

        if (remainingBytes > 0)
        {
            Buffer.BlockCopy(
                pendingBuffer,
                consumedBytes,
                pendingBuffer,
                0,
                remainingBytes);
        }

        _pendingAudio.SetLength(remainingBytes);
        _pendingAudio.Position = remainingBytes;
    }

    private static byte[] ConvertToMonoPcm16(
        AudioFrame frame)
    {
        int bytesPerSample =
            frame.BitsPerSample / 8;

        int sourceBlockAlign =
            frame.Channels * bytesPerSample;

        int sourceFrameCount =
            frame.Data.Length / sourceBlockAlign;

        var output =
            new byte[sourceFrameCount * sizeof(short)];

        ReadOnlySpan<byte> input =
            frame.Data.Span;

        for (
            int sourceFrameIndex = 0;
            sourceFrameIndex < sourceFrameCount;
            sourceFrameIndex++)
        {
            double channelTotal = 0;

            for (
                int channelIndex = 0;
                channelIndex < frame.Channels;
                channelIndex++)
            {
                int sampleOffset =
                    sourceFrameIndex * sourceBlockAlign +
                    channelIndex * bytesPerSample;

                channelTotal += ReadNormalizedSample(
                    input,
                    sampleOffset,
                    frame);
            }

            double monoSample =
                channelTotal / frame.Channels;

            short pcm16Sample =
                ConvertFloatToPcm16(monoSample);

            BinaryPrimitives.WriteInt16LittleEndian(
                output.AsSpan(
                    sourceFrameIndex * sizeof(short),
                    sizeof(short)),
                pcm16Sample);
        }

        return output;
    }

    private static double ReadNormalizedSample(
        ReadOnlySpan<byte> input,
        int offset,
        AudioFrame frame)
    {
        if (
            frame.Encoding ==
                AudioSampleEncoding.IeeeFloat &&
            frame.BitsPerSample == 32)
        {
            float sample =
                BitConverter.ToSingle(
                    input.Slice(
                        offset,
                        sizeof(float)));

            return float.IsFinite(sample)
                ? Math.Clamp(sample, -1.0f, 1.0f)
                : 0;
        }

        if (
            frame.Encoding ==
                AudioSampleEncoding.PcmInteger &&
            frame.BitsPerSample == 16)
        {
            short sample =
                BinaryPrimitives.ReadInt16LittleEndian(
                    input.Slice(
                        offset,
                        sizeof(short)));

            return sample / 32768.0;
        }

        throw new NotSupportedException(
            $"TransGo cannot normalize " +
            $"{frame.Encoding} audio with " +
            $"{frame.BitsPerSample} bits per sample.");
    }

    private static short ConvertFloatToPcm16(
        double sample)
    {
        double clamped =
            Math.Clamp(sample, -1.0, 1.0);

        int scaled = clamped >= 0
            ? (int)Math.Round(
                clamped * short.MaxValue)
            : (int)Math.Round(
                clamped * -short.MinValue);

        return (short)Math.Clamp(
            scaled,
            short.MinValue,
            short.MaxValue);
    }

    private static void ValidateFrame(
        AudioFrame frame)
    {
        if (frame.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                "The sample rate must be positive.");
        }

        if (frame.Channels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                "The channel count must be positive.");
        }

        if (
            frame.BitsPerSample <= 0 ||
            frame.BitsPerSample % 8 != 0)
        {
            throw new ArgumentException(
                "Bits per sample must be a positive " +
                "multiple of eight.",
                nameof(frame));
        }

        int bytesPerSample =
            frame.BitsPerSample / 8;

        int blockAlign =
            frame.Channels * bytesPerSample;

        if (frame.Data.Length % blockAlign != 0)
        {
            throw new ArgumentException(
                "The audio data does not contain a whole " +
                "number of sample frames.",
                nameof(frame));
        }

        bool supported =
            frame.Encoding ==
                AudioSampleEncoding.IeeeFloat &&
            frame.BitsPerSample == 32
            ||
            frame.Encoding ==
                AudioSampleEncoding.PcmInteger &&
            frame.BitsPerSample == 16;

        if (!supported)
        {
            throw new NotSupportedException(
                $"Unsupported input format: " +
                $"{frame.Encoding}, " +
                $"{frame.BitsPerSample} bits.");
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            _pendingAudio.SetLength(0);
            _pendingAudio.Position = 0;

            _sampleRate = null;
            _chunkSequence = 0;
            _nextChunkCapturedAt = null;
            _nextChunkSessionStart = TimeSpan.Zero;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingAudio.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}