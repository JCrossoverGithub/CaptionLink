using System.Diagnostics;
using System.Text.Json;
using TransGo.Core.Runtime;

namespace TransGo.Speech.Parakeet;

public sealed class MultitalkerParakeetServiceLauncher
    : IMultitalkerParakeetServiceLauncher
{
    private const string ServiceDirectoryName =
        "TransGo.LocalAsr.Multitalker";

    private const string NemoPythonSetup =
        "NEMO_PYTHON=\"${XDG_DATA_HOME:-$HOME/.local/share}" +
        "/transgo/nemo-speech/.venv/bin/python\"";

    private static readonly Uri HealthUri =
        new("http://localhost:8768/health");

    private static readonly TimeSpan StartupTimeout =
        TimeSpan.FromSeconds(180);

    private static readonly TimeSpan HealthPollInterval =
        TimeSpan.FromMilliseconds(500);

    private static readonly SemaphoreSlim StartupGate =
        new(1, 1);

    private readonly HttpClient _httpClient =
        new()
        {
            Timeout = TimeSpan.FromSeconds(2),
        };

    private readonly ILocalServiceRuntime _runtime;

    private Process? _serviceProcess;
    private bool _disposed;

    public MultitalkerParakeetServiceLauncher(
        ILocalServiceRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        _runtime = runtime;
    }

    public async Task EnsureReadyAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (await IsReadyAsync(
                cancellationToken))
        {
            return;
        }

        await StartupGate.WaitAsync(
            cancellationToken);

        try
        {
            if (await IsReadyAsync(
                    cancellationToken))
            {
                return;
            }

            await StopConflictingServicesAsync(
                cancellationToken);

            DisposeServiceProcess();

            _serviceProcess =
                StartServiceProcess();

            await WaitUntilReadyAsync(
                _serviceProcess,
                cancellationToken);
        }
        finally
        {
            StartupGate.Release();
        }
    }

    private async Task<bool> IsReadyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync(
                    HealthUri,
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string json =
                await response.Content
                    .ReadAsStringAsync(
                        cancellationToken);

            using JsonDocument document =
                JsonDocument.Parse(json);

            return
                document.RootElement
                    .TryGetProperty(
                        "status",
                        out JsonElement statusElement)
                &&
                string.Equals(
                    statusElement.GetString(),
                    "ready",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (
            Exception exception)
            when (
                exception is HttpRequestException
                or TaskCanceledException
                or JsonException)
        {
            return false;
        }
    }

    private async Task
        StopConflictingServicesAsync(
            CancellationToken cancellationToken)
    {
        const string stopCommand =
            "pkill -f " +
            "'[u]vicorn multitalker_service:app' " +
            ">/dev/null 2>&1 || true; " +
            "pkill -f " +
            "'[u]vicorn parakeet_service:app' " +
            ">/dev/null 2>&1 || true; " +
            "pkill -f " +
            "'[u]vicorn nemotron_service:app' " +
            ">/dev/null 2>&1 || true; " +
            "pkill -f " +
            "'[u]vicorn sortformer_service:app' " +
            ">/dev/null 2>&1 || true";

        using Process stopProcess =
            new()
            {
                StartInfo =
                    _runtime.CreateShellStartInfo(
                        stopCommand,
                        redirectOutput: false),
            };

        if (!stopProcess.Start())
        {
            throw new InvalidOperationException(
                "Windows could not stop conflicting " +
                "local GPU services.");
        }

        await stopProcess.WaitForExitAsync(
            cancellationToken);
    }

    private Process StartServiceProcess()
    {
        string repositoryDirectory =
            _runtime.GetRepositoryDirectory(
                ServiceDirectoryName);

        string command =
            $"{NemoPythonSetup} " +
            "&& test -x \"$NEMO_PYTHON\" " +
            $"&& cd {PosixShell.QuoteArgument(repositoryDirectory)} " +
            "&& exec \"$NEMO_PYTHON\" " +
            "-m uvicorn multitalker_service:app " +
            "--host 127.0.0.1 " +
            "--port 8768";

        var startInfo =
            _runtime.CreateShellStartInfo(
                command,
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
                "Windows could not start the " +
                "Multitalker service through WSL.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Debug.WriteLine(
            "Started Multitalker Parakeet service.");

        return process;
    }

    private async Task WaitUntilReadyAsync(
        Process serviceProcess,
        CancellationToken cancellationToken)
    {
        Stopwatch timer =
            Stopwatch.StartNew();

        while (timer.Elapsed < StartupTimeout)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            if (await IsReadyAsync(
                    cancellationToken))
            {
                Debug.WriteLine(
                    "Multitalker Parakeet service ready.");

                return;
            }

            if (serviceProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "The Multitalker service exited " +
                    "before it became ready. " +
                    $"Exit code: {serviceProcess.ExitCode}");
            }

            await Task.Delay(
                HealthPollInterval,
                cancellationToken);
        }

        throw new TimeoutException(
            "Multitalker Parakeet did not become " +
            "ready within 180 seconds.");
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
                $"Multitalker: {eventArgs.Data}");
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
                $"Multitalker: {eventArgs.Data}");
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
         * Keep the models resident after TransGo closes.
         * The next Multitalker launch can reuse the service.
         */
        DisposeServiceProcess();

        _httpClient.Dispose();

        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }
}
