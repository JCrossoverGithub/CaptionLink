using System.Text.Json;
using TransGo.Remote.Protocol.Messages;

namespace TransGo.Remote.Protocol.Tests;

public sealed class RemoteProtocolTests
{
    [Fact]
    public void JsonSerialization_IncludesMessageType()
    {
        StartSessionMessage message = CreateValidStartMessage();

        byte[] json = ProtocolJson.Serialize(message);

        Assert.Equal(
            RemoteMessageTypes.StartSession,
            ProtocolJson.GetMessageType(json));
    }

    [Fact]
    public void StartSessionJson_RoundTrips()
    {
        StartSessionMessage original = CreateValidStartMessage();

        byte[] json = ProtocolJson.Serialize(original);
        StartSessionMessage restored =
            ProtocolJson.Deserialize<StartSessionMessage>(json);

        Assert.Equal(original.ProtocolVersion, restored.ProtocolVersion);
        Assert.Equal(original.RequestId, restored.RequestId);
        Assert.Equal(original.Client, restored.Client);
        Assert.Equal(original.Audio, restored.Audio);
        Assert.Equal(original.Options, restored.Options);
    }

    [Fact]
    public void GetMessageType_RejectsMissingType()
    {
        byte[] json = "{}"u8.ToArray();

        Assert.Throws<JsonException>(
            () => ProtocolJson.GetMessageType(json));
    }

    [Fact]
    public void AudioChunk_RoundTrips()
    {
        byte[] pcmBytes = [1, 2, 3, 4, 5, 6];

        byte[] packet = AudioChunkCodec.Encode(
            sequence: 12,
            captureTimestampMilliseconds: 1_250,
            pcmBytes);

        RemoteAudioChunk decoded = AudioChunkCodec.Decode(packet);

        Assert.Equal(12, decoded.Sequence);
        Assert.Equal(1_250, decoded.CaptureTimestampMilliseconds);
        Assert.Equal(pcmBytes, decoded.PcmBytes);
    }

    [Fact]
    public void AudioChunk_RejectsOddLengthPcm()
    {
        RemoteProtocolException exception =
            Assert.Throws<RemoteProtocolException>(
                () => AudioChunkCodec.Encode(0, 0, [1, 2, 3]));

        Assert.Equal(
            RemoteErrorCodes.InvalidAudioFormat,
            exception.Code);
    }

    [Fact]
    public void AudioChunk_RejectsUnsupportedProtocolVersion()
    {
        byte[] packet = AudioChunkCodec.Encode(0, 0, [1, 2]);
        packet[4] = 99;

        RemoteProtocolException exception =
            Assert.Throws<RemoteProtocolException>(
                () => AudioChunkCodec.Decode(packet));

        Assert.Equal(
            RemoteErrorCodes.UnsupportedProtocol,
            exception.Code);
    }

    [Fact]
    public void StartSessionValidation_AcceptsRequiredFormat()
    {
        ProtocolValidationResult result =
            ProtocolValidation.Validate(CreateValidStartMessage());

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void StartSessionValidation_RejectsWrongSampleRate()
    {
        StartSessionMessage valid = CreateValidStartMessage();

        StartSessionMessage invalid = valid with
        {
            Audio = valid.Audio with
            {
                SampleRateHz = 48_000
            }
        };

        ProtocolValidationResult result =
            ProtocolValidation.Validate(invalid);

        Assert.False(result.IsValid);
        Assert.Equal(
            RemoteErrorCodes.InvalidAudioFormat,
            result.ErrorCode);
    }

    private static StartSessionMessage CreateValidStartMessage()
    {
        return new StartSessionMessage(
            ProtocolVersion: RemoteProtocol.CurrentVersion,
            RequestId: "request-001",
            Client: new RemoteClientInfo(
                Name: "TransGo Test Client",
                Version: "1.0.0",
                Platform: "Windows"),
            Audio: new RemoteAudioFormat(
                Encoding: RemoteProtocol.RequiredAudioEncoding,
                SampleRateHz: RemoteProtocol.RequiredSampleRateHz,
                Channels: RemoteProtocol.RequiredChannels,
                BitsPerSample: RemoteProtocol.RequiredBitsPerSample),
            Options: new RemoteSessionOptions(
                Language: "en-US",
                EnableInterimResults: true));
    }
}