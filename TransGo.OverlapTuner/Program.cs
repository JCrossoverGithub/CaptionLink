namespace TransGo.OverlapTuner;

internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length == 0 ||
            arguments.Contains(
                "--help",
                StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(TunerOptions.Usage);
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
            TunerOptions options =
                TunerOptions.Parse(arguments);

            var tuner = new ReplayTuner(options);

            if (options.ValidationCandidatePath is not null)
            {
                ValidationReport validation =
                    await tuner.RunValidationAsync(
                        cancellation.Token);

                var validationWriter =
                    new ValidationReportWriter(
                        options.OutputDirectory);

                await validationWriter.WriteAsync(
                    validation,
                    cancellation.Token);

                PrintResult(
                    "Production baseline",
                    validation.Baseline);

                PrintResult(
                    $"Frozen candidate ({validation.FrozenCandidate.CandidateId})",
                    validation.Candidate);

                Console.WriteLine();
                Console.WriteLine("Predeclared validation checks:");
                PrintCheck(
                    "strict F1 meets minimum",
                    validation.Checks.StrictF1AtLeastMinimum);
                PrintCheck(
                    "strict precision meets minimum",
                    validation.Checks.StrictPrecisionAtLeastMinimum);
                PrintCheck(
                    "strict recall meets minimum",
                    validation.Checks.StrictRecallAtLeastMinimum);
                PrintCheck(
                    "under-0.5s recall is preserved",
                    validation.Checks.UnderHalfSecondRecallPreserved);
                PrintCheck(
                    "p95 finalization latency is within maximum",
                    validation.Checks.P95FinalizationLatencyWithinMaximum);
                PrintCheck(
                    "strict F1 improves over production baseline",
                    validation.Checks.StrictF1ImprovesOverBaseline);

                Console.WriteLine();
                Console.WriteLine(
                    $"VALIDATION VERDICT: " +
                    $"{(validation.Passed ? "PASS" : "FAIL")}");

                Console.WriteLine();
                Console.WriteLine(
                    $"JSON: {validationWriter.ReportPath}");
                Console.WriteLine(
                    $"CSV:  {validationWriter.CsvPath}");

                return 0;
            }

            TuningReport report =
                await tuner.RunAsync(cancellation.Token);

            var writer =
                new TuningReportWriter(
                    options.OutputDirectory);

            await writer.WriteAsync(
                report,
                cancellation.Token);

            PrintResult("Baseline", report.Baseline);
            PrintResult("Recommended", report.Recommended);

            Console.WriteLine();
            Console.WriteLine(
                $"Top {Math.Min(options.TopResults, report.Candidates.Count)} configurations:");

            foreach (var result in
                report.Candidates.Take(options.TopResults))
            {
                Console.WriteLine(
                    $"  strict F1={result.Strict.F1:P2}; " +
                    $"practical F1={result.Practical.F1:P2}; " +
                    $"short recall={result.UnderHalfSecondRecall:P2}; " +
                    $"guardrails={(result.MeetsGuardrails ? "pass" : "fail")}; " +
                    result.Configuration.Id);
            }

            Console.WriteLine();
            Console.WriteLine($"JSON: {writer.ReportPath}");
            Console.WriteLine($"CSV:  {writer.CsvPath}");
            Console.WriteLine(
                $"Settings: {writer.RecommendedSettingsPath}");

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Tuning cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(TunerOptions.Usage);
            return 1;
        }
    }

    private static void PrintResult(
        string label,
        TuningCandidateResult result)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"{label}: strict P/R/F1=" +
            $"{result.Strict.Precision:P2}/" +
            $"{result.Strict.Recall:P2}/" +
            $"{result.Strict.F1:P2}; " +
            $"practical F1={result.Practical.F1:P2}; " +
            $"under-0.5s recall={result.UnderHalfSecondRecall:P2}; " +
            $"p95 finalization=" +
            $"{result.P95FinalizationLatencyMilliseconds:0} ms");

        Console.WriteLine(
            $"  {result.Configuration.Id}");
    }

    private static void PrintCheck(
        string label,
        bool passed)
    {
        Console.WriteLine(
            $"  {(passed ? "PASS" : "FAIL")}: {label}");
    }
}
