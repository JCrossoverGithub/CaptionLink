using CaptionLink.Remote.Protocol;

namespace CaptionLink.Speech.Remote;

public sealed record RemoteGatewayOptions(
    Uri WebSocketUri,
    string Token)
{
    public const string UrlEnvironmentVariable =
        "CAPTIONLINK_REMOTE_GATEWAY_URL";

    public const string TokenEnvironmentVariable =
        "CAPTIONLINK_REMOTE_GATEWAY_TOKEN";

    private const string LegacyUrlEnvironmentVariable =
        "TRANSGO_REMOTE_GATEWAY_URL";

    private const string LegacyTokenEnvironmentVariable =
        "TRANSGO_REMOTE_GATEWAY_TOKEN";

    public static RemoteGatewayOptions FromEnvironment()
    {
        string? url = ReadEnvironmentVariable(
            UrlEnvironmentVariable,
            LegacyUrlEnvironmentVariable);

        string? token = ReadEnvironmentVariable(
            TokenEnvironmentVariable,
            LegacyTokenEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                $"Set {UrlEnvironmentVariable} to the CaptionLink GPU " +
                "gateway URL before starting remote captions.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Set {TokenEnvironmentVariable} to the CaptionLink GPU " +
                "gateway token before starting remote captions.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException(
                $"{UrlEnvironmentVariable} is not a valid absolute URL.");
        }

        var builder = new UriBuilder(uri);

        builder.Scheme = builder.Scheme.ToLowerInvariant() switch
        {
            "http" => "ws",
            "https" => "wss",
            "ws" => "ws",
            "wss" => "wss",
            _ => throw new InvalidOperationException(
                $"{UrlEnvironmentVariable} must use http, https, ws, or wss.")
        };

        string path = builder.Path.TrimEnd('/');

        if (string.IsNullOrEmpty(path))
        {
            builder.Path = RemoteProtocol.WebSocketPath;
        }
        else if (!string.Equals(
                     path,
                     RemoteProtocol.WebSocketPath,
                     StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{UrlEnvironmentVariable} must be a gateway base URL " +
                $"or end with {RemoteProtocol.WebSocketPath}.");
        }
        else
        {
            builder.Path = path;
        }

        builder.Query = string.Empty;
        builder.Fragment = string.Empty;

        return new RemoteGatewayOptions(
            builder.Uri,
            token.Trim());
    }

    private static string? ReadEnvironmentVariable(
        string currentName,
        string legacyName)
    {
        string? value =
            Environment.GetEnvironmentVariable(
                currentName);

        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Environment.GetEnvironmentVariable(
            legacyName);
    }
}
