using System.Buffers.Binary;
using System.Text;

namespace TransGo.Speech.Sherpa;

internal sealed class Pcm16WaveReader
{
    public Pcm16WaveReader(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var stream =
            File.OpenRead(filePath);

        using var reader =
            new BinaryReader(
                stream,
                Encoding.ASCII,
                leaveOpen: false);

        string riff =
            Encoding.ASCII.GetString(
                reader.ReadBytes(4));

        _ = reader.ReadUInt32();

        string wave =
            Encoding.ASCII.GetString(
                reader.ReadBytes(4));

        if (riff != "RIFF" || wave != "WAVE")
        {
            throw new InvalidDataException(
                "The file is not a valid RIFF/WAVE file.");
        }

        ushort audioFormat = 0;
        ushort channels = 0;
        ushort bitsPerSample = 0;
        int sampleRate = 0;

        byte[]? audioData = null;

        while (stream.Position + 8 <= stream.Length)
        {
            string chunkId =
                Encoding.ASCII.GetString(
                    reader.ReadBytes(4));

            int chunkSize =
                checked((int)reader.ReadUInt32());

            long nextChunkPosition =
                stream.Position + chunkSize;

            switch (chunkId)
            {
                case "fmt ":
                    audioFormat =
                        reader.ReadUInt16();

                    channels =
                        reader.ReadUInt16();

                    sampleRate =
                        reader.ReadInt32();

                    _ = reader.ReadInt32();
                    _ = reader.ReadUInt16();

                    bitsPerSample =
                        reader.ReadUInt16();

                    break;

                case "data":
                    audioData =
                        reader.ReadBytes(chunkSize);

                    break;
            }

            /*
             * RIFF chunks are padded to an even byte boundary.
             */
            stream.Position =
                nextChunkPosition +
                (chunkSize % 2);

            if (
                audioData is not null &&
                sampleRate > 0)
            {
                break;
            }
        }

        if (audioFormat != 1)
        {
            throw new NotSupportedException(
                $"Expected PCM WAV audio, but format was {audioFormat}.");
        }

        if (channels != 1)
        {
            throw new NotSupportedException(
                $"Expected mono WAV audio, but found {channels} channels.");
        }

        if (bitsPerSample != 16)
        {
            throw new NotSupportedException(
                $"Expected 16-bit WAV audio, but found {bitsPerSample} bits.");
        }

        if (sampleRate <= 0)
        {
            throw new InvalidDataException(
                "The WAV sample rate is missing or invalid.");
        }

        if (audioData is null || audioData.Length == 0)
        {
            throw new InvalidDataException(
                "The WAV file does not contain audio data.");
        }

        if (audioData.Length % sizeof(short) != 0)
        {
            throw new InvalidDataException(
                "The PCM data contains an incomplete sample.");
        }

        SampleRate =
            sampleRate;

        Samples =
            ConvertToFloatSamples(audioData);
    }

    public int SampleRate { get; }

    public float[] Samples { get; }

    private static float[] ConvertToFloatSamples(
        byte[] pcm16)
    {
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
                sampleIndex *
                sizeof(short);

            short sample =
                BinaryPrimitives
                    .ReadInt16LittleEndian(
                        pcm16.AsSpan(
                            byteOffset,
                            sizeof(short)));

            samples[sampleIndex] =
                sample / 32768.0F;
        }

        return samples;
    }
}