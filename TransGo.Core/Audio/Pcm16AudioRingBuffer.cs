using System.Buffers.Binary;

namespace TransGo.Core.Audio;

/// <summary>
/// Keeps a fixed-duration mono PCM16 history for overlap processing.
/// Incoming normalized chunks are converted to the configured sample
/// rate before storage, so the future separator receives one stable
/// audio format regardless of the Windows output-device rate.
/// </summary>
public sealed class Pcm16AudioRingBuffer
{
    private const int BytesPerSample = sizeof(short);

    private readonly object _gate = new();
    private readonly byte[] _buffer;

    private int _writeOffset;
    private int _storedBytes;
    private long _firstSampleIndex;
    private bool _hasTimeline;

    private long _chunksAppended;
    private long _inputBytesReceived;
    private long _outputBytesProduced;
    private long _overwrittenOutputBytes;
    private long _discontinuities;

    public Pcm16AudioRingBuffer(
        int sampleRate = 16_000,
        TimeSpan? capacity = null)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }

        SampleRate = sampleRate;
        Capacity = capacity ??
            TimeSpan.FromSeconds(30);

        if (Capacity <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity));
        }

        long capacitySamples = checked(
            (long)Math.Ceiling(
                Capacity.TotalSeconds *
                SampleRate));

        long capacityBytes = checked(
            capacitySamples *
            BytesPerSample);

        if (capacityBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                "The requested audio buffer is too large.");
        }

        _buffer = new byte[(int)capacityBytes];
    }

    public int SampleRate { get; }

    public TimeSpan Capacity { get; }

    public TimeSpan? AvailableStartTime
    {
        get
        {
            lock (_gate)
            {
                return GetAvailableStartTimeLocked();
            }
        }
    }

    public TimeSpan? AvailableEndTime
    {
        get
        {
            lock (_gate)
            {
                return GetAvailableEndTimeLocked();
            }
        }
    }

    public void Append(
        TranscriptionAudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ValidateChunk(chunk);

        long outputStartSample =
            TimeToSampleIndex(
                chunk.SessionStartTime);

        long outputEndSample =
            TimeToSampleIndex(
                chunk.SessionEndTime);

        int outputSampleCount = checked(
            (int)(
                outputEndSample -
                outputStartSample));

        if (outputSampleCount <= 0)
        {
            return;
        }

        byte[] output =
            ResamplePcm16(
                chunk.Data.Span,
                chunk.SampleRate,
                outputSampleCount);

        lock (_gate)
        {
            _chunksAppended++;
            _inputBytesReceived +=
                chunk.Data.Length;

            _outputBytesProduced +=
                output.Length;

            long expectedStartSample =
                _firstSampleIndex +
                _storedBytes /
                BytesPerSample;

            if (
                _hasTimeline
                &&
                outputStartSample !=
                    expectedStartSample)
            {
                ClearAudioLocked();
                _discontinuities++;
            }

            if (!_hasTimeline)
            {
                _firstSampleIndex =
                    outputStartSample;

                _hasTimeline = true;
            }

            AppendOutputLocked(
                output,
                outputStartSample);
        }
    }

    public bool TryRead(
        TimeSpan startTime,
        TimeSpan endTime,
        out Pcm16AudioWindow? window)
    {
        if (
            startTime < TimeSpan.Zero
            ||
            endTime <= startTime)
        {
            throw new ArgumentException(
                "The requested audio range is invalid.");
        }

        long startSample =
            TimeToSampleIndex(startTime);

        long endSample =
            TimeToSampleIndex(endTime);

        lock (_gate)
        {
            long availableEndSample =
                _firstSampleIndex +
                _storedBytes /
                BytesPerSample;

            if (
                !_hasTimeline
                ||
                startSample < _firstSampleIndex
                ||
                endSample > availableEndSample
                ||
                endSample <= startSample)
            {
                window = null;
                return false;
            }

            int byteCount = checked(
                (int)(
                    (endSample - startSample) *
                    BytesPerSample));

            int byteOffset = checked(
                (int)(
                    (startSample - _firstSampleIndex) *
                    BytesPerSample));

            var data =
                new byte[byteCount];

            CopyFromRingLocked(
                byteOffset,
                data);

            window =
                new Pcm16AudioWindow(
                    StartTime:
                        SampleIndexToTime(
                            startSample),

                    EndTime:
                        SampleIndexToTime(
                            endSample),

                    SampleRate:
                        SampleRate,

                    Data:
                        data);

            return true;
        }
    }

    public Pcm16AudioRingBufferMetrics GetMetrics()
    {
        lock (_gate)
        {
            return new Pcm16AudioRingBufferMetrics(
                ChunksAppended:
                    _chunksAppended,

                InputBytesReceived:
                    _inputBytesReceived,

                OutputBytesProduced:
                    _outputBytesProduced,

                OverwrittenOutputBytes:
                    _overwrittenOutputBytes,

                Discontinuities:
                    _discontinuities,

                StoredBytes:
                    _storedBytes,

                StoredDuration:
                    TimeSpan.FromSeconds(
                        _storedBytes /
                        (double)(
                            SampleRate *
                            BytesPerSample)),

                AvailableStartTime:
                    GetAvailableStartTimeLocked(),

                AvailableEndTime:
                    GetAvailableEndTimeLocked());
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ClearAudioLocked();

            _chunksAppended = 0;
            _inputBytesReceived = 0;
            _outputBytesProduced = 0;
            _overwrittenOutputBytes = 0;
            _discontinuities = 0;
        }
    }

    private void AppendOutputLocked(
        byte[] output,
        long outputStartSample)
    {
        ReadOnlySpan<byte> bytes =
            output;

        if (bytes.Length >= _buffer.Length)
        {
            int skippedBytes =
                bytes.Length -
                _buffer.Length;

            skippedBytes -=
                skippedBytes %
                BytesPerSample;

            _overwrittenOutputBytes +=
                _storedBytes +
                skippedBytes;

            bytes = bytes[skippedBytes..];

            _firstSampleIndex =
                outputStartSample +
                skippedBytes /
                BytesPerSample;

            _storedBytes = 0;
            _writeOffset = 0;
        }

        int bytesToDiscard =
            Math.Max(
                0,
                _storedBytes +
                bytes.Length -
                _buffer.Length);

        bytesToDiscard -=
            bytesToDiscard %
            BytesPerSample;

        if (bytesToDiscard > 0)
        {
            _storedBytes -=
                bytesToDiscard;

            _firstSampleIndex +=
                bytesToDiscard /
                BytesPerSample;

            _overwrittenOutputBytes +=
                bytesToDiscard;
        }

        int firstCopyLength =
            Math.Min(
                bytes.Length,
                _buffer.Length -
                _writeOffset);

        bytes[..firstCopyLength]
            .CopyTo(
                _buffer.AsSpan(
                    _writeOffset,
                    firstCopyLength));

        int remainingLength =
            bytes.Length -
            firstCopyLength;

        if (remainingLength > 0)
        {
            bytes[firstCopyLength..]
                .CopyTo(
                    _buffer.AsSpan(
                        0,
                        remainingLength));
        }

        _writeOffset =
            (_writeOffset + bytes.Length) %
            _buffer.Length;

        _storedBytes +=
            bytes.Length;
    }

    private void CopyFromRingLocked(
        int relativeByteOffset,
        Span<byte> destination)
    {
        int oldestByteOffset =
            (_writeOffset -
             _storedBytes +
             _buffer.Length) %
            _buffer.Length;

        int sourceOffset =
            (oldestByteOffset +
             relativeByteOffset) %
            _buffer.Length;

        int firstCopyLength =
            Math.Min(
                destination.Length,
                _buffer.Length -
                sourceOffset);

        _buffer.AsSpan(
                sourceOffset,
                firstCopyLength)
            .CopyTo(
                destination[..firstCopyLength]);

        int remainingLength =
            destination.Length -
            firstCopyLength;

        if (remainingLength > 0)
        {
            _buffer.AsSpan(
                    0,
                    remainingLength)
                .CopyTo(
                    destination[firstCopyLength..]);
        }
    }

    private TimeSpan?
        GetAvailableStartTimeLocked()
    {
        return _hasTimeline
            ? SampleIndexToTime(
                _firstSampleIndex)
            : null;
    }

    private TimeSpan?
        GetAvailableEndTimeLocked()
    {
        return _hasTimeline
            ? SampleIndexToTime(
                _firstSampleIndex +
                _storedBytes /
                BytesPerSample)
            : null;
    }

    private void ClearAudioLocked()
    {
        _writeOffset = 0;
        _storedBytes = 0;
        _firstSampleIndex = 0;
        _hasTimeline = false;
    }

    private long TimeToSampleIndex(
        TimeSpan time)
    {
        return checked(
            (long)Math.Round(
                time.TotalSeconds *
                SampleRate,
                MidpointRounding.AwayFromZero));
    }

    private TimeSpan SampleIndexToTime(
        long sampleIndex)
    {
        return TimeSpan.FromSeconds(
            sampleIndex /
            (double)SampleRate);
    }

    private static byte[] ResamplePcm16(
        ReadOnlySpan<byte> input,
        int inputSampleRate,
        int outputSampleCount)
    {
        int inputSampleCount =
            input.Length /
            BytesPerSample;

        if (
            inputSampleRate <= 0
            ||
            inputSampleCount <= 0
            ||
            outputSampleCount <= 0)
        {
            throw new ArgumentException(
                "The PCM16 resampling input is invalid.");
        }

        var output =
            new byte[
                outputSampleCount *
                BytesPerSample];

        if (inputSampleCount == outputSampleCount)
        {
            input.CopyTo(output);
            return output;
        }

        double inputSamplesPerOutputSample =
            inputSampleCount /
            (double)outputSampleCount;

        for (
            int outputIndex = 0;
            outputIndex < outputSampleCount;
            outputIndex++)
        {
            double sourceStart =
                outputIndex *
                inputSamplesPerOutputSample;

            double sourceEnd =
                (outputIndex + 1) *
                inputSamplesPerOutputSample;

            int firstInputIndex =
                Math.Max(
                    0,
                    (int)Math.Floor(
                        sourceStart));

            int lastInputIndex =
                Math.Min(
                    inputSampleCount - 1,
                    (int)Math.Ceiling(
                        sourceEnd) - 1);

            double weightedSample = 0;
            double totalWeight = 0;

            for (
                int inputIndex = firstInputIndex;
                inputIndex <= lastInputIndex;
                inputIndex++)
            {
                double overlapStart =
                    Math.Max(
                        sourceStart,
                        inputIndex);

                double overlapEnd =
                    Math.Min(
                        sourceEnd,
                        inputIndex + 1.0);

                double weight =
                    overlapEnd -
                    overlapStart;

                if (weight <= 0)
                {
                    continue;
                }

                short sample =
                    BinaryPrimitives
                        .ReadInt16LittleEndian(
                            input.Slice(
                                inputIndex *
                                    BytesPerSample,

                                BytesPerSample));

                weightedSample +=
                    sample * weight;

                totalWeight +=
                    weight;
            }

            int roundedSample =
                totalWeight > 0
                    ? (int)Math.Round(
                        weightedSample /
                        totalWeight)
                    : 0;

            short outputSample =
                (short)Math.Clamp(
                    roundedSample,
                    short.MinValue,
                    short.MaxValue);

            BinaryPrimitives
                .WriteInt16LittleEndian(
                    output.AsSpan(
                        outputIndex *
                            BytesPerSample,

                        BytesPerSample),

                    outputSample);
        }

        return output;
    }

    private static void ValidateChunk(
        TranscriptionAudioChunk chunk)
    {
        if (chunk.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunk),
                "The input sample rate must be positive.");
        }

        if (
            chunk.Data.IsEmpty
            ||
            chunk.Data.Length %
                BytesPerSample != 0)
        {
            throw new ArgumentException(
                "The chunk must contain complete PCM16 samples.",
                nameof(chunk));
        }

        if (chunk.SessionStartTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunk),
                "The chunk session time cannot be negative.");
        }
    }
}
