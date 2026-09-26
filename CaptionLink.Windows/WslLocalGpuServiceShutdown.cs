using System.Diagnostics;
using CaptionLink.Core.Runtime;

namespace CaptionLink.Windows;

/// <summary>
/// Stops WSL-backed local GPU services when the desktop
/// application itself exits.
/// </summary>
internal sealed class WslLocalGpuServiceShutdown
{
    private static readonly TimeSpan ShutdownTimeout =
        TimeSpan.FromSeconds(10);

    private const string StopCommand =
        "pkill -f '[u]vicorn parakeet_service:app' " +
        ">/dev/null 2>&1 || true; " +
        "pkill -f '[u]vicorn multitalker_service:app' " +
        ">/dev/null 2>&1 || true; " +
        "pkill -f '[u]vicorn nemotron_service:app' " +
        ">/dev/null 2>&1 || true; " +
        "pkill -f '[u]vicorn sortformer_service:app' " +
        ">/dev/null 2>&1 || true; " +
        "for ((i = 0; i < 50; i++)); do " +
        "if ! pgrep -f '[u]vicorn parakeet_service:app' " +
        ">/dev/null " +
        "&& ! pgrep -f '[u]vicorn multitalker_service:app' " +
        ">/dev/null " +
        "&& ! pgrep -f '[u]vicorn nemotron_service:app' " +
        ">/dev/null " +
        "&& ! pgrep -f '[u]vicorn sortformer_service:app' " +
        ">/dev/null; " +
        "then exit 0; fi; " +
        "sleep 0.1; " +
        "done; " +
        "exit 1";

    private readonly ILocalServiceRuntime _runtime;

    public WslLocalGpuServiceShutdown(
        ILocalServiceRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _runtime = runtime;
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        using Process process =
            new()
            {
                StartInfo =
                    _runtime.CreateShellStartInfo(
                        StopCommand,
                        redirectOutput: false),
            };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Windows could not start local GPU service " +
                "shutdown.");
        }

        using var timeoutCancellation =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);

        timeoutCancellation.CancelAfter(
            ShutdownTimeout);

        try
        {
            await process.WaitForExitAsync(
                timeoutCancellation.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken
                .IsCancellationRequested)
        {
            throw new TimeoutException(
                "Local GPU services did not stop within " +
                $"{ShutdownTimeout.TotalSeconds:0} seconds.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "One or more local GPU services remained " +
                "running after shutdown.");
        }
    }
}
