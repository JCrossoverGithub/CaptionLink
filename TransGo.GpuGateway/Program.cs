using System.Net;
using System.Security.Cryptography;
using System.Text;
using TransGo.GpuGateway;
using TransGo.Remote.Protocol;

var builder = WebApplication.CreateBuilder(args);

string gatewayToken =
    Environment.GetEnvironmentVariable("TRANSGO_GATEWAY_TOKEN")
    ?? builder.Configuration["Gateway:Token"]
    ?? string.Empty;

if (string.IsNullOrWhiteSpace(gatewayToken))
{
    throw new InvalidOperationException(
        "Set the TRANSGO_GATEWAY_TOKEN environment variable " +
        "before starting the GPU gateway.");
}

var app = builder.Build();

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

        if (!HasValidBearerToken(
                context.Request,
                gatewayToken))
        {
            context.Response.StatusCode =
                StatusCodes.Status401Unauthorized;

            await context.Response.WriteAsync(
                "A valid bearer token is required.",
                context.RequestAborted);

            return;
        }

        using var socket =
            await context.WebSockets.AcceptWebSocketAsync();

        await TranscriptionWebSocketSession.RunAsync(
            socket,
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