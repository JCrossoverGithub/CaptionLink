using System.Buffers.Binary;
using CaptionLink.Audio.Processing;
using CaptionLink.Core.Audio;

static class Program
{
    private static int _passed;

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAILED: " + name);
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static TranscriptionAudioChunk Chunk(byte[] audio, long sequence, long startSample)
        => new(sequence, DateTimeOffset.UnixEpoch, audio, 48_000)
        {
            SessionStartTime = TimeSpan.FromTicks(checked((long)Math.Round(
                startSample * (double)TimeSpan.TicksPerSecond / 48_000,
                MidpointRounding.AwayFromZero)))
        };

    private static byte[] Sine(double hz, int samples, short peak = 12_000)
    {
        byte[] data = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short value = (short)Math.Round(peak * Math.Sin(2 * Math.PI * hz * i / 48_000));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2 * i, 2), value);
        }
        return data;
    }

    private static double Rms(byte[] bytes, int skip)
    {
        double total = 0;
        int count = 0;
        for (int i = skip; i < bytes.Length / 2; i++)
        {
            double v = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2));
            total += v * v;
            count++;
        }
        return Math.Sqrt(total / count);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, name); return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + name);
    }

    private static byte[] RunSplit(byte[] source, int pieceSamples)
    {
        var resampler = new StreamingPcm16Resampler48To16();
        using var output = new MemoryStream();
        long seq = 1;
        for (int pos = 0; pos < source.Length / 2; pos += pieceSamples)
        {
            int count = Math.Min(pieceSamples, source.Length / 2 - pos);
            byte[] piece = source.AsSpan(pos * 2, count * 2).ToArray();
            TranscriptionAudioChunk? result = resampler.Process(Chunk(piece, seq++, pos));
            if (result is not null) output.Write(result.Data.Span);
        }
        Check(resampler.InputSamplesConsumed == source.Length / 2, "all input samples consumed");
        Check(resampler.OutputSamplesProduced == (source.Length / 2 + 2) / 3,
            "decimated output count is exact");
        return output.ToArray();
    }

    static void Main()
    {
        byte[] speechBand = Sine(2_000, 48_000);
        byte[] whole = RunSplit(speechBand, 48_000);
        Check(whole.Length == 32_000, "one second maps 48k to 16k exactly");
        byte[] sliced = RunSplit(speechBand, 97);
        Check(whole.AsSpan().SequenceEqual(sliced), "fragmentation-independent exact output bytes");
        byte[] chunks100 = RunSplit(speechBand, 4_800);
        Check(whole.AsSpan().SequenceEqual(chunks100), "100ms capture chunks match a full batch");
        Check(Rms(whole, 500) > 6_000, "2kHz voice-band tone preserved");
        byte[] alias = RunSplit(Sine(12_000, 48_000), 4_800);
        Check(Rms(alias, 500) < 200, "12kHz alias strongly attenuated");

        var r = new StreamingPcm16Resampler48To16();
        byte[] first = Sine(1_000, 4_800);
        var a = r.Process(Chunk(first, 1, 0))!;
        var b = r.Process(Chunk(first, 2, 4_800))!;
        Check(a.SampleRate == 16_000 && a.Data.Length == 3_200,
            "resampler returns 16kHz PCM16 100ms chunks");
        Check(a.SessionStartTime == TimeSpan.Zero &&
              b.SessionStartTime == TimeSpan.FromMilliseconds(100),
            "100ms output timestamps remain contiguous");
        Throws<InvalidOperationException>(() => r.Process(Chunk(first, 4, 9_600)),
            "sequence gaps reject before sample consumption");
        Throws<InvalidOperationException>(() => r.Process(Chunk(first, 3, 9_601)),
            "timeline gaps reject before sample consumption");
        Throws<ArgumentException>(() => r.Process(Chunk(new byte[] { 0 }, 3, 9_600)),
            "partial PCM16 input rejected");
        var wrongRate = Chunk(first, 3, 9_600) with { SampleRate = 44_100 };
        Throws<ArgumentException>(() => r.Process(wrongRate), "unsupported rate rejected");
        Check(r.InputSamplesConsumed == 9_600 && r.OutputSamplesProduced == 3_200,
            "rejected chunks do not corrupt state");
        r.Reset();
        Check(r.InputSamplesConsumed == 0 && r.OutputSamplesProduced == 0,
            "reset clears session timeline");
        var restarted = r.Process(Chunk(first, 1, 0))!;
        Check(restarted.Data.Span.SequenceEqual(a.Data.Span),
            "reset restores deterministic first-chunk output");

        Console.WriteLine($"PASS: {_passed} streaming resampler checks");
    }
}
