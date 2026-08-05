using System.Diagnostics;

namespace TransGo.Speech.Parakeet;

/// <summary>
/// Starts the local Parakeet service through WSL and ensures
/// that the requested streaming profile is loaded.
/// </summary>
public sealed class ParakeetServiceLauncher
    : IAsyncDisposable
{
    private const string DistributionName =
        "Ubuntu-24.04";

    private const string RepositoryDirectory =
        "/mnt/c/Users/user/Documents/Projects/" +
        "TransGo.Desktop/TransGo.LocalAsr.Parakeet";

    private const string PythonExecutable =
        "$HOME/transgo-parakeet-benchmark/.venv/bin/python";

    private static readonly TimeSpan StartupTimeout =
        TimeSpan.FromSeconds(90);

    private static readonly TimeSpan ShutdownTimeout =
        TimeSpan.FromSeconds(10);

    private static readonly TimeSpan HealthPollInterval =
        TimeSpan.FromMilliseconds(500);

    private static readonly SemaphoreSlim StartupGate =
        new(1, 1);

    private readonly ParakeetServiceHealthClient
        _healthClient = new();

    private Process? _serviceProcess;
    private bool _disposed;

    public Task<ParakeetServiceHealth> EnsureReadyAsync(
        CancellationToken cancellationToken = default)
    {
        return EnsureReadyAsync(
            ParakeetStreamingProfile.Accurate,
            cancellationToken);
    }

    public async Task<ParakeetServiceHealth>
        EnsureReadyAsync(
            ParakeetStreamingProfile profile,
            CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        string expectedProfile =
            profile.ToServiceValue();

        ParakeetServiceHealth? currentHealth =
            await _healthClient.GetHealthAsync(
                cancellationToken);

        if (IsMatchingReadyService(
                currentHealth,
                expectedProfile))
        {
            return currentHealth!;
        }

        await StartupGate.WaitAsync(
            cancellationToken);

        try
        {
            currentHealth =
                await _healthClient.GetHealthAsync(
                    cancellationToken);

            if (IsMatchingReadyService(
                    currentHealth,
                    expectedProfile))
            {
                return currentHealth!;
            }

            /*
             * The NeMo pipeline profile is fixed at service
             * startup. Restart the service when a different
             * profile is requested.
             */
            if (currentHealth?.IsReady == true)
            {
                Debug.WriteLine(
                    "Restarting Parakeet to change profile " +
                    $"from '{currentHealth.Profile}' " +
                    $"to '{expectedProfile}'.");

                await StopExistingServiceAsync(
                    cancellationToken);
            }

            if (
                _serviceProcess is null ||
                _serviceProcess.HasExited)
            {
                DisposeServiceProcess();

                _serviceProcess =
                    StartServiceProcess(profile);
            }

            return await WaitUntilReadyAsync(
                _serviceProcess,
                expectedProfile,
                cancellationToken);
        }
        finally
        {
            StartupGate.Release();
        }
    }

    private static bool IsMatchingReadyService(
        ParakeetServiceHealth? health,
        string expectedProfile)
    {
        return
            health?.IsReady == true &&
            string.Equals(
                health.Profile,
                expectedProfile,
                StringComparison.OrdinalIgnoreCase);
    }

    private static Process StartServiceProcess(
        ParakeetStreamingProfile profile)
    {
        string serviceProfile =
            profile.ToServiceValue();

        string linuxServiceCommand =
            $"cd \"{RepositoryDirectory}\" " +
            $"&& export TRANSGO_PARAKEET_PROFILE=" +
            $"{serviceProfile} " +
            $"&& exec \"{PythonExecutable}\" " +
            "-m uvicorn parakeet_service:app " +
            "--host 127.0.0.1 " +
            "--port 8765";

        var startInfo =
            CreateWslStartInfo(
                linuxServiceCommand,
                redirectOutput: true);

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

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Debug.WriteLine(
            "Started the Parakeet service with profile " +
            $"'{serviceProfile}'.");

        return process;
    }

    private async Task StopExistingServiceAsync(
        CancellationToken cancellationToken)
    {
        const string stopCommand =
            "pkill -f " +
            "'[u]vicorn parakeet_service:app' " +
            "|| true";

        using Process stopProcess =
            new()
            {
                StartInfo = CreateWslStartInfo(
                    stopCommand,
                    redirectOutput: false)
            };

        if (!stopProcess.Start())
        {
            throw new InvalidOperationException(
                "Windows could not stop the existing " +
                "Parakeet service through WSL.");
        }

        using var shutdownCancellation =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken);

        shutdownCancellation.CancelAfter(
            ShutdownTimeout);

        try
        {
            await stopProcess.WaitForExitAsync(
                shutdownCancellation.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken
                .IsCancellationRequested)
        {
            throw new TimeoutException(
                "The existing Parakeet service did not " +
                "stop within 10 seconds.");
        }

        Stopwatch timer =
            Stopwatch.StartNew();

        while (timer.Elapsed < ShutdownTimeout)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            ParakeetServiceHealth? health =
                await _healthClient.GetHealthAsync(
                    cancellationToken);

            if (health?.IsReady != true)
            {
                DisposeServiceProcess();
                return;
            }

            await Task.Delay(
                HealthPollInterval,
                cancellationToken);
        }

        throw new TimeoutException(
            "The existing Parakeet health endpoint " +
            "remained available after shutdown.");
    }

    private async Task<ParakeetServiceHealth>
        WaitUntilReadyAsync(
            Process serviceProcess,
            string expectedProfile,
            CancellationToken cancellationToken)
    {
        Stopwatch timer =
            Stopwatch.StartNew();

        while (timer.Elapsed < StartupTimeout)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            ParakeetServiceHealth? health =
                await _healthClient.GetHealthAsync(
                    cancellationToken);

            if (IsMatchingReadyService(
                    health,
                    expectedProfile))
            {
                Debug.WriteLine(
                    "Parakeet service is ready. " +
                    $"Profile: {health!.Profile}. " +
                    $"GPU: {health.Gpu}");

                return health;
            }

            if (serviceProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "The Parakeet service exited before it " +
                    "became ready. " +
                    $"Exit code: {serviceProcess.ExitCode}");
            }

            await Task.Delay(
                HealthPollInterval,
                cancellationToken);
        }

        throw new TimeoutException(
            "Parakeet did not become ready with profile " +
            $"'{expectedProfile}' within " +
            $"{StartupTimeout.TotalSeconds:0} seconds.");
    }

    private static ProcessStartInfo CreateWslStartInfo(
        string linuxCommand,
        bool redirectOutput)
    {
        var startInfo =
            new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput =
                    redirectOutput,
                RedirectStandardError =
                    redirectOutput
            };

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
            linuxCommand);

        return startInfo;
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
                $"Parakeet: {eventArgs.Data}");
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
         * Do not terminate a healthy model when TransGo closes.
         * Keeping it alive makes the next launch much faster.
         */
        DisposeServiceProcess();

        _healthClient.Dispose();

        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }
}
