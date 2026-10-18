using System.Buffers.Binary;
using System.Runtime.InteropServices;
using CaptionLink.Audio.Processing;
using CaptionLink.Core.Audio;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

static TranscriptionAudioChunk Chunk(long sequence, int samplesPerChunk = 4800)
{
    byte[] pcm = new byte[samplesPerChunk * 2];
    for (int n = 0; n < samplesPerChunk; n++)
    {
        int inputSample = checked((int)(sequence * samplesPerChunk + n));
        short amplitude = (short)Math.Round(12_000 * Math.Sin(inputSample * Math.PI / 12));
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(n * 2, 2), amplitude);
    }
    return new TranscriptionAudioChunk(sequence, DateTimeOffset.UnixEpoch, pcm, 48_000)
    {
        SessionStartTime = TimeSpan.FromTicks(
            checked(sequence * samplesPerChunk * TimeSpan.TicksPerSecond / 48_000))
    };
}

// Ordered, serialized async dispatch and chunk-independent audio data.
var received = new List<TranscriptionAudioChunk>();
int concurrentSends = 0;
int peakSends = 0;
await using (var pump = new OrderedPcm16StreamingPump(async (chunk, ct) =>
{
    int active = Interlocked.Increment(ref concurrentSends);
    peakSends = Math.Max(peakSends, active);
    try
    {
        await Task.Yield();
        received.Add(chunk);
    }
    finally { Interlocked.Decrement(ref concurrentSends); }
}, capacityChunks: 16))
{
    for (int i = 0; i < 8; i++) pump.Enqueue(Chunk(i));
    await pump.DrainAsync();
}
Check(peakSends == 1, "network sends are strictly serialized");
Check(received.Count == 8, "all eight queued audio chunks are delivered");
Check(received.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(0, 8).Select(x => (long)x)),
    "input sequence order preserved");
Check(received.All(x => x.SampleRate == 16_000 && x.ByteCount == 3200),
    "48 kHz 100 ms chunks become 16 kHz 100 ms chunks");
Check(received.Select((chunk, index) => (chunk, index)).All(x =>
    x.chunk.SessionStartTime == TimeSpan.FromMilliseconds(x.index * 100)),
    "resampled timestamps remain contiguous");

var standalone = new StreamingPcm16Resampler48To16();
var reference = Enumerable.Range(0, 8).Select(i => standalone.Process(Chunk(i))!.Data.ToArray()).ToArray();
bool matchesFIR = received.Count == reference.Length;
for (int i = 0; i < received.Count && matchesFIR; i++)
    matchesFIR &= received[i].Data.ToArray().SequenceEqual(reference[i]);
Check(matchesFIR, "pump output matches standalone verified FIR converter byte-for-byte");

// Enqueue copies capture data before a blocked sender can consume it.
var copyObserved = new List<TranscriptionAudioChunk>();
var copyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var copyRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var pump = new OrderedPcm16StreamingPump(async (chunk, ct) =>
{
    if (chunk.Sequence == 0)
    {
        copyStarted.TrySetResult(true);
        await copyRelease.Task.WaitAsync(ct);
    }
    copyObserved.Add(chunk);
}))
{
    pump.Enqueue(Chunk(0));
    await copyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    TranscriptionAudioChunk original = Chunk(1);
    var resampler = new StreamingPcm16Resampler48To16();
    _ = resampler.Process(Chunk(0));
    byte[] expectedSecond = resampler.Process(Chunk(1))!.Data.ToArray();
    pump.Enqueue(original);
    Check(MemoryMarshal.TryGetArray(original.Data, out ArraySegment<byte> captureArray),
        "source PCM exposes the original capture buffer");
    captureArray.Array!.AsSpan(captureArray.Offset, captureArray.Count).Clear();
    copyRelease.TrySetResult(true);
    await pump.DrainAsync();
    Check(copyObserved.Count == 2 &&
          copyObserved[1].Data.ToArray().SequenceEqual(expectedSecond),
        "queued audio is owned, not changed by capture-buffer reuse");
}

// Deliberate sender stall fills a bounded queue; overflow MUST fail closed.
var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var pump = new OrderedPcm16StreamingPump(async (_, ct) =>
{
    started.TrySetResult(true);
    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
}, capacityChunks: 1))
{
    pump.Enqueue(Chunk(0));
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    pump.Enqueue(Chunk(1));
    bool overflow = false;
    try { pump.Enqueue(Chunk(2)); }
    catch (InvalidOperationException) { overflow = true; }
    Check(overflow, "bounded queue rejects overflow instead of dropping PCM");
    bool unsuccessful = false;
    try { await pump.DrainAsync(); }
    catch (Exception) { unsuccessful = true; }
    Check(unsuccessful, "overflow cannot be reported as drained success");
}

// Gap is fatal and never concealed with synthetic zeros.
await using (var pump = new OrderedPcm16StreamingPump((_, _) => ValueTask.CompletedTask))
{
    pump.Enqueue(Chunk(0));
    pump.Enqueue(Chunk(2));
    bool failed = false;
    try { await pump.DrainAsync(); }
    catch (InvalidOperationException) { failed = true; }
    Check(failed, "noncontiguous sequence/timeline fails before model completion");
}

// Cancellation does not imply a successful drain.
var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var pump = new OrderedPcm16StreamingPump(async (_, ct) =>
{
    blocked.TrySetResult(true);
    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
}))
{
    pump.Enqueue(Chunk(0));
    await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    pump.Abort();
    bool aborted = false;
    try { await pump.DrainAsync(); }
    catch (Exception) { aborted = true; }
    Check(aborted, "aborted audio path cannot drain as success");
}

Console.WriteLine("PASS: ordered 48-to-16 kHz pump contract checks");
