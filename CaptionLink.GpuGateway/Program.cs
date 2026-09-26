using System.Net;
using System.Security.Cryptography;
using System.Text;
using CaptionLink.Core.Runtime;
using CaptionLink.GpuGateway;
using CaptionLink.Remote.Protocol;
using CaptionLink.Speech.Parakeet;

var builder = WebApplication.CreateBuilder(args);

string gatewayToken =
    Environment.GetEnvironmentVariable("CAPTIONLINK_GATEWAY_TOKEN")
    ?? Environment.GetEnvironmentVariable("TRANSGO_GATEWAY_TOKEN")
    ?? builder.Configuration["Gateway:Token"]
    ?? string.Empty;

if (string.IsNullOrWhiteSpace(gatewayToken))
{
    throw new InvalidOperationException(
        "Set the CAPTIONLINK_GATEWAY_TOKEN environment variable " +
        "before starting the GPU gateway.");
}

var app = builder.Build();

ILocalServiceRuntime localServiceRuntime =
    new WslLocalServiceRuntime();

const string BrowserWebSocketSubprotocol =
    "captionlink-v1";

const string BrowserTokenSubprotocolPrefix =
    "captionlink-token.";

const string LegacyBrowserWebSocketSubprotocol =
    "transgo-v1";

const string LegacyBrowserTokenSubprotocolPrefix =
    "transgo-token.";

app.UseWebSockets(
    new WebSocketOptions
    {
        KeepAliveInterval = TimeSpan.FromSeconds(20)
    });

app.MapGet(
    "/health",
    () => Results.Ok(
        new
        {
            status = "healthy",
            engine = "parakeet",
            protocolVersion = RemoteProtocol.CurrentVersion,
            serverTimeUtc = DateTimeOffset.UtcNow
        }));

app.MapGet(
    RemoteProtocol.WebSocketPath,
    async context =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode =
                StatusCodes.Status400BadRequest;

            await context.Response.WriteAsync(
                "A WebSocket connection is required.",
                context.RequestAborted);

            return;
        }

        bool hasValidBearerToken =
            HasValidBearerToken(
                context.Request,
                gatewayToken);

        string? browserWebSocketSubprotocol = null;

        if (HasValidBrowserWebSocketToken(
                context.Request,
                gatewayToken,
                BrowserWebSocketSubprotocol,
                BrowserTokenSubprotocolPrefix))
        {
            browserWebSocketSubprotocol =
                BrowserWebSocketSubprotocol;
        }
        else if (HasValidBrowserWebSocketToken(
                     context.Request,
                     gatewayToken,
                     LegacyBrowserWebSocketSubprotocol,
                     LegacyBrowserTokenSubprotocolPrefix))
        {
            browserWebSocketSubprotocol =
                LegacyBrowserWebSocketSubprotocol;
        }

        bool hasValidBrowserToken =
            browserWebSocketSubprotocol is not null;

        if (!hasValidBearerToken &&
            !hasValidBrowserToken)
        {
            context.Response.StatusCode =
                StatusCodes.Status401Unauthorized;

            await context.Response.WriteAsync(
                "A valid gateway token is required.",
                context.RequestAborted);

            return;
        }

        using var socket = hasValidBrowserToken
            ? await context.WebSockets.AcceptWebSocketAsync(
                browserWebSocketSubprotocol!)
            : await context.WebSockets.AcceptWebSocketAsync();

        await TranscriptionWebSocketSession.RunAsync(
            socket,
            () => new ParakeetStreamingTranscriptionEngine(
                ParakeetStreamingProfile.Accurate,
                new ParakeetServiceLauncher(
                    localServiceRuntime)),
            context.RequestAborted);
    });

app.Run();

static bool HasValidBearerToken(
    HttpRequest request,
    string expectedToken)
{
    string authorization =
        request.Headers.Authorization.ToString();

    const string bearerPrefix = "Bearer ";

    if (!authorization.StartsWith(
            bearerPrefix,
            StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    string suppliedToken =
        authorization[bearerPrefix.Length..].Trim();

    byte[] expectedBytes =
        Encoding.UTF8.GetBytes(expectedToken);

    byte[] suppliedBytes =
        Encoding.UTF8.GetBytes(suppliedToken);

    return expectedBytes.Length == suppliedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(
            expectedBytes,
            suppliedBytes);
}
static bool HasValidBrowserWebSocketToken(
    HttpRequest request,
    string expectedToken,
    string browserWebSocketSubprotocol,
    string browserTokenSubprotocolPrefix)
{
    string requestedProtocols =
        request.Headers["Sec-WebSocket-Protocol"].ToString();

    if (string.IsNullOrWhiteSpace(requestedProtocols))
    {
        return false;
    }

    string[] protocols = requestedProtocols.Split(
        ',',
        StringSplitOptions.TrimEntries |
        StringSplitOptions.RemoveEmptyEntries);

    if (!protocols.Contains(
            browserWebSocketSubprotocol,
            StringComparer.Ordinal))
    {
        return false;
    }

    string? encodedToken = null;

    foreach (string protocol in protocols)
    {
        if (!protocol.StartsWith(
                browserTokenSubprotocolPrefix,
                StringComparison.Ordinal))
        {
            continue;
        }

        if (encodedToken is not null)
        {
            return false;
        }

        encodedToken =
            protocol[browserTokenSubprotocolPrefix.Length..];
    }

    if (string.IsNullOrWhiteSpace(encodedToken))
    {
        return false;
    }

    byte[] suppliedBytes;

    try
    {
        suppliedBytes = DecodeBase64Url(encodedToken);
    }
    catch (FormatException)
    {
        return false;
    }

    byte[] expectedBytes =
        Encoding.UTF8.GetBytes(expectedToken);

    return expectedBytes.Length == suppliedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(
            expectedBytes,
            suppliedBytes);
}

static byte[] DecodeBase64Url(string value)
{
    string base64 = value
        .Replace('-', '+')
        .Replace('_', '/');

    int remainder = base64.Length % 4;

    if (remainder == 1)
    {
        throw new FormatException(
            "The browser token is not valid Base64URL data.");
    }

    if (remainder > 0)
    {
        base64 = base64.PadRight(
            base64.Length + (4 - remainder),
            '=');
    }

    return Convert.FromBase64String(base64);
}
