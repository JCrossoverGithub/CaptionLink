using System.Diagnostics;
using TransGo.Core.Runtime;

namespace TransGo.Diarization.Sortformer;

/// <summary>
/// Starts the local Sortformer service through WSL and waits
/// until the GPU model is ready.
/// </summary>
public sealed class SortformerServiceLauncher
    : ISortformerServiceLauncher
{
    private const string ServiceDirectoryName =
        "TransGo.LocalDiarization.Sortformer";

    private const string NemoPythonSetup =
        "NEMO_PYTHON=\"${XDG_DATA_HOME:-$HOME/.local/share}" +
        "/transgo/nemo-speech/.venv/bin/python\"";

    private static readonly TimeSpan StartupTimeout =
        TimeSpan.FromSeconds(90);

    private static readonly TimeSpan HealthPollInterval =
        TimeSpan.FromMilliseconds(500);

    private static readonly SemaphoreSlim StartupGate =
        new(1, 1);

    private readonly SortformerServiceHealthClient
        _healthClient = new();

    private Process? _serviceProcess;
    private bool _disposed;

    public async Task<SortformerServiceHealth>
        EnsureReadyAsync(
            CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        SortformerServiceHealth? currentHealth =
            await _healthClient.GetHealthAsync(
                cancellationToken);

        if (currentHealth?.IsReady == true)
        {
            return currentHealth;
        }

        await StartupGate.WaitAsync(
            cancellationToken);

        try
        {
            currentHealth =
                await _healthClient.GetHealthAsync(
                    cancellationToken);

            if (currentHealth?.IsReady == true)
            {
                return currentHealth;
            }

            if (
                _serviceProcess is null ||
                _serviceProcess.HasExited)
            {
                DisposeServiceProcess();
                _serviceProcess = StartServiceProcess();
            }

            return await WaitUntilReadyAsync(
                _serviceProcess,
                cancellationToken);
        }
        finally
        {
            StartupGate.Release();
        }
    }

    private static Process StartServiceProcess()
    {
        string repositoryDirectory =
            WslRuntime.GetLinuxRepositoryDirectory(
                ServiceDirectoryName);

        string linuxServiceCommand =
            $"{NemoPythonSetup} " +
            "&& test -x \"$NEMO_PYTHON\" " +
            $"&& cd {WslRuntime.QuoteShellArgument(repositoryDirectory)} " +
            "&& exec \"$NEMO_PYTHON\" " +
            "-m uvicorn sortformer_service:app " +
            "--host 127.0.0.1 " +
            "--port 8766";

        ProcessStartInfo startInfo =
            CreateWslStartInfo(
                linuxServiceCommand,
                redirectOutput: true);

        var process =
            new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

        process.OutputDataReceived +=
            ServiceProcess_OutputDataReceived;

        process.ErrorDataReceived +=
            ServiceProcess_ErrorDataReceived;

        if (!process.Start())
        {
            process.Dispose();

            throw new InvalidOperationException(
                "Windows could not start the Sortformer " +
                "service through WSL.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Debug.WriteLine(
            "Started the Sortformer service through WSL.");

        return process;
    }

    private async Task<SortformerServiceHealth>
        WaitUntilReadyAsync(
            Process serviceProcess,
            CancellationToken cancellationToken)
    {
        Stopwatch timer =
            Stopwatch.StartNew();

        while (timer.Elapsed < StartupTimeout)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            SortformerServiceHealth? health =
                await _healthClient.GetHealthAsync(
                    cancellationToken);

            if (health?.IsReady == true)
            {
                Debug.WriteLine(
                    "Sortformer service is ready. " +
                    $"GPU: {health.Gpu ?? "unknown"}.");

                return health;
            }

            if (serviceProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "The Sortformer service exited before " +
                    "it became ready. " +
                    $"Exit code: {serviceProcess.ExitCode}");
            }

            await Task.Delay(
                HealthPollInterval,
                cancellationToken);
        }

        throw new TimeoutException(
            "Sortformer did not become ready within " +
            $"{StartupTimeout.TotalSeconds:0} seconds.");
    }

    private static ProcessStartInfo CreateWslStartInfo(
        string linuxCommand,
        bool redirectOutput)
    {
        return WslRuntime.CreateStartInfo(
            linuxCommand,
            redirectOutput);
    }

    private static void
        ServiceProcess_OutputDataReceived(
            object sender,
            DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(
                eventArgs.Data))
        {
            Debug.WriteLine(
                $"Sortformer: {eventArgs.Data}");
        }
    }

    private static void
        ServiceProcess_ErrorDataReceived(
            object sender,
            DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(
                eventArgs.Data))
        {
            Debug.WriteLine(
                $"Sortformer: {eventArgs.Data}");
        }
    }

    private void DisposeServiceProcess()
    {
        Process? process = _serviceProcess;
        _serviceProcess = null;

        if (process is null)
        {
            return;
        }

        process.OutputDataReceived -=
            ServiceProcess_OutputDataReceived;

        process.ErrorDataReceived -=
            ServiceProcess_ErrorDataReceived;

        process.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        /*
         * Match Parakeet: keep a healthy model alive so the next
         * TransGo session starts quickly.
         */
        DisposeServiceProcess();
        _healthClient.Dispose();

        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }
}
