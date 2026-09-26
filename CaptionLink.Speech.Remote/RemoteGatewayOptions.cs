using CaptionLink.Remote.Protocol;

namespace CaptionLink.Speech.Remote;

public sealed record RemoteGatewayOptions(
    Uri WebSocketUri,
    string Token)
{
    public const string UrlEnvironmentVariable =
        "TRANSGO_REMOTE_GATEWAY_URL";

    public const string TokenEnvironmentVariable =
        "TRANSGO_REMOTE_GATEWAY_TOKEN";

    public static RemoteGatewayOptions FromEnvironment()
    {
        string? url = Environment.GetEnvironmentVariable(
            UrlEnvironmentVariable);

        string? token = Environment.GetEnvironmentVariable(
            TokenEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                $"Set {UrlEnvironmentVariable} to the TransGo GPU " +
                "gateway URL before starting remote captions.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Set {TokenEnvironmentVariable} to the TransGo GPU " +
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
}
