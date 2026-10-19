using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using CaptionLink.Core.Audio;
using CaptionLink.Core.Transcription;
using CaptionLink.Speech.GPULink;
using CaptionLink.Speech.GPULink.Provider;
using GPULink.Client;

var checkedCount = 0;
void Pass(bool yes, string label)
{
    if (!yes) throw new Exception("FAIL: " + label);
    checkedCount++;
    Console.WriteLine("PASS: " + label);
}
async Task MustFail(Func<Task> f, string label)
{
    bool failed = false;
    try { await f(); } catch { failed = true; }
    Pass(failed, label);
}
var connector = new FakeConnector();
var engine = new GPULinkMultitalkerTranscriptionEngine(connector);
var output = new List<TranscriptResult>();
var speakerIds = new List<string>();
engine.ResultReceived += (_, e) => output.Add(e.Result);
var observedActivity = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
engine.ActivityReceived += (_, e) => { speakerIds.Add(e.Activity.SpeakerId); observedActivity.TrySetResult(true); };
await MustFail(() => engine.StartAsync(new TranscriptionConfiguration("en", 48_000)), "reject unnormalized Windows 48k audio");
Pass(connector.OpenCount == 0, "48k rejected before remote allocation");
await engine.StartAsync(new TranscriptionConfiguration("en", 16_000));
Pass(engine.IsRunning && connector.OpenCount == 1, "model readiness gates start");
var fake = connector.Current!;
Pass(fake.SentText.Count == 1 && fake.SentText[0] == MultitalkerWire.StartJson, "application START sent after relay connect");
var pcm = new byte[20_000]; // 0.625 sec, split into bounded frames
await engine.SendAsync(new TranscriptionAudioChunk(0, DateTimeOffset.UtcNow, pcm, 16_000));
Pass(fake.SentBinary.Count == 4 && fake.SentBinary.All(x => x.Length <= 6_416), "audio split into 200ms-bounded frames");
Pass(fake.SentBinary.Select(x => BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(4, 4))).SequenceEqual(new uint[] { 0, 1, 2, 3 }), "audio sequence is monotonic");
Pass(fake.SentBinary.Select(x => BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(8, 4))).SequenceEqual(new uint[] { 0, 3_200, 6_400, 9_600 }), "audio sample offsets are gap-free");
await MustFail(() => engine.SendAsync(new TranscriptionAudioChunk(1, DateTimeOffset.UtcNow, pcm, 48_000)).AsTask(), "reject incompatible SendAsync rate");
var first = "{\"type\":\"transcript\",\"segment_id\":\"seg1\",\"speaker_id\":\"speaker-1\",\"sequence\":1,\"text\":\"hello\",\"is_final\":false,\"result_start_time_seconds\":0,\"result_end_time_seconds\":0.5}";
var second = "{\"type\":\"transcript\",\"segment_id\":\"seg2\",\"speaker_id\":\"speaker-2\",\"sequence\":2,\"text\":\"there\",\"is_final\":true,\"result_start_time_seconds\":0.3,\"result_end_time_seconds\":0.6}";
var activity = "{\"type\":\"speaker_activity\",\"activity_id\":\"act1\",\"speaker_id\":\"speaker-2\",\"sequence\":1,\"is_final\":true,\"start_time_seconds\":0.3,\"end_time_seconds\":0.6}";
fake.Push(first); fake.Push(second); fake.Push(activity);
await observedActivity.Task.WaitAsync(TimeSpan.FromSeconds(3));
Pass(output.Count == 2 && output[0].SpeakerId == "speaker-1" && output[1].SpeakerId == "speaker-2", "two distinct captions delivered");
Pass(output[1].ResultStartTime < output[0].ResultEndTime, "overlapping speaker intervals preserved");
Pass(speakerIds.SequenceEqual(new[] { "speaker-2" }), "speaker activity delivered");
await engine.StopAsync();
Pass(engine.CompletedSuccessfully && !engine.IsRunning, "Stop requires authoritative completed ACK");
Pass(fake.SentText.Last() == MultitalkerWire.EndJson, "Stop sends explicit END");
Pass(fake.Closed, "Stop releases GPULink session");
await engine.DisposeAsync();

