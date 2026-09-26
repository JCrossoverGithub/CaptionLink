using System.Buffers.Binary;

namespace CaptionLink.Speech.Sherpa;

/// <summary>
/// Accumulates mono PCM16 audio and produces overlapping
/// floating-point windows for Whisper recognition.
/// </summary>
internal sealed class WhisperRollingAudioBuffer
{
    private readonly int _sampleRate;
    private readonly int _windowSampleCount;
    private readonly int _hopSampleCount;

    private readonly List<float> _samples = new();

    private int _startIndex;

    public WhisperRollingAudioBuffer(
        int sampleRate,
        TimeSpan windowDuration,
        TimeSpan hopDuration)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate));
        }

        if (windowDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowDuration));
        }

        if (hopDuration <= TimeSpan.Zero ||
            hopDuration > windowDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hopDuration),
                "Hop duration must be positive and no longer " +
                "than the window duration.");
        }

        _sampleRate = sampleRate;

        _windowSampleCount = checked(
            (int)Math.Round(
                sampleRate *
                windowDuration.TotalSeconds));

        _hopSampleCount = checked(
            (int)Math.Round(
                sampleRate *
                hopDuration.TotalSeconds));
    }

    public int AvailableSampleCount =>
        _samples.Count - _startIndex;

    public TimeSpan AvailableDuration =>
        TimeSpan.FromSeconds(
            AvailableSampleCount /
            (double)_sampleRate);

    public IReadOnlyList<float[]> AddPcm16(
        ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length % sizeof(short) != 0)
        {
            throw new ArgumentException(
                "PCM16 data must contain complete samples.",
                nameof(pcm16));
        }

        AppendSamples(pcm16);

        var completedWindows =
            new List<float[]>();

        while (AvailableSampleCount >=
               _windowSampleCount)
        {
            var window =
                new float[_windowSampleCount];

            _samples.CopyTo(
                _startIndex,
                window,
                0,
                _windowSampleCount);

            completedWindows.Add(window);

            /*
             * Advance only by the hop duration. Samples after
             * the hop remain available as overlap for the next
             * recognition window.
             */
            _startIndex +=
                _hopSampleCount;

            CompactWhenUseful();
        }

        return completedWindows;
    }

    public float[] DrainRemaining(
        TimeSpan minimumDuration)
    {
        if (minimumDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumDuration));
        }

        int minimumSampleCount = checked(
            (int)Math.Round(
                _sampleRate *
                minimumDuration.TotalSeconds));

        if (AvailableSampleCount <
            minimumSampleCount)
        {
            Reset();
            return [];
        }

        var remaining =
            new float[AvailableSampleCount];

        _samples.CopyTo(
            _startIndex,
            remaining,
            0,
            remaining.Length);

        Reset();

        return remaining;
    }

    public void Reset()
    {
        _samples.Clear();
        _startIndex = 0;
    }

    private void AppendSamples(
        ReadOnlySpan<byte> pcm16)
    {
        int sampleCount =
            pcm16.Length / sizeof(short);

        _samples.EnsureCapacity(
            _samples.Count + sampleCount);

        for (
            int sampleIndex = 0;
            sampleIndex < sampleCount;
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

            _samples.Add(
                sample / 32768.0F);
        }
    }

    private void CompactWhenUseful()
    {
        /*
         * Avoid repeatedly shifting the list after every
         * window. Compact only after enough consumed samples
         * have accumulated.
         */
        if (_startIndex <
            _windowSampleCount * 2)
        {
            return;
        }

        _samples.RemoveRange(
            0,
            _startIndex);

        _startIndex = 0;
    }
}