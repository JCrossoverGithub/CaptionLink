using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SherpaOnnx;

namespace TransGo.Speech.Sherpa;

public static class WhisperTurboSmokeTest
{
    public static WhisperTurboProbeResult Run(
        string waveFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            waveFilePath);

        if (!File.Exists(waveFilePath))
        {
            throw new FileNotFoundException(
                "The Whisper test WAV file was not found.",
                waveFilePath);
        }

        /*
         * Measure model loading separately because it will
         * normally happen only once when the provider starts.
         */
        var modelLoadTimer =
            Stopwatch.StartNew();

        OfflineRecognizer recognizer =
            SherpaWhisperRecognizerFactory
                .CreateEnglishCpu();

        modelLoadTimer.Stop();

        OfflineStream stream =
            recognizer.CreateStream();

        var waveReader =
            new Pcm16WaveReader(waveFilePath);

        stream.AcceptWaveform(
            waveReader.SampleRate,
            waveReader.Samples);

        TimeSpan audioDuration =
            TimeSpan.FromSeconds(
                waveReader.Samples.Length /
                (double)waveReader.SampleRate);

        var decodeTimer =
            Stopwatch.StartNew();

        recognizer.Decode(
            new List<OfflineStream>
            {
                stream
            });

        decodeTimer.Stop();

        string text =
            stream.Result.Text.Trim();

        return new WhisperTurboProbeResult(
            Text: text,
            AudioDuration: audioDuration,
            ModelLoadTime: modelLoadTimer.Elapsed,
            DecodeTime: decodeTimer.Elapsed);
    }
}