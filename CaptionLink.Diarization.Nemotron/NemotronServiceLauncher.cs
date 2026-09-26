using System.Diagnostics;
using CaptionLink.Core.Runtime;

namespace CaptionLink.Diarization.Nemotron;

/// <summary>
/// Starts the local Nemotron service through the configured
/// runtime and waits until the GPU model is ready.
/// </summary>
public sealed class NemotronServiceLauncher
    : INemotronServiceLauncher
{
    private const string ServiceDirectoryName =
        "CaptionLink.LocalDiarization.Nemotron";

    private const string NemoPythonSetup =
        "NEMO_PYTHON=\"${XDG_DATA_HOME:-$HOME/.local/share}" +
        "/transgo/nemo-speech/.venv/bin/python\"";

    private const string HfDependenciesSetup =
        "HF_DEPS=\"${XDG_DATA_HOME:-$HOME/.local/share}" +
        "/transgo/transformers-nemotron-deps\"";

    private static readonly TimeSpan StartupTimeout =
        TimeSpan.FromSeconds(90);

    private static readonly TimeSpan HealthPollInterval =
        TimeSpan.FromMilliseconds(500);

    private static readonly SemaphoreSlim StartupGate =
        new(1, 1);

    private readonly NemotronServiceHealthClient
        _healthClient = new();

    private readonly ILocalServiceRuntime _runtime;

    private Process? _serviceProcess;
    private bool _disposed;

    public NemotronServiceLauncher(
        ILocalServiceRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _runtime = runtime;
    }

    public async Task<NemotronServiceHealth>
        EnsureReadyAsync(
            CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        NemotronServiceHealth? currentHealth =
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

    private Process StartServiceProcess()
    {
        string repositoryDirectory =
            _runtime.GetRepositoryDirectory(
                ServiceDirectoryName);

        string linuxServiceCommand =
            $"{NemoPythonSetup} " +
            $"&& {HfDependenciesSetup} " +
            "&& test -x \"$NEMO_PYTHON\" " +
            "&& test -d \"$HF_DEPS/transformers\" " +
            "&& export PYTHONNOUSERSITE=1 " +
            "&& export PYTHONPATH=\"$HF_DEPS\" " +
            $"&& cd {PosixShell.QuoteArgument(repositoryDirectory)} " +
            "&& exec \"$NEMO_PYTHON\" " +
            "-m uvicorn nemotron_service:app " +
            "--host 127.0.0.1 " +
            "--port 8767";

        ProcessStartInfo startInfo =
            CreateServiceStartInfo(
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
                "The configured runtime could not start the " +
                "Nemotron service.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Debug.WriteLine(
            "Started the local Nemotron service.");

        return process;
    }

    private async Task<NemotronServiceHealth>
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

            NemotronServiceHealth? health =
                await _healthClient.GetHealthAsync(
                    cancellationToken);

            if (health?.IsReady == true)
            {
                Debug.WriteLine(
                    "Nemotron service is ready. " +
                    $"GPU: {health.Gpu ?? "unknown"}.");

                return health;
            }

            if (serviceProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "The Nemotron service exited before " +
                    "it became ready. " +
                    $"Exit code: {serviceProcess.ExitCode}");
            }

            await Task.Delay(
                HealthPollInterval,
                cancellationToken);
        }

        throw new TimeoutException(
            "Nemotron did not become ready within " +
            $"{StartupTimeout.TotalSeconds:0} seconds.");
    }

    private ProcessStartInfo CreateServiceStartInfo(
        string command,
        bool redirectOutput)
    {
        return _runtime.CreateShellStartInfo(
            command,
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
                $"Nemotron: {eventArgs.Data}");
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
                $"Nemotron: {eventArgs.Data}");
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
         * CaptionLink session starts quickly.
         */
        DisposeServiceProcess();
        _healthClient.Dispose();

        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }
}
