using System.Net.Http.Json;

namespace TransGo.Speech.Parakeet;

/// <summary>
/// Checks whether the local Parakeet service is running and
/// ready to accept streaming transcription sessions.
/// </summary>
public sealed class ParakeetServiceHealthClient
    : IDisposable
{
    private static readonly Uri DefaultHealthUri =
        new("http://localhost:8765/health");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    private bool _disposed;

    public ParakeetServiceHealthClient()
        : this(
            new HttpClient
            {
                Timeout =
                    TimeSpan.FromSeconds(3)
            },
            ownsHttpClient: true)
    {
    }

    public ParakeetServiceHealthClient(
        HttpClient httpClient)
        : this(
            httpClient,
            ownsHttpClient: false)
    {
    }

    private ParakeetServiceHealthClient(
        HttpClient httpClient,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(
            httpClient);

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<ParakeetServiceHealth?>
        GetHealthAsync(
            CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        try
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync(
                    DefaultHealthUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<ParakeetServiceHealth>(
                    cancellationToken:
                        cancellationToken);
        }
        catch (HttpRequestException)
        {
            /*
             * The service is not running, localhost refused
             * the connection, or the response was invalid.
             */
            return null;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            /*
             * The three-second HTTP timeout elapsed.
             */
            return null;
        }
    }

    public async Task<bool> IsReadyAsync(
        CancellationToken cancellationToken = default)
    {
        ParakeetServiceHealth? health =
            await GetHealthAsync(
                cancellationToken);

        return health?.IsReady == true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}