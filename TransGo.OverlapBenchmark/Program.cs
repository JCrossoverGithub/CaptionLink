using TransGo.Diarization.Nemotron;
using TransGo.Diarization.Sortformer;

namespace TransGo.OverlapBenchmark;

internal static class Program
{
    private static async Task<int> Main(
        string[] arguments)
    {
        if (
            arguments.Length == 0 ||
            arguments.Contains(
                "--help",
                StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(BenchmarkOptions.Usage);
            return arguments.Length == 0 ? 1 : 0;
        }

        using var cancellation =
            new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            BenchmarkOptions options =
                BenchmarkOptions.Parse(arguments);

            Directory.CreateDirectory(
                options.OutputDirectory);

            var runner =
                new VoxConverseBenchmarkRunner(
                    options,
                    backend => backend switch
                    {
                        DiarizationBackend.Sortformer =>
                            new SortformerDiarizationEngine(),

                        DiarizationBackend.Nemotron =>
                            new NemotronDiarizationEngine(),

                        _ => throw new InvalidOperationException(
                            $"Unsupported diarization engine: " +
                            $"{backend}."),
                    });

            var checkpointStore =
                new BenchmarkCheckpointStore(options);

            BenchmarkCheckpoint? checkpoint =
                options.Resume
                    ? await checkpointStore.LoadAsync(
                        cancellation.Token)
                    : null;

            BenchmarkRunSnapshot initialState =
                runner.CreateInitialSnapshot(
                    checkpoint?.SuccessfulResults ?? [],
                    checkpoint?.Failures ?? []);

            if (
                checkpoint is not null &&
                checkpoint.SelectedRecordings !=
                    initialState.SelectedRecordings)
            {
                throw new InvalidDataException(
                    "The number of selected recordings has changed since " +
                    "the checkpoint was written. Use a different output " +
                    "directory for this dataset state.");
            }

            await checkpointStore.SaveAsync(
                initialState,
                cancellation.Token);

            BenchmarkRunSnapshot run =
                await runner.RunAsync(
                    initialState,
                    snapshot => checkpointStore.SaveAsync(
                        snapshot,
                        CancellationToken.None),
                    cancellation.Token);

            AggregateBenchmarkReport report =
                BenchmarkReportBuilder.Build(
                    run.SuccessfulResults,
                    options,
                    run.SelectedRecordings,
                    run.Failures);

            Console.WriteLine();
            Console.WriteLine(
                $"Strict: precision={report.Strict.Precision:P2}, " +
                $"recall={report.Strict.Recall:P2}, " +
                $"F1={report.Strict.F1:P2}");

            Console.WriteLine(
                $"Practical ({report.PracticalCollarMilliseconds:0} ms collar): " +
                $"precision={report.Practical.Precision:P2}, " +
                $"recall={report.Practical.Recall:P2}, " +
                $"F1={report.Practical.F1:P2}");

            Console.WriteLine(
                $"Completed: {run.SuccessfulResults.Count}/" +
                $"{run.SelectedRecordings}; failed: {run.Failures.Count}");
            Console.WriteLine(
                $"JSON: {checkpointStore.ReportPath}");
            Console.WriteLine(
                $"CSV:  {checkpointStore.CsvPath}");

            if (run.Failures.Count > 0)
            {
                Console.WriteLine(
                    $"Failures: {checkpointStore.FailuresPath}");
                Console.WriteLine(
                    "Rerun the same command with --resume to retry failures " +
                    "without repeating completed recordings.");
            }

            if (!options.Realtime)
            {
                Console.WriteLine(
                    "Buffer availability was not scored in accelerated mode. " +
                    "Use --realtime for that metric.");
            }

            return run.Failures.Count == 0 ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Benchmark cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(BenchmarkOptions.Usage);
            return 1;
        }
    }

}
