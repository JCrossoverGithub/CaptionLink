using System.Buffers.Binary;

namespace CaptionLink.Remote.Protocol;

public sealed record RemoteAudioChunk(
    long Sequence,
    long CaptureTimestampMilliseconds,
    byte[] PcmBytes);

public sealed class RemoteProtocolException : Exception
{
    public RemoteProtocolException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class AudioChunkCodec
{
    // The first four bytes appear as ASCII "TGAC" on the wire.
    private const uint Magic = 0x43414754;

    public static byte[] Encode(
        long sequence,
        long captureTimestampMilliseconds,
        ReadOnlySpan<byte> pcmBytes)
    {
        ValidateMetadata(sequence, captureTimestampMilliseconds);
        ValidatePcmPayload(pcmBytes.Length);

        byte[] packet =
            new byte[RemoteProtocol.AudioChunkHeaderSize + pcmBytes.Length];

        Span<byte> header =
            packet.AsSpan(0, RemoteProtocol.AudioChunkHeaderSize);

        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], Magic);
        header[4] = RemoteProtocol.CurrentVersion;
        header[5] = 0; // Reserved flags.

        BinaryPrimitives.WriteUInt16LittleEndian(
            header[6..8],
            RemoteProtocol.AudioChunkHeaderSize);

        BinaryPrimitives.WriteInt64LittleEndian(header[8..16], sequence);

        BinaryPrimitives.WriteInt64LittleEndian(
            header[16..24],
            captureTimestampMilliseconds);

        pcmBytes.CopyTo(packet.AsSpan(RemoteProtocol.AudioChunkHeaderSize));

        return packet;
    }

    public static RemoteAudioChunk Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < RemoteProtocol.AudioChunkHeaderSize)
        {
            throw InvalidAudio(
                $"Audio packet must contain at least " +
                $"{RemoteProtocol.AudioChunkHeaderSize} bytes.");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(packet[0..4]);

        if (magic != Magic)
        {
            throw InvalidAudio("Audio packet has an invalid magic value.");
        }

        int protocolVersion = packet[4];

        if (protocolVersion != RemoteProtocol.CurrentVersion)
        {
            throw new RemoteProtocolException(
                RemoteErrorCodes.UnsupportedProtocol,
                $"Audio packet uses protocol version {protocolVersion}; " +
                $"version {RemoteProtocol.CurrentVersion} is required.");
        }

        if (packet[5] != 0)
        {
            throw InvalidAudio(
                "Audio packet contains unsupported flag values.");
        }

        int headerSize =
            BinaryPrimitives.ReadUInt16LittleEndian(packet[6..8]);

        if (headerSize != RemoteProtocol.AudioChunkHeaderSize)
        {
            throw InvalidAudio(
                $"Audio packet header size must be " +
                $"{RemoteProtocol.AudioChunkHeaderSize} bytes.");
        }

        long sequence =
            BinaryPrimitives.ReadInt64LittleEndian(packet[8..16]);

        long captureTimestampMilliseconds =
            BinaryPrimitives.ReadInt64LittleEndian(packet[16..24]);

        ValidateMetadata(sequence, captureTimestampMilliseconds);

        ReadOnlySpan<byte> payload = packet[headerSize..];
        ValidatePcmPayload(payload.Length);

        return new RemoteAudioChunk(
            sequence,
            captureTimestampMilliseconds,
            payload.ToArray());
    }

    private static void ValidateMetadata(
        long sequence,
        long captureTimestampMilliseconds)
    {
        if (sequence < 0)
        {
            throw InvalidAudio(
                "Audio chunk sequence cannot be negative.");
        }

        if (captureTimestampMilliseconds < 0)
        {
            throw InvalidAudio(
                "Audio capture timestamp cannot be negative.");
        }
    }

    private static void ValidatePcmPayload(int payloadLength)
    {
        if (payloadLength == 0)
        {
            throw InvalidAudio("Audio chunk payload cannot be empty.");
        }

        if (payloadLength > RemoteProtocol.MaxAudioChunkPayloadBytes)
        {
            throw InvalidAudio(
                $"Audio chunk payload cannot exceed " +
                $"{RemoteProtocol.MaxAudioChunkPayloadBytes} bytes.");
        }

        // Mono signed 16-bit PCM requires complete two-byte samples.
        if (payloadLength % 2 != 0)
        {
            throw InvalidAudio(
                "PCM signed 16-bit payload must contain an even number of bytes.");
        }
    }

    private static RemoteProtocolException InvalidAudio(string message)
    {
        return new RemoteProtocolException(
            RemoteErrorCodes.InvalidAudioFormat,
            message);
    }
}