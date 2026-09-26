using System.Globalization;

namespace CaptionLink.OverlapBenchmark;

public sealed record BenchmarkOptions(
    string AudioDirectory,
    string RttmDirectory,
    string OutputDirectory,
    int? MaximumFiles,
    IReadOnlySet<string> IncludedRecordingIds,
    TimeSpan ChunkDuration,
    TimeSpan PracticalCollar,
    int MaximumSpeakers,
    bool Realtime,
    bool Resume,
    string? TraceDirectory)
{
    public DiarizationBackend Engine { get; init; } =
        DiarizationBackend.Sortformer;

    public string? DiarizationRttmDirectory { get; init; }

    public static BenchmarkOptions Parse(
        string[] arguments)
    {
        string[] valueOptionNames =
        [
            "--audio-dir",
            "--rttm-dir",
            "--engine",
            "--output-dir",
            "--max-files",
            "--include",
            "--chunk-ms",
            "--collar-ms",
            "--maximum-speakers",
            "--trace-dir",
            "--diarization-rttm-dir",
        ];

        var values =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        var switches =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];

            if (string.Equals(
                    argument,
                    "--realtime",
                    StringComparison.OrdinalIgnoreCase))
            {
                switches.Add(argument);
                continue;
            }

            if (string.Equals(
                    argument,
                    "--resume",
                    StringComparison.OrdinalIgnoreCase))
            {
                switches.Add(argument);
                continue;
            }

            if (!valueOptionNames.Contains(
                    argument,
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Unknown option '{argument}'.");
            }

            if (
                !argument.StartsWith("--") ||
                index + 1 >= arguments.Length ||
                arguments[index + 1].StartsWith("--"))
            {
                throw new ArgumentException(
                    $"Expected a value after '{argument}'.");
            }

            values[argument] = arguments[++index];
        }

        string audioDirectory =
            Required(values, "--audio-dir");

        string rttmDirectory =
            Required(values, "--rttm-dir");

        string outputDirectory =
            values.GetValueOrDefault(
                "--output-dir",
                Path.Combine(
                    Environment.CurrentDirectory,
                    "benchmark-results",
                    "voxconverse"));

        DiarizationBackend engine =
            values.TryGetValue(
                "--engine",
                out string? engineText)
                ? ParseEngine(engineText)
                : DiarizationBackend.Sortformer;

        int? maximumFiles =
            values.TryGetValue(
                "--max-files",
                out string? maximumFilesText)
                ? ParsePositiveInt(
                    maximumFilesText,
                    "--max-files")
                : null;

        int chunkMilliseconds =
            values.TryGetValue(
                "--chunk-ms",
                out string? chunkText)
                ? ParsePositiveInt(
                    chunkText,
                    "--chunk-ms")
                : 100;

        int collarMilliseconds =
            values.TryGetValue(
                "--collar-ms",
                out string? collarText)
                ? ParseNonNegativeInt(
                    collarText,
                    "--collar-ms")
                : 250;

        int maximumSpeakers =
            values.TryGetValue(
                "--maximum-speakers",
                out string? maximumSpeakersText)
                ? ParsePositiveInt(
                    maximumSpeakersText,
                    "--maximum-speakers")
                : 4;

        int supportedMaximumSpeakers =
            engine == DiarizationBackend.Nemotron
                ? 8
                : 4;

        if (maximumSpeakers > supportedMaximumSpeakers)
        {
            throw new ArgumentOutOfRangeException(
                "--maximum-speakers",
                $"{engine} supports at most " +
                $"{supportedMaximumSpeakers} speakers.");
        }

        IReadOnlySet<string> includedRecordingIds =
            values.TryGetValue(
                "--include",
                out string? includeText)
                ? includeText
                    .Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries)
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

        string? traceDirectory =
            values.TryGetValue(
                "--trace-dir",
                out string? traceDirectoryText)
                ? Path.GetFullPath(traceDirectoryText)
                : null;

        string? diarizationRttmDirectory =
            values.TryGetValue(
                "--diarization-rttm-dir",
                out string? diarizationRttmDirectoryText)
                ? Path.GetFullPath(
                    diarizationRttmDirectoryText)
                : null;

        if (
            engine == DiarizationBackend.Nemotron &&
            traceDirectory is not null)
        {
            throw new ArgumentException(
                "Nemotron probability trace capture is not " +
                "implemented yet. Run the live benchmark " +
                "without --trace-dir.");
        }

        return new BenchmarkOptions(
            Path.GetFullPath(audioDirectory),
            Path.GetFullPath(rttmDirectory),
            Path.GetFullPath(outputDirectory),
            maximumFiles,
            includedRecordingIds,
            TimeSpan.FromMilliseconds(chunkMilliseconds),
            TimeSpan.FromMilliseconds(collarMilliseconds),
            maximumSpeakers,
            switches.Contains("--realtime"),
            switches.Contains("--resume"),
            traceDirectory)
        {
            Engine = engine,
            DiarizationRttmDirectory =
                diarizationRttmDirectory,
        };
    }

    public static string Usage =>
        """
        CaptionLink VoxConverse overlap benchmark

        Required:
          --audio-dir <path>       Directory containing VoxConverse WAV files
          --rttm-dir <path>        Directory containing matching v0.3 RTTM files

        Optional:
          --output-dir <path>      Report directory (default: benchmark-results/voxconverse)
          --engine <name>          sortformer or nemotron (default: sortformer)
          --max-files <count>      Run the first N recordings
          --include <id,id,...>    Run only the listed recording IDs
          --chunk-ms <count>       Streaming chunk size (default: 100)
          --collar-ms <count>      Practical scoring collar (default: 250)
          --maximum-speakers <n>   Speaker capacity; Sortformer 1-4, Nemotron 1-8 (default: 4)
          --trace-dir <path>       Save raw Sortformer probability traces for CPU replay
          --diarization-rttm-dir <path>
                                   Save finalized speaker activities as RTTM
          --realtime               Pace audio in real time and measure 30-second buffer availability
          --resume                 Skip completed recordings in the output checkpoint and retry failures
        """;

    private static DiarizationBackend ParseEngine(
        string value)
    {
        if (string.Equals(
                value,
                "sortformer",
                StringComparison.OrdinalIgnoreCase))
        {
            return DiarizationBackend.Sortformer;
        }

        if (string.Equals(
                value,
                "nemotron",
                StringComparison.OrdinalIgnoreCase))
        {
            return DiarizationBackend.Nemotron;
        }

        throw new ArgumentException(
            "--engine must be 'sortformer' or 'nemotron'.");
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string name) =>
        values.TryGetValue(name, out string? value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException(
                $"Missing required option '{name}'.");

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
        if (
            !int.TryParse(
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
