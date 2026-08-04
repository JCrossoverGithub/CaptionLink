using System;
using SherpaOnnx;

namespace TransGo.Speech.Sherpa;

internal static class SherpaWhisperRecognizerFactory
{
    public static OfflineRecognizer CreateEnglishCpu()
    {
        WhisperTurboModelPaths model =
            WhisperTurboModelPaths.CreateDefault();

        model.ValidateInstalled();

        var config =
            new OfflineRecognizerConfig();

        /*
         * Whisper models operate with 16 kHz, 80-dimensional
         * audio features. Sherpa can resample incoming audio
         * when AcceptWaveform receives a different sample rate.
         */
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;

        config.ModelConfig.Whisper.Encoder =
            model.Encoder;

        config.ModelConfig.Whisper.Decoder =
            model.Decoder;

        config.ModelConfig.Whisper.Language =
            "en";

        config.ModelConfig.Whisper.Task =
            "transcribe";

        /*
         * -1 allows Sherpa to use the model's default tail
         * padding behavior.
         */
        config.ModelConfig.Whisper.TailPaddings =
            -1;

        config.ModelConfig.Tokens =
            model.Tokens;

        /*
         * Start with CPU so model configuration and decoding
         * can be verified before introducing CUDA libraries.
         */
        config.ModelConfig.Provider =
            "cpu";

        config.ModelConfig.NumThreads =
            Math.Clamp(
                Environment.ProcessorCount / 2,
                1,
                8);

        config.ModelConfig.Debug =
            0;

        config.DecodingMethod =
            "greedy_search";

        config.MaxActivePaths =
            4;

        return new OfflineRecognizer(
            config);
    }
}