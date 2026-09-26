using System.IO;
using CaptionLink.Core.Audio;

namespace CaptionLink.Speech.Sherpa;

/// <summary>
/// A completed PCM16 utterance ready for Whisper decoding.
/// </summary>
internal sealed record HybridUtteranceAudio(
    int SampleRate,
    byte[] Pcm16)
{
    public TimeSpan Duration =>
        TimeSpan.FromSeconds(
            Pcm16.Length /
            (double)(SampleRate * sizeof(short)));
}

/// <summary>
/// Collects normalized mono PCM16 audio until the streaming
/// recognizer detects the end of an utterance.
/// </summary>
internal sealed class HybridUtteranceAudioBuffer
    : IDisposable
{
    private static readonly TimeSpan DefaultMaximumDuration =
        TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly TimeSpan _maximumDuration;

    private readonly MemoryStream _pcm16 =
        new();

    private int? _sampleRate;
    private bool _disposed;

    public HybridUtteranceAudioBuffer()
        : this(DefaultMaximumDuration)
    {
    }

    public HybridUtteranceAudioBuffer(
        TimeSpan maximumDuration)
    {
        if (maximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDuration));
        }

        _maximumDuration =
            maximumDuration;
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_gate)
            {
                if (
                    _sampleRate is null ||
                    _pcm16.Length == 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromSeconds(
                    _pcm16.Length /
                    (double)(
                        _sampleRate.Value *
                        sizeof(short)));
            }
        }
    }

    public void Add(
        TranscriptionAudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        ValidateChunk(chunk);

        if (chunk.Data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (_sampleRate is null)
            {
                _sampleRate =
                    chunk.SampleRate;
            }
            else if (
                _sampleRate.Value !=
                chunk.SampleRate)
            {
                throw new InvalidOperationException(
                    "The utterance sample rate changed while " +
                    "audio was being buffered.");
            }

            _pcm16.Write(
                chunk.Data.Span);

            TrimToMaximumDurationLocked();
        }
    }

    /// <summary>
    /// Returns the current utterance and clears the buffer.
    /// Tiny segments are discarded.
    /// </summary>
    public HybridUtteranceAudio? TakeAndReset(
        TimeSpan minimumDuration)
    {
        if (minimumDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumDuration));
        }

        lock (_gate)
        {
            ThrowIfDisposedLocked();

            if (
                _sampleRate is null ||
                _pcm16.Length == 0)
            {
                ResetLocked();
                return null;
            }

            int sampleRate =
                _sampleRate.Value;

            TimeSpan duration =
                TimeSpan.FromSeconds(
                    _pcm16.Length /
                    (double)(
                        sampleRate *
                        sizeof(short)));

            if (duration < minimumDuration)
            {
                ResetLocked();
                return null;
            }

            byte[] audio =
                _pcm16.ToArray();

            ResetLocked();

            return new HybridUtteranceAudio(
                SampleRate: sampleRate,
                Pcm16: audio);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            ResetLocked();
        }
    }

    private void TrimToMaximumDurationLocked()
    {
        if (_sampleRate is null)
        {
            return;
        }

        int maximumByteCount =
            checked(
                (int)Math.Round(
                    _sampleRate.Value *
                    sizeof(short) *
                    _maximumDuration.TotalSeconds));

        /*
         * PCM16 samples contain two bytes, so preserve an even
         * number of bytes.
         */
        maximumByteCount -=
            maximumByteCount % sizeof(short);

        if (_pcm16.Length <= maximumByteCount)
        {
            return;
        }

        byte[] currentAudio =
            _pcm16.ToArray();

        int startIndex =
            currentAudio.Length -
            maximumByteCount;

        /*
         * Keep the newest audio if an endpoint is not detected
         * for an unusually long time.
         */
        _pcm16.SetLength(0);
        _pcm16.Position = 0;

        _pcm16.Write(
            currentAudio.AsSpan(
                startIndex,
                maximumByteCount));
    }

    private static void ValidateChunk(
        TranscriptionAudioChunk chunk)
    {
        if (
            chunk.SampleRate <= 0 ||
            chunk.Channels != 1 ||
            chunk.BitsPerSample != 16 ||
            chunk.Encoding !=
                AudioSampleEncoding.PcmInteger)
        {
            throw new ArgumentException(
                "The hybrid utterance buffer requires " +
                "mono PCM16 audio with a valid sample rate.",
                nameof(chunk));
        }

        if (
            chunk.Data.Length %
            sizeof(short) != 0)
        {
            throw new ArgumentException(
                "The PCM16 chunk contains an incomplete sample.",
                nameof(chunk));
        }
    }

    private void ResetLocked()
    {
        _pcm16.SetLength(0);
        _pcm16.Position = 0;
        _sampleRate = null;
    }

    private void ThrowIfDisposedLocked()
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
            _pcm16.Dispose();
        }
    }
}