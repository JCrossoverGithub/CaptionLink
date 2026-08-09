using System.Text.Json;
using System.Text.Json.Serialization;
using TransGo.Remote.Protocol.Messages;

namespace TransGo.Remote.Protocol;

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static byte[] Serialize(IRemoteProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return JsonSerializer.SerializeToUtf8Bytes(
            message,
            message.GetType(),
            Options);
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> json)
        where T : IRemoteProtocolMessage
    {
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new JsonException(
                $"Could not deserialize {typeof(T).Name}.");
    }

    public static string GetMessageType(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("type", out JsonElement type))
        {
            throw new JsonException(
                "Protocol message does not contain a 'type' property.");
        }

        return type.GetString()
            ?? throw new JsonException(
                "Protocol message 'type' cannot be null.");
    }
}