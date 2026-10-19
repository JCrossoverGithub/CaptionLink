using System.Buffers.Binary;
using CaptionLink.Speech.GPULink;

int tests = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    tests++;
}
void MustThrow(Action action, string name)
{
    bool threw = false;
    try { action(); }
    catch (FormatException) { threw = true; }
    catch (InvalidOperationException) { threw = true; }
    catch (ArgumentOutOfRangeException) { threw = true; }
    Check(threw, name);
}
var pcm = new byte[] { 0x00, 0x80, 0xFF, 0x7F };
byte[] packet = MultitalkerWire.EncodePcm(4, 32, pcm);
Check(packet.Length == 20 && packet.AsSpan(0,4).SequenceEqual("ASR1"u8) &&
      BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == 4 &&
      BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 32 &&
      BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)) == 2 &&
      packet.AsSpan(16).SequenceEqual(pcm), "ASR1 exact framing and LE audio");
MustThrow(() => MultitalkerWire.EncodePcm(0, 0, new byte[0]), "no empty PCM");
MustThrow(() => MultitalkerWire.EncodePcm(0, 0, new byte[65522]), "adapter frame limit");
MustThrow(() => MultitalkerWire.EncodePcm(0, MultitalkerWire.MaxSamples, pcm), "600s cap");
Check(MultitalkerWire.StartJson.Contains("16000") && MultitalkerWire.EndJson.Contains("gpulink.session.end.v1"), "wire control values");
var events = new MultitalkerEventTracker();
const string ready = "{\"type\":\"captionlink.multitalker.model_ready.v0\",\"sample_rate\":16000,\"max_speakers\":4}";
const string first = "{\"type\":\"transcript\",\"segment_id\":\"seg1\",\"speaker_id\":\"speaker-1\",\"sequence\":1,\"text\":\"hello\",\"is_final\":false,\"result_start_time_seconds\":1.2,\"result_end_time_seconds\":3.0}";
const string final = "{\"type\":\"transcript\",\"segment_id\":\"seg1\",\"speaker_id\":\"speaker-1\",\"sequence\":2,\"text\":\"hello there\",\"is_final\":true,\"result_start_time_seconds\":1.2,\"result_end_time_seconds\":3.1}";
const string overlapping = "{\"type\":\"transcript\",\"segment_id\":\"seg2\",\"speaker_id\":\"speaker-2\",\"sequence\":3,\"text\":\"goodbye\",\"is_final\":true,\"result_start_time_seconds\":2.4,\"result_end_time_seconds\":4.0}";
const string activity = "{\"type\":\"speaker_activity\",\"activity_id\":\"act1\",\"speaker_id\":\"speaker-1\",\"sequence\":1,\"is_final\":true,\"start_time_seconds\":1.2,\"end_time_seconds\":3.1}";
MustThrow(() => new MultitalkerEventTracker().Accept(first), "reject output before model ready");
Check(events.Accept(ready).Kind == MultitalkerEventKind.Ready && events.IsReady, "model ready");
var result = events.Accept(first).Transcript!;
Check(result.SpeakerId == "speaker-1" && !result.IsFinal && result.ResultStartTime == TimeSpan.FromSeconds(1.2), "interim speaker attribution");
Check(events.Accept(final).Transcript!.IsFinal, "final revision");
var other = events.Accept(overlapping).Transcript!;
Check(other.SpeakerId == "speaker-2" && other.ResultStartTime < result.ResultEndTime, "overlapping speakers retained");
Check(events.Accept(activity).Activity!.SpeakerId == "speaker-1", "speaker-activity contract");
MustThrow(() => events.Accept(final), "no revision after final");
MustThrow(() => events.Accept("{\"type\":\"speaker_activity\",\"type\":\"transcript\"}"), "duplicate keys rejected");
MustThrow(() => MultitalkerWire.ParseEvent("{\"type\":\"transcript\",\"speaker_id\":\"speaker-99\"}"), "invalid speaker rejected");
MustThrow(() => MultitalkerWire.ParseEvent("{\"type\":\"gpulink.relay.ready.v2\"}"), "relay control not interpreted as model ready");
Check(events.Accept("{\"type\":\"gpulink.session.completed.v1\"}").Kind == MultitalkerEventKind.Completed && events.IsCompleted,
      "explicit authoritative completion");
MustThrow(() => events.Accept(ready), "no output after completion");
Console.WriteLine($"PASS: {tests} offline wire-contract checks");
