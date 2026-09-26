using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace CaptionLink.Diarization.Nemotron;

/// <summary>
/// Reads the health endpoint exposed by the local Nemotron
/// service.
/// </summary>
internal sealed class NemotronServiceHealthClient
    : IDisposable
{
    private static readonly Uri HealthUri =
        new("http://127.0.0.1:8767/health");

    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(2);

    private readonly HttpClient _httpClient =
        new()
        {
            Timeout = RequestTimeout,
        };

    private bool _disposed;

    public async Task<NemotronServiceHealth?>
        GetHealthAsync(
            CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        try
        {
            return await _httpClient
                .GetFromJsonAsync<NemotronServiceHealth>(
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
                "Invalid Nemotron health JSON: " +
                exception);

            return null;
        }
        catch (NotSupportedException exception)
        {
            Debug.WriteLine(
                "Unsupported Nemotron health response: " +
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
