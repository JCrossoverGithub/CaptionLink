using System.Diagnostics;

namespace TransGo.Speech.Parakeet;

/// <summary>
/// Starts the local Parakeet service through WSL when it is
/// not already running, then waits for the health endpoint.
/// </summary>
public sealed class ParakeetServiceLauncher
    : IAsyncDisposable
{
    private const string DistributionName =
        "Ubuntu-24.04";

    private const string LinuxServiceCommand =
        "cd \"$HOME/transgo-parakeet-benchmark\" " +
        "&& exec " +
        "\"$HOME/transgo-parakeet-benchmark/.venv/bin/python\" " +
        "-m uvicorn parakeet_service:app " +
        "--host 127.0.0.1 " +
        "--port 8765";

    private static readonly TimeSpan StartupTimeout =
        TimeSpan.FromSeconds(45);

    private static readonly TimeSpan HealthPollInterval =
        TimeSpan.FromMilliseconds(500);

    private readonly SemaphoreSlim _startupGate =
        new(1, 1);

    private readonly ParakeetServiceHealthClient
        _healthClient = new();

    private Process? _serviceProcess;
    private bool _disposed;

    public async Task<ParakeetServiceHealth>
        EnsureReadyAsync(
            CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        ParakeetServiceHealth? currentHealth =
            await _healthClient.GetHealthAsync(
                cancellationToken);

        if (currentHealth?.IsReady == true)
        {
            return currentHealth;
        }

        /*
         * Prevent two simultaneous Start Listening requests
         * from launching two WSL service processes.
         */
        await _startupGate.WaitAsync(
            cancellationToken);

        try
        {
            /*
             * Another operation may have started the service
             * while this operation waited for the gate.
             */
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

                _serviceProcess =
                    StartServiceProcess();
            }

            return await WaitUntilReadyAsync(
                _serviceProcess,
                cancellationToken);
        }
        finally
        {
            _startupGate.Release();
        }
    }

    private static Process StartServiceProcess()
    {
        var startInfo =
            new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

        /*
         * Equivalent command:
         *
         * wsl.exe --distribution Ubuntu-24.04 --
         * bash -lc "<LinuxServiceCommand>"
         *
         * ArgumentList handles quoting each argument safely.
         */
        startInfo.ArgumentList.Add(
            "--distribution");

        startInfo.ArgumentList.Add(
            DistributionName);

        startInfo.ArgumentList.Add(
            "--");

        startInfo.ArgumentList.Add(
            "bash");

        startInfo.ArgumentList.Add(
            "-lc");

        startInfo.ArgumentList.Add(
            LinuxServiceCommand);

        var process =
            new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

        process.OutputDataReceived +=
            ServiceProcess_OutputDataReceived;

        process.ErrorDataReceived +=
            ServiceProcess_ErrorDataReceived;

        if (!process.Start())
        {
            process.Dispose();

            throw new InvalidOperationException(
                "Windows could not start the Parakeet " +
                "service through WSL.");
        }

        /*
         * Drain both streams asynchronously. NeMo writes a
         * substantial amount of startup output, and leaving a
         * redirected stream unread could eventually block it.
         */
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private async Task<ParakeetServiceHealth>
        WaitUntilReadyAsync(
            Process serviceProcess,
            CancellationToken cancellationToken)
    {
        var timer =
            Stopwatch.StartNew();

        while (timer.Elapsed < StartupTimeout)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            if (serviceProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "The Parakeet service exited before it " +
                    "became ready. " +
                    $"Exit code: {serviceProcess.ExitCode}");
            }

            ParakeetServiceHealth? health =
                await _healthClient.GetHealthAsync(
                    cancellationToken);

            if (health?.IsReady == true)
            {
                Debug.WriteLine(
                    "Parakeet service is ready. " +
                    $"GPU: {health.Gpu}");

                return health;
            }

            await Task.Delay(
                HealthPollInterval,
                cancellationToken);
        }

        throw new TimeoutException(
            "Parakeet did not become ready within " +
            $"{StartupTimeout.TotalSeconds:0} seconds.");
    }

    private static void ServiceProcess_OutputDataReceived(
        object sender,
        DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(
                eventArgs.Data))
        {
            Debug.WriteLine(
                $"Parakeet: {eventArgs.Data}");
        }
    }

    private static void ServiceProcess_ErrorDataReceived(
        object sender,
        DataReceivedEventArgs eventArgs)
    {
        /*
         * Uvicorn and NeMo write ordinary status messages to
         * stderr, so these are diagnostic messages rather than
         * necessarily fatal errors.
         */
        if (!string.IsNullOrWhiteSpace(
                eventArgs.Data))
        {
            Debug.WriteLine(
                $"Parakeet: {eventArgs.Data}");
        }
    }

    private void DisposeServiceProcess()
    {
        Process? process =
            _serviceProcess;

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
         * Disposing the Windows Process object does not
         * deliberately terminate an already-running service.
         * Service shutdown will be added separately.
         */
        DisposeServiceProcess();

        _healthClient.Dispose();
        _startupGate.Dispose();

        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }
}