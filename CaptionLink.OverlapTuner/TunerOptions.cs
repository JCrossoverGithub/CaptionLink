using System.Globalization;

namespace CaptionLink.OverlapTuner;

public sealed record TunerOptions(
    string TraceDirectory,
    string RttmDirectory,
    string OutputDirectory,
    IReadOnlySet<string> IncludedRecordingIds,
    string? ValidationCandidatePath,
    IReadOnlyList<double> StartThresholds,
    IReadOnlyList<double> StopThresholds,
    IReadOnlyList<int> ConsecutiveStartFrames,
    IReadOnlyList<int> MinimumOverlapMilliseconds,
    IReadOnlyList<int> StartHysteresisMilliseconds,
    IReadOnlyList<int> EndHysteresisMilliseconds,
    IReadOnlyList<int> MergeGapMilliseconds,
    TimeSpan PracticalCollar,
    int Parallelism,
    int TopResults)
{
    public static TunerOptions Parse(string[] arguments)
    {
        string[] valueOptionNames =
        [
            "--trace-dir",
            "--rttm-dir",
            "--output-dir",
            "--include",
            "--validate-candidate",
            "--start-thresholds",
            "--stop-thresholds",
            "--onset-frames",
            "--minimum-overlap-ms",
            "--start-hysteresis-ms",
            "--end-hysteresis-ms",
            "--merge-gap-ms",
            "--collar-ms",
            "--parallelism",
            "--top",
        ];

        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];

            if (!valueOptionNames.Contains(
                    argument,
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Unknown option '{argument}'.");
            }

            if (index + 1 >= arguments.Length ||
                arguments[index + 1].StartsWith("--"))
            {
                throw new ArgumentException(
                    $"Expected a value after '{argument}'.");
            }

            values[argument] = arguments[++index];
        }

        string traceDirectory =
            Path.GetFullPath(Required(values, "--trace-dir"));

        string rttmDirectory =
            Path.GetFullPath(Required(values, "--rttm-dir"));

        string outputDirectory = Path.GetFullPath(
            values.GetValueOrDefault(
                "--output-dir",
                Path.Combine(
                    Environment.CurrentDirectory,
                    "benchmark-results",
                    "overlap-tuning")));

        IReadOnlySet<string> includedRecordingIds =
            ParseStrings(values.GetValueOrDefault("--include"));

        string? validationCandidatePath =
            values.TryGetValue(
                "--validate-candidate",
                out string? candidatePath)
                ? Path.GetFullPath(candidatePath)
                : null;

        if (validationCandidatePath is not null)
        {
            string[] incompatibleOptions =
            [
                "--include",
                "--start-thresholds",
                "--stop-thresholds",
                "--onset-frames",
                "--minimum-overlap-ms",
                "--start-hysteresis-ms",
                "--end-hysteresis-ms",
                "--merge-gap-ms",
                "--parallelism",
                "--top",
            ];

            string[] supplied = incompatibleOptions
                .Where(values.ContainsKey)
                .ToArray();

            if (supplied.Length > 0)
            {
                throw new ArgumentException(
                    "Validation mode evaluates exactly the production " +
                    "baseline and the frozen candidate. Remove: " +
                    string.Join(", ", supplied));
            }
        }

        int defaultParallelism = Math.Max(
            1,
            Environment.ProcessorCount - 1);

        return new TunerOptions(
            traceDirectory,
            rttmDirectory,
            outputDirectory,
            includedRecordingIds,
            validationCandidatePath,
            ParseDoubles(
                values.GetValueOrDefault("--start-thresholds"),
                [0.45, 0.50, 0.55],
                "--start-thresholds"),
            ParseDoubles(
                values.GetValueOrDefault("--stop-thresholds"),
                [0.25, 0.35, 0.45],
                "--stop-thresholds"),
            ParsePositiveInts(
                values.GetValueOrDefault("--onset-frames"),
                [1, 2],
                "--onset-frames"),
            ParseNonNegativeInts(
                values.GetValueOrDefault("--minimum-overlap-ms"),
                [80, 160, 240],
                "--minimum-overlap-ms"),
            ParseNonNegativeInts(
                values.GetValueOrDefault("--start-hysteresis-ms"),
                [80, 160],
                "--start-hysteresis-ms"),
            ParseNonNegativeInts(
                values.GetValueOrDefault("--end-hysteresis-ms"),
                [720, 960],
                "--end-hysteresis-ms"),
            ParseNonNegativeInts(
                values.GetValueOrDefault("--merge-gap-ms"),
                [200, 320, 480],
                "--merge-gap-ms"),
            TimeSpan.FromMilliseconds(
                ParseNonNegativeInt(
                    values.GetValueOrDefault("--collar-ms", "250"),
                    "--collar-ms")),
            ParsePositiveInt(
                values.GetValueOrDefault(
                    "--parallelism",
                    defaultParallelism.ToString(
                        CultureInfo.InvariantCulture)),
                "--parallelism"),
            ParsePositiveInt(
                values.GetValueOrDefault("--top", "10"),
                "--top"));
    }

    public static string Usage =>
        """
        TransGo cached Sortformer-output overlap tuner

        Required:
          --trace-dir <path>              Directory containing *.sortformer-trace.json.gz
          --rttm-dir <path>               Directory containing matching VoxConverse RTTM files

        Optional:
          --output-dir <path>             Output directory (default: benchmark-results/overlap-tuning)
          --include <id,id,...>           Tune against only the listed recording IDs
          --validate-candidate <path>     Validate one frozen settings file against production
          --start-thresholds <csv>        Activity start thresholds (default: 0.45,0.50,0.55)
          --stop-thresholds <csv>         Activity stop thresholds (default: 0.25,0.35,0.45)
          --onset-frames <csv>            Consecutive frames required to start (default: 1,2)
          --minimum-overlap-ms <csv>      Minimum overlap duration (default: 80,160,240)
          --start-hysteresis-ms <csv>     Detector start hysteresis (default: 80,160)
          --end-hysteresis-ms <csv>       Detector end hysteresis (default: 720,960)
          --merge-gap-ms <csv>            Detector merge gap (default: 200,320,480)
          --collar-ms <count>             Practical collar (default: 250)
          --parallelism <count>           CPU configurations evaluated concurrently
          --top <count>                   Number of ranked configurations printed (default: 10)

        Validation mode:
          --validate-candidate evaluates exactly two configurations. It cannot
          be combined with --include, threshold grids, --parallelism, or --top.
          It also requires traces for every RTTM file. This keeps a held-out
          split from becoming another tuning set.
        """;

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string name) =>
        values.TryGetValue(name, out string? value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException(
                $"Missing required option '{name}'.");

    private static IReadOnlySet<string> ParseStrings(
        string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : value.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<double> ParseDoubles(
        string? value,
        IReadOnlyList<double> defaults,
        string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaults;
        }

        double[] parsed = value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(item =>
                double.TryParse(
                    item,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double number) &&
                double.IsFinite(number) &&
                number >= 0 &&
                number <= 1
                    ? number
                    : throw new ArgumentOutOfRangeException(
                        name,
                        "Thresholds must be between zero and one."))
            .Distinct()
            .Order()
            .ToArray();

        return parsed.Length > 0
            ? parsed
            : throw new ArgumentException(
                $"'{name}' cannot be empty.");
    }

    private static IReadOnlyList<int> ParsePositiveInts(
        string? value,
        IReadOnlyList<int> defaults,
        string name) =>
        ParseInts(value, defaults, name, requirePositive: true);

    private static IReadOnlyList<int> ParseNonNegativeInts(
        string? value,
        IReadOnlyList<int> defaults,
        string name) =>
        ParseInts(value, defaults, name, requirePositive: false);

    private static IReadOnlyList<int> ParseInts(
        string? value,
        IReadOnlyList<int> defaults,
        string name,
        bool requirePositive)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaults;
        }

        int[] parsed = value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(item =>
            {
                int number = ParseNonNegativeInt(item, name);

                if (requirePositive && number == 0)
                {
                    throw new ArgumentOutOfRangeException(
                        name,
                        "Values must be positive.");
                }

                return number;
            })
            .Distinct()
            .Order()
            .ToArray();

        return parsed.Length > 0
            ? parsed
            : throw new ArgumentException(
                $"'{name}' cannot be empty.");
    }

    private static int ParsePositiveInt(
        string value,
        string name)
    {
        int parsed = ParseNonNegativeInt(value, name);

        return parsed > 0
            ? parsed
            : throw new ArgumentOutOfRangeException(
                name,
                "The value must be positive.");
    }

    private static int ParseNonNegativeInt(
        string value,
        string name)
    {
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed) ||
            parsed < 0)
        {
            throw new ArgumentOutOfRangeException(
                name,
                "The value must be a non-negative integer.");
        }

        return parsed;
    }
}