var badConnector = new FakeConnector(ready: false);
var badEngine = new GPULinkMultitalkerTranscriptionEngine(badConnector);
await MustFail(() => badEngine.StartAsync(new TranscriptionConfiguration("en", 16_000)), "startup rejects transcript before MODEL_READY");
Pass(badConnector.Current!.Closed, "failed startup closes remote allocation");
await badEngine.DisposeAsync();

var noAckConnector = new FakeConnector(completeOnEnd: false);
var noAckEngine = new GPULinkMultitalkerTranscriptionEngine(noAckConnector);
await noAckEngine.StartAsync(new TranscriptionConfiguration("en", 16_000));
noAckConnector.Current!.FailIncoming();
await MustFail(() => noAckEngine.StopAsync(), "disconnect before END ACK is not success");
Pass(!noAckEngine.CompletedSuccessfully && noAckConnector.Current.Closed, "no false completed state and session cleaned");
await noAckEngine.DisposeAsync();

var cancelConnector = new FakeConnector();
var cancelEngine = new GPULinkMultitalkerTranscriptionEngine(cancelConnector);
await cancelEngine.StartAsync(new TranscriptionConfiguration("en", 16_000));
await cancelEngine.DisposeAsync();
Pass(!cancelEngine.CompletedSuccessfully && cancelConnector.Current!.Closed &&
    cancelConnector.Current.SentText.Count == 1, "dispose cancels without END or success");
Console.WriteLine($"PASS: {checkedCount} offline CaptionLink GPULink provider checks (no GPU/network)");

sealed class FakeConnector(bool ready = true, bool completeOnEnd = true) : IGPULinkMultitalkerConnector
{
    public FakeChannel? Current { get; private set; }
    public int OpenCount { get; private set; }
    public Task<IGPULinkMultitalkerChannel> OpenAsync(CancellationToken cancellationToken = default)
    {
        OpenCount++;
        Current = new FakeChannel(ready, completeOnEnd);
        return Task.FromResult<IGPULinkMultitalkerChannel>(Current);
    }
}

sealed class FakeChannel : IGPULinkMultitalkerChannel
{
    private readonly Channel<GPULinkMessage> _messages = Channel.CreateUnbounded<GPULinkMessage>();
    private readonly bool _completeOnEnd;
    public List<string> SentText { get; } = [];
    public List<byte[]> SentBinary { get; } = [];
    public bool Closed { get; private set; }
    public FakeChannel(bool ready, bool completeOnEnd)
    {
        _completeOnEnd = completeOnEnd;
        Push(ready
            ? "{\"type\":\"captionlink.multitalker.model_ready.v0\",\"sample_rate\":16000,\"max_speakers\":4}"
            : "{\"type\":\"transcript\",\"segment_id\":\"oops\"}");
    }
    public Task SendTextAsync(string text, CancellationToken ct = default)
    {
        SentText.Add(text);
        if (text == MultitalkerWire.EndJson && _completeOnEnd) Push("{\"type\":\"gpulink.session.completed.v1\"}");
        return Task.CompletedTask;
    }
    public Task SendBinaryAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default)
    {
        SentBinary.Add(message.ToArray());
        return Task.CompletedTask;
    }
    public async Task<GPULinkMessage> ReceiveAsync(CancellationToken ct = default)
    {
        var value = await _messages.Reader.ReadAsync(ct);
        return value;
    }
    public void Push(string json) => _messages.Writer.TryWrite(new GPULinkMessage(false, Encoding.UTF8.GetBytes(json)));
    public void FailIncoming() => _messages.Writer.TryComplete(new IOException("client disconnected"));
    public Task CloseAsync() { Closed = true; _messages.Writer.TryComplete(); return Task.CompletedTask; }
    public async ValueTask DisposeAsync() { await CloseAsync(); }
}
