using System.Diagnostics;
using System.IO;
using CaptionLink.Audio.Processing;
using CaptionLink.Core.Audio;
using CaptionLink.Core.Diarization;
using CaptionLink.Core.Transcription;
using CaptionLink.Speech.GPULink.Provider;
using GPULink.Client;

namespace CaptionLink.Windows;

/// <summary>
/// Opt-in Windows application composition, not part of the GPULink SDK.
/// Converts the existing 48k mono PCM16 capture chunks into ordered 16k frames.
/// Owns the GPULink client, workload provider and bounded audio pump.
/// </summary>
public sealed class GPULinkDesktopTranscriptionEngine : ITranscriptionEngine, ISpeakerActivitySource
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private GPULinkClient? _client;
    private GPULinkMultitalkerTranscriptionEngine? _provider;
    private OrderedPcm16StreamingPump? _pump;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private int _faultReported;
    private int _disposed;
    private int _started; // read on capture and background receive-monitor threads

    public event EventHandler<TranscriptResultEventArgs>? ResultReceived;
    public event EventHandler<SpeakerActivityEventArgs>? ActivityReceived;
    // Raised at most once for a streaming-path fault. Never contains bearer credentials.
    public event Action<Exception>? StreamFailed;

    public bool IsRunning => Volatile.Read(ref _started) != 0 && Volatile.Read(ref _provider)?.IsRunning == true;

    public async Task StartAsync(
        TranscriptionConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SampleRate != 48_000)
            throw new NotSupportedException("GPULink desktop capture currently requires 48 kHz mono PCM16.");

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _started) != 0 || _client is not null)
                throw new InvalidOperationException("GPULink desktop transcription is already started.");

            // Values are read ONLY after the user opts into this provider.
            // Never log or persist the bearer token.
            string origin = RequiredSetting("GPULINK_CONTROL_PLANE_ORIGIN");
            string bearer = RequiredSetting("GPULINK_CLIENT_BEARER");
            string projectId = RequiredSetting("GPULINK_PROJECT_ID");
            var client = new GPULinkClient(origin, bearer);
            _client = client;
            var connector = new NativeMultitalkerConnector(client, projectId);
            var provider = new GPULinkMultitalkerTranscriptionEngine(connector);
            _provider = provider;
            provider.ResultReceived += RelayResult;
            provider.ActivityReceived += RelayActivity;

            try
            {
                await provider.StartAsync(configuration with { SampleRate = 16_000 },
                    cancellationToken).ConfigureAwait(false);
                _pump = new OrderedPcm16StreamingPump(async (audio, ct) =>
                {
                    try { await provider.SendAsync(audio, ct).ConfigureAwait(false); }
                    catch (Exception error) when (!ct.IsCancellationRequested)
                    {
                        ReportFault(error);
                        throw;
                    }
                }, capacityChunks: 12);
                Volatile.Write(ref _started, 1);
                var monitorCancellation = new CancellationTokenSource();
                _monitorCancellation = monitorCancellation;
                _monitorTask = ObserveProviderAsync(provider, monitorCancellation.Token);
            }
            catch
            {
                await CleanupAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private static string RequiredSetting(string key)
    {
        string? value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"GPULink setup missing required environment variable {key}.");
        return value.Trim();
    }

    // A receive-side disconnect can stop the provider before another PCM send.
    // Detect that condition without waiting for queue overflow or a user Stop.
    private async Task ObserveProviderAsync(
        GPULinkMultitalkerTranscriptionEngine provider,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _started) != 0 && !provider.IsRunning)
                {
                    ReportFault(new IOException(
                        "GPULink worker disconnected before desktop stream completion."));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    // Completes synchronously: WASAPI's audio callback never awaits network I/O.
    public ValueTask SendAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (Volatile.Read(ref _started) == 0 || _pump is null)
                throw new InvalidOperationException("GPULink desktop audio pump is not running.");
            _pump.Enqueue(chunk);
            return ValueTask.CompletedTask;
        }
        catch (Exception error)
        {
            ReportFault(error);
            throw;
        }
    }

    // Draining is mandatory before sending workload END and accepting a completed ACK.
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var provider = _provider;
            var pump = _pump;
            if (Volatile.Read(ref _started) == 0 || provider is null || pump is null) return;
            Volatile.Write(ref _started, 0);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await pump.DrainAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                pump.Abort();
                throw new TimeoutException("GPULink audio could not drain within 20 seconds; session must not report successful completion.");
            }
            // Do not send END if any enqueued sample was lost, the network send
            // failed, or the receive-side worker disconnected prematurely.
            if (Volatile.Read(ref _faultReported) != 0)
                throw new IOException("GPULink streaming failed; no completion may be inferred.");
            await provider.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private void RelayResult(object? sender, TranscriptResultEventArgs args)
        => ResultReceived?.Invoke(this, args);

    private void RelayActivity(object? sender, SpeakerActivityEventArgs args)
        => ActivityReceived?.Invoke(this, args);

    private void ReportFault(Exception error)
    {
        if (Interlocked.Exchange(ref _faultReported, 1) != 0) return;
        try { StreamFailed?.Invoke(error); }
        catch (Exception notificationError)
        {
            Debug.WriteLine("GPULink streaming fault notification failed: " + notificationError);
        }
    }

    private async Task CleanupAsync()
    {
        Volatile.Write(ref _started, 0);
        _monitorCancellation?.Cancel();
        if (_monitorTask is not null)
        {
            try { await _monitorTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception monitorError)
            {
                Debug.WriteLine("GPULink receive monitor cleanup failed: " + monitorError);
            }
        }
        _monitorTask = null;
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;

        var pump = _pump;
        _pump = null;
        var provider = _provider;
        _provider = null;
        var client = _client;
        _client = null;
        Exception? error = null;
        if (pump is not null)
        {
            try
            {
                pump.Abort();
                await pump.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) { error ??= e; }
        }
        if (provider is not null)
        {
            provider.ResultReceived -= RelayResult;
            provider.ActivityReceived -= RelayActivity;
            try { await provider.DisposeAsync().ConfigureAwait(false); }
            catch (Exception e) { error ??= e; }
        }
        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception e) { error ??= e; }
        }
        if (error is not null)
            throw new IOException("GPULink desktop resource cleanup failed.", error);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                await CleanupAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
}
