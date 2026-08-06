using System.Buffers.Binary;

namespace TransGo.Speech.Remote;

internal sealed class Pcm16MonoResampler
{
    private readonly int _sourceSampleRate;
    private readonly int _targetSampleRate;

    public Pcm16MonoResampler(
        int sourceSampleRate,
        int targetSampleRate)
    {
        if (sourceSampleRate < targetSampleRate)
        {
            throw new NotSupportedException(
                $"Remote transcription cannot upsample " +
                $"{sourceSampleRate} Hz audio to {targetSampleRate} Hz.");
        }

        _sourceSampleRate = sourceSampleRate;
        _targetSampleRate = targetSampleRate;
    }

    public byte[] Resample(ReadOnlySpan<byte> sourcePcm)
    {
        if (sourcePcm.Length == 0 || sourcePcm.Length % sizeof(short) != 0)
        {
            throw new ArgumentException(
                "PCM audio must contain complete 16-bit samples.",
                nameof(sourcePcm));
        }

        if (_sourceSampleRate == _targetSampleRate)
        {
            return sourcePcm.ToArray();
        }

        int sourceSampleCount = sourcePcm.Length / sizeof(short);
        int targetSampleCount = checked((int)Math.Round(
            sourceSampleCount *
            (_targetSampleRate / (double)_sourceSampleRate)));

        if (targetSampleCount <= 0)
        {
            throw new ArgumentException(
                "PCM audio is too short to resample.",
                nameof(sourcePcm));
        }

        var targetPcm = new byte[targetSampleCount * sizeof(short)];
        double sourceSamplesPerTargetSample =
            _sourceSampleRate / (double)_targetSampleRate;

        for (int targetIndex = 0;
             targetIndex < targetSampleCount;
             targetIndex++)
        {
            double sourceStart =
                targetIndex * sourceSamplesPerTargetSample;
            double sourceEnd = Math.Min(
                sourceSampleCount,
                (targetIndex + 1) * sourceSamplesPerTargetSample);

            int firstSourceIndex = (int)Math.Floor(sourceStart);
            int lastSourceIndex = Math.Min(
                sourceSampleCount - 1,
                (int)Math.Ceiling(sourceEnd) - 1);

            double weightedTotal = 0;
            double totalWeight = 0;

            for (int sourceIndex = firstSourceIndex;
                 sourceIndex <= lastSourceIndex;
                 sourceIndex++)
            {
                double overlap = Math.Min(sourceEnd, sourceIndex + 1.0) -
                    Math.Max(sourceStart, sourceIndex);

                if (overlap <= 0)
                {
                    continue;
                }

                short sample = BinaryPrimitives.ReadInt16LittleEndian(
                    sourcePcm.Slice(
                        sourceIndex * sizeof(short),
                        sizeof(short)));

                weightedTotal += sample * overlap;
                totalWeight += overlap;
            }

            int resampled = totalWeight > 0
                ? (int)Math.Round(weightedTotal / totalWeight)
                : 0;

            short targetSample = (short)Math.Clamp(
                resampled,
                short.MinValue,
                short.MaxValue);

            BinaryPrimitives.WriteInt16LittleEndian(
                targetPcm.AsSpan(
                    targetIndex * sizeof(short),
                    sizeof(short)),
                targetSample);
        }

        return targetPcm;
    }
}
