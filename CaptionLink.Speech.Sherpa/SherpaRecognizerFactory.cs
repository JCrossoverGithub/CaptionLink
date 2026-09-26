using System;
using System.IO;
using SherpaOnnx;

namespace CaptionLink.Speech.Sherpa;

internal static class SherpaRecognizerFactory
{
    public static OnlineRecognizer CreateDefaultEnglish()
    {
        SherpaModelPaths model =
            SherpaModelPaths.CreateDefaultEnglish();

        if (!model.IsInstalled)
        {
            throw new FileNotFoundException(
                "The local Sherpa speech model is incomplete." +
                Environment.NewLine +
                Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    model.GetMissingFiles()));
        }

        var config =
            new OnlineRecognizerConfig();

        /*
         * This is the sample rate used to train the model.
         * Audio passed to AcceptWaveform can still use its
         * actual source sample rate.
         */
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;

        config.ModelConfig.Transducer.Encoder =
            model.Encoder;

        config.ModelConfig.Transducer.Decoder =
            model.Decoder;

        config.ModelConfig.Transducer.Joiner =
            model.Joiner;

        config.ModelConfig.Tokens =
            model.Tokens;

        config.ModelConfig.Provider =
            "cpu";

        config.ModelConfig.NumThreads =
            Math.Clamp(
                Environment.ProcessorCount / 2,
                1,
                4);

        config.ModelConfig.Debug = 0;

        /*
         * Greedy search is the simplest and lowest-latency
         * choice for the first local-captioning version.
         */
        config.DecodingMethod =
            "greedy_search";

        config.MaxActivePaths = 4;

        /*
         * Endpoint detection identifies the end of a spoken
         * phrase so interim text can become final.
         */
        config.EnableEndpoint = 1;

        config.Rule1MinTrailingSilence = 1.2F;
        config.Rule2MinTrailingSilence = 0.55F;
        config.Rule3MinUtteranceLength = 8.0F;

        return new OnlineRecognizer(
            config);
    }
}