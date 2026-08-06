using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace TransGo.Diarization.Sortformer;

/// <summary>
/// Reads the health endpoint exposed by the local Sortformer
/// service.
/// </summary>
internal sealed class SortformerServiceHealthClient
    : IDisposable
{
    private static readonly Uri HealthUri =
        new("http://127.0.0.1:8766/health");

    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient =
        new()
        {
            Timeout = RequestTimeout,
        };

    private bool _disposed;

    public async Task<SortformerServiceHealth?>
        GetHealthAsync(
            CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        try
        {
            return await _httpClient
                .GetFromJsonAsync<SortformerServiceHealth>(
                    HealthUri,
                    cancellationToken);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException exception)
        {
            Debug.WriteLine(
                "Invalid Sortformer health JSON: " +
                exception);

            return null;
        }
        catch (NotSupportedException exception)
        {
            Debug.WriteLine(
                "Unsupported Sortformer health response: " +
                exception);

            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();

        GC.SuppressFinalize(this);
    }
}
