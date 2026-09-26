using CaptionLink.Speech.Sherpa;

string localAppData =
    Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData);

string waveFilePath =
    Path.Combine(
        localAppData,
        "CaptionLink",
        "Models",
        "sherpa-onnx-whisper-turbo",
        "test_wavs",
        "0.wav");

Console.WriteLine(
    "Loading Whisper Turbo and decoding test audio...");

try
{
    WhisperTurboProbeResult result =
        WhisperTurboSmokeTest.Run(
            waveFilePath);

    Console.WriteLine();
    Console.WriteLine(
        $"Audio duration: {result.AudioDuration.TotalSeconds:0.00} seconds");

    Console.WriteLine(
        $"Model load time: {result.ModelLoadTime.TotalSeconds:0.00} seconds");

    Console.WriteLine(
        $"Decode time: {result.DecodeTime.TotalSeconds:0.00} seconds");

    Console.WriteLine(
        $"Real-time factor: {result.RealTimeFactor:0.00}");

    Console.WriteLine();
    Console.WriteLine("Transcript:");
    Console.WriteLine(result.Text);
}
catch (Exception exception)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Whisper test failed:");
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}