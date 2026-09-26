using System.Buffers.Binary;
using SherpaOnnx;

namespace CaptionLink.Speech.Sherpa;

/// <summary>
/// Decodes complete speech utterances with Whisper Turbo.
/// This class should be called from one background worker.
/// </summary>
internal sealed class WhisperUtteranceDecoder
    : IDisposable
{
    private readonly OfflineRecognizer _recognizer;
    private bool _disposed;

    public WhisperUtteranceDecoder()
    {
        _recognizer =
            SherpaWhisperRecognizerFactory
                .CreateEnglishCpu();
    }

    public string Decode(
        HybridUtteranceAudio utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        if (utterance.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utterance),
                "The utterance sample rate must be positive.");
        }

        if (
            utterance.Pcm16.Length == 0 ||
            utterance.Pcm16.Length % sizeof(short) != 0)
        {
            throw new ArgumentException(
                "The utterance must contain complete PCM16 samples.",
                nameof(utterance));
        }

        float[] samples =
            ConvertPcm16ToFloat(
                utterance.Pcm16);

        OfflineStream stream =
            _recognizer.CreateStream();

        try
        {
            stream.AcceptWaveform(
                utterance.SampleRate,
                samples);

            _recognizer.Decode(
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

    private static float[] ConvertPcm16ToFloat(
        ReadOnlySpan<byte> pcm16)
    {
        int sampleCount =
            pcm16.Length / sizeof(short);

        var samples =
            new float[sampleCount];

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

            samples[sampleIndex] =
                sample / 32768.0F;
        }

        return samples;
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

    private static void DisposeIfSupported(
        object? value)
    {
        if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        DisposeIfSupported(
            _recognizer);
    }
}