using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TransGo.Speech.Sherpa;

public sealed record WhisperTurboModelPaths(
    string ModelDirectory,
    string Encoder,
    string Decoder,
    string Tokens)
{
    private const string ModelFolderName =
        "sherpa-onnx-whisper-turbo";

    public static WhisperTurboModelPaths CreateDefault()
    {
        string localAppData =
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

        string modelDirectory =
            Path.Combine(
                localAppData,
                "TransGo",
                "Models",
                ModelFolderName);

        return new WhisperTurboModelPaths(
            ModelDirectory: modelDirectory,

            Encoder: Path.Combine(
                modelDirectory,
                "turbo-encoder.int8.onnx"),

            Decoder: Path.Combine(
                modelDirectory,
                "turbo-decoder.int8.onnx"),

            Tokens: Path.Combine(
                modelDirectory,
                "turbo-tokens.txt"));
    }

    public bool IsInstalled =>
        GetMissingFiles().Count == 0;

    public IReadOnlyList<string> GetMissingFiles()
    {
        string[] requiredFiles =
        [
            Encoder,
            Decoder,
            Tokens
        ];

        return requiredFiles
            .Where(path => !File.Exists(path))
            .ToArray();
    }

    public void ValidateInstalled()
    {
        IReadOnlyList<string> missingFiles =
            GetMissingFiles();

        if (missingFiles.Count == 0)
        {
            return;
        }

        throw new FileNotFoundException(
            "The Whisper Turbo model is incomplete." +
            Environment.NewLine +
            Environment.NewLine +
            string.Join(
                Environment.NewLine,
                missingFiles));
    }
}