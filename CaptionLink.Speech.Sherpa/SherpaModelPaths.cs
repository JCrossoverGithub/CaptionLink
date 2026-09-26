using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CaptionLink.Speech.Sherpa;

public sealed record SherpaModelPaths(
    string ModelDirectory,
    string Encoder,
    string Decoder,
    string Joiner,
    string Tokens)
{
    private const string DefaultModelName =
        "sherpa-onnx-streaming-zipformer-en-2023-06-26";

    public static SherpaModelPaths CreateDefaultEnglish()
    {
        string localAppData =
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

        string modelDirectory =
            Path.Combine(
                localAppData,
                "TransGo",
                "Models",
                DefaultModelName);

        return new SherpaModelPaths(
            ModelDirectory: modelDirectory,
            Encoder: Path.Combine(
                modelDirectory,
                "encoder-epoch-99-avg-1-chunk-16-left-128.int8.onnx"),
            Decoder: Path.Combine(
                modelDirectory,
                "decoder-epoch-99-avg-1-chunk-16-left-128.onnx"),
            Joiner: Path.Combine(
                modelDirectory,
                "joiner-epoch-99-avg-1-chunk-16-left-128.int8.onnx"),
            Tokens: Path.Combine(
                modelDirectory,
                "tokens.txt"));
    }

    public bool IsInstalled =>
        GetMissingFiles().Count == 0;

    public IReadOnlyList<string> GetMissingFiles()
    {
        string[] requiredFiles =
        [
            Encoder,
            Decoder,
            Joiner,
            Tokens
        ];

        return requiredFiles
            .Where(path => !File.Exists(path))
            .ToArray();
    }
}