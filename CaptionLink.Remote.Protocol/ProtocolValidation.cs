using CaptionLink.Remote.Protocol.Messages;

namespace CaptionLink.Remote.Protocol;

public sealed record ProtocolValidationResult(
    bool IsValid,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ProtocolValidationResult Valid { get; } =
        new(true, null, null);

    public static ProtocolValidationResult Invalid(
        string errorCode,
        string errorMessage)
    {
        return new(false, errorCode, errorMessage);
    }
}

public static class ProtocolValidation
{
    public static ProtocolValidationResult Validate(
        StartSessionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.ProtocolVersion != RemoteProtocol.CurrentVersion)
        {
            return ProtocolValidationResult.Invalid(
                RemoteErrorCodes.UnsupportedProtocol,
                $"Protocol version {message.ProtocolVersion} is unsupported. " +
                $"Version {RemoteProtocol.CurrentVersion} is required.");
        }

        if (string.IsNullOrWhiteSpace(message.RequestId))
        {
            return InvalidMessage("RequestId is required.");
        }

        if (message.Client is null ||
            string.IsNullOrWhiteSpace(message.Client.Name) ||
            string.IsNullOrWhiteSpace(message.Client.Version) ||
            string.IsNullOrWhiteSpace(message.Client.Platform))
        {
            return InvalidMessage(
                "Client name, version, and platform are required.");
        }

        if (message.Audio is null)
        {
            return InvalidAudio("An audio format is required.");
        }

        if (!string.Equals(
                message.Audio.Encoding,
                RemoteProtocol.RequiredAudioEncoding,
                StringComparison.OrdinalIgnoreCase) ||
            message.Audio.SampleRateHz !=
                RemoteProtocol.RequiredSampleRateHz ||
            message.Audio.Channels !=
                RemoteProtocol.RequiredChannels ||
            message.Audio.BitsPerSample !=
                RemoteProtocol.RequiredBitsPerSample)
        {
            return InvalidAudio(
                "Audio must be mono 16 kHz PCM signed 16-bit little-endian.");
        }

        if (message.Options is null ||
            string.IsNullOrWhiteSpace(message.Options.Language))
        {
            return InvalidMessage("A transcription language is required.");
        }

        return ProtocolValidationResult.Valid;
    }

    private static ProtocolValidationResult InvalidMessage(string message)
    {
        return ProtocolValidationResult.Invalid(
            RemoteErrorCodes.InvalidMessage,
            message);
    }

    private static ProtocolValidationResult InvalidAudio(string message)
    {
        return ProtocolValidationResult.Invalid(
            RemoteErrorCodes.InvalidAudioFormat,
            message);
    }
}