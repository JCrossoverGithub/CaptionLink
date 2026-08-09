using System.Text;

namespace TransGo.OverlapBenchmark;

public sealed class Pcm16WaveFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly int _dataLength;

    private Pcm16WaveFile(
        FileStream stream,
        int sampleRate,
        int dataLength,
        long dataOffset)
    {
        _stream = stream;
        SampleRate = sampleRate;
        _dataLength = dataLength;
        _dataOffset = dataOffset;
    }

    public int SampleRate { get; }

    public int DataLength => _dataLength;

    public double DurationSeconds =>
        _dataLength /
        (double)(SampleRate * sizeof(short));

    public static Pcm16WaveFile Open(
        string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        try
        {
            using var reader =
                new BinaryReader(
                    stream,
                    Encoding.ASCII,
                    leaveOpen: true);

            if (
                ReadFourCc(reader) != "RIFF" ||
                reader.ReadUInt32() < 4 ||
                ReadFourCc(reader) != "WAVE")
            {
                throw new InvalidDataException(
                    $"'{path}' is not a RIFF WAVE file.");
            }

            ushort? format = null;
            ushort? channels = null;
            int? sampleRate = null;
            ushort? bitsPerSample = null;
            long? dataOffset = null;
            int? dataLength = null;

            while (stream.Position + 8 <= stream.Length)
            {
                string chunkId = ReadFourCc(reader);
                uint chunkLength = reader.ReadUInt32();
                long chunkStart = stream.Position;

                if (chunkLength > int.MaxValue)
                {
                    throw new InvalidDataException(
                        "The WAVE chunk is too large.");
                }

                if (chunkId == "fmt ")
                {
                    if (chunkLength < 16)
                    {
                        throw new InvalidDataException(
                            "The WAVE format chunk is incomplete.");
                    }

                    format = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt16();
                    bitsPerSample = reader.ReadUInt16();
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkStart;
                    dataLength = (int)chunkLength;
                }

                long nextChunk =
                    checked(
                        chunkStart + chunkLength +
                        (chunkLength % 2));

                if (nextChunk > stream.Length)
                {
                    throw new InvalidDataException(
                        "A WAVE chunk extends past the file.");
                }

                stream.Position = nextChunk;
            }

            if (
                format != 1 ||
                channels != 1 ||
                bitsPerSample != 16 ||
                sampleRate is null ||
                sampleRate <= 0 ||
                dataOffset is null ||
                dataLength is null ||
                dataLength <= 0 ||
                dataLength % sizeof(short) != 0)
            {
                throw new InvalidDataException(
                    $"'{path}' must be mono PCM16 WAVE audio.");
            }

            return new Pcm16WaveFile(
                stream,
                sampleRate.Value,
                dataLength.Value,
                dataOffset.Value);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public IEnumerable<byte[]> ReadChunks(
        TimeSpan chunkDuration)
    {
        if (chunkDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkDuration));
        }

        int samplesPerChunk = checked(
            (int)Math.Round(
                SampleRate *
                chunkDuration.TotalSeconds));

        if (samplesPerChunk <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkDuration));
        }

        int chunkBytes = checked(
            samplesPerChunk * sizeof(short));

        _stream.Position = _dataOffset;
        int remaining = _dataLength;

        while (remaining > 0)
        {
            int requested =
                Math.Min(chunkBytes, remaining);

            var buffer = new byte[requested];
            _stream.ReadExactly(buffer);
            remaining -= requested;
            yield return buffer;
        }
    }

    public void Dispose() =>
        _stream.Dispose();

    private static string ReadFourCc(
        BinaryReader reader) =>
        Encoding.ASCII.GetString(
            reader.ReadBytes(4));
}
