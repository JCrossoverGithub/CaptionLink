using System.Buffers.Binary;
using CaptionLink.Core.Audio;

namespace CaptionLink.Audio.Processing;

/// <summary>
/// Stateful, mono PCM16 48 kHz -> 16 kHz downsampler for sequential
/// CaptionLink audio chunks. It is independent of GPULink transport.
///
/// A causal 255-tap Blackman-windowed low-pass FIR precedes decimation by 3.
/// The filter is continuous across chunk boundaries. The fixed group delay
/// is 127 input samples (~2.65 ms); timestamp compensation is intentionally
/// deferred to the eventual provider-composition review.
/// </summary>
public sealed class StreamingPcm16Resampler48To16
{
    public const int InputSampleRate = 48_000;
    public const int OutputSampleRate = 16_000;
    private const int Decimation = 3;
    private const int Taps = 255;
    private const int MaxInputSamplesPerChunk = 96_000;
    private static readonly double[] Coefficients = CreateCoefficients();

    private readonly short[] _history = new short[Taps];
    private int _writeIndex;
    private long _inputSamples;
    private long _outputSamples;
    private long? _previousSequence;

    public long InputSamplesConsumed => _inputSamples;
    public long OutputSamplesProduced => _outputSamples;

    /// <summary>
    /// Produces one gap-free 16kHz chunk, or null when fewer than three input
    /// samples produce no new decimated output. Must be used by one serialized
    /// producer per capture session. Reset between sessions.
    /// </summary>
    public TranscriptionAudioChunk? Process(TranscriptionAudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.SampleRate != InputSampleRate ||
            chunk.Data.IsEmpty ||
            (chunk.Data.Length & 1) != 0 ||
            chunk.Data.Length / 2 > MaxInputSamplesPerChunk)
        {
            throw new ArgumentException(
                "Expected bounded nonempty 48 kHz mono PCM16 input.", nameof(chunk));
        }

        long startSample = checked((long)Math.Round(
            chunk.SessionStartTime.Ticks * (double)InputSampleRate / TimeSpan.TicksPerSecond,
            MidpointRounding.AwayFromZero));
        if (chunk.SessionStartTime < TimeSpan.Zero || startSample != _inputSamples)
            throw new InvalidOperationException("Input sample timeline is discontinuous.");
        if (_previousSequence.HasValue &&
            (_previousSequence.Value == long.MaxValue || chunk.Sequence != _previousSequence.Value + 1))
            throw new InvalidOperationException("Input chunk sequence is discontinuous.");

        int inputCount = chunk.Data.Length / 2;
        // Number of output ticks at input positions 0,3,6,..., including
        // ticks that straddle the current call boundary.
        int outputCount = checked((int)(
            (_inputSamples + inputCount + Decimation - 1) / Decimation -
            (_inputSamples + Decimation - 1) / Decimation));
        byte[] result = new byte[checked(outputCount * 2)];
        int outputPos = 0;
        ReadOnlySpan<byte> pcm = chunk.Data.Span;
        long outputStartSample = _outputSamples;

        for (int n = 0; n < inputCount; n++)
        {
            _history[_writeIndex] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(n * 2, 2));
            _writeIndex = (_writeIndex + 1) % Taps;
            if (_inputSamples % Decimation == 0)
            {
                double filtered = 0;
                int index = _writeIndex == 0 ? Taps - 1 : _writeIndex - 1;
                for (int k = 0; k < Taps; k++)
                {
                    filtered += Coefficients[k] * _history[index];
                    if (--index < 0) index = Taps - 1;
                }
                int rounded = (int)Math.Round(filtered, MidpointRounding.AwayFromZero);
                short sample = (short)Math.Clamp(rounded, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(outputPos * 2, 2), sample);
                outputPos++;
                _outputSamples++;
            }
            _inputSamples++;
        }
        _previousSequence = chunk.Sequence;
        if (outputPos != outputCount)
            throw new InvalidOperationException("Output sample count invariant violated.");
        if (outputCount == 0) return null;
        long outputStartTicks = checked((long)Math.Round(
            outputStartSample * (double)TimeSpan.TicksPerSecond / OutputSampleRate,
            MidpointRounding.AwayFromZero));

        return new TranscriptionAudioChunk(
            chunk.Sequence, chunk.CapturedAt, result, OutputSampleRate)
        {
            SessionStartTime = TimeSpan.FromTicks(outputStartTicks)
        };
    }

    public void Reset()
    {
        Array.Clear(_history);
        _writeIndex = 0;
        _inputSamples = 0;
        _outputSamples = 0;
        _previousSequence = null;
    }

    private static double[] CreateCoefficients()
    {
        // 7.0kHz cutoff, 48kHz input; Blackman window suppresses aliases
        // above the 8kHz Nyquist of the 16kHz output.
        const double cutoffCyclesPerSample = 7_000.0 / InputSampleRate;
        var taps = new double[Taps];
        double sum = 0;
        int middle = (Taps - 1) / 2;
        for (int i = 0; i < Taps; i++)
        {
            int distance = i - middle;
            double ideal = distance == 0
                ? 2 * cutoffCyclesPerSample
                : Math.Sin(2 * Math.PI * cutoffCyclesPerSample * distance) /
                  (Math.PI * distance);
            double window = 0.42
                - 0.5 * Math.Cos(2 * Math.PI * i / (Taps - 1))
                + 0.08 * Math.Cos(4 * Math.PI * i / (Taps - 1));
            taps[i] = ideal * window;
            sum += taps[i];
        }
        for (int i = 0; i < taps.Length; i++) taps[i] /= sum;
        return taps;
    }
}
