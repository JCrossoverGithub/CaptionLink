using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using TransGo.Audio.Processing;
using TransGo.Audio.Windows;
using TransGo.Core.Audio;
using TransGo.Core.Transcription;
using TransGo.Speech.Google;

namespace TransGo.Windows;

public partial class MainWindow : Window
{
    private const int TranscriptionSampleRate = 48000;

    private readonly IAudioCaptureEngine _captureEngine;
    private readonly IAudioFrameNormalizer _audioNormalizer;
    private readonly ITranscriptionEngine _transcriptionEngine;
    private readonly CaptionOverlayWindow _captionOverlay;

    private readonly List<AudioOutputDevice> _audioDevices = new();

    private string _finalTranscript = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        _captionOverlay =
        new CaptionOverlayWindow();

        _captureEngine =
            new WasapiLoopbackCaptureEngine();

        _audioNormalizer =
            new Pcm16MonoFrameNormalizer();

        _transcriptionEngine =
            new GoogleStreamingTranscriptionEngine();

        _captureEngine.AudioFrameAvailable +=
            CaptureEngine_AudioFrameAvailable;

        _captureEngine.MetricsUpdated +=
            CaptureEngine_MetricsUpdated;

        _captureEngine.CaptureStopped +=
            CaptureEngine_CaptureStopped;

        _audioNormalizer.ChunkAvailable +=
            AudioNormalizer_ChunkAvailable;

        _transcriptionEngine.ResultReceived +=
            TranscriptionEngine_ResultReceived;

        LoadOutputDevices();
    }

    private void LoadOutputDevices()
    {
        string? previouslySelectedId =
            (OutputDeviceComboBox.SelectedItem
                as AudioOutputDevice)?.Id;

        OutputDeviceComboBox.ItemsSource = null;
        _audioDevices.Clear();

        try
        {
            IReadOnlyList<AudioOutputDevice> devices =
                _captureEngine.GetOutputDevices();

            _audioDevices.AddRange(devices);

            OutputDeviceComboBox.ItemsSource =
                _audioDevices;

            AudioOutputDevice? selectedDevice =
                _audioDevices.FirstOrDefault(
                    device =>
                        device.Id == previouslySelectedId)
                ?? _audioDevices.FirstOrDefault(
                    device => device.IsDefault)
                ?? _audioDevices.FirstOrDefault();

            OutputDeviceComboBox.SelectedItem =
                selectedDevice;

            bool devicesAvailable =
                selectedDevice is not null;

            StartButton.IsEnabled =
                devicesAvailable;

            StatusText.Text = devicesAvailable
                ? "Select a device and start listening"
                : "No active audio output devices found";
        }
        catch (Exception exception)
        {
            StartButton.IsEnabled = false;

            StatusText.Text =
                "Could not load audio devices";

            MessageBox.Show(
                "TransGo could not list the available " +
                $"audio devices.\n\n{exception.Message}",
                "Audio device error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RefreshDevicesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_captureEngine.IsCapturing)
        {
            return;
        }

        LoadOutputDevices();
    }

    private async void StartButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_captureEngine.IsCapturing ||
            _transcriptionEngine.IsRunning)
        {
            return;
        }

        if (OutputDeviceComboBox.SelectedItem
            is not AudioOutputDevice selectedDevice)
        {
            MessageBox.Show(
                "Select an audio output device first.",
                "No device selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;

        OutputDeviceComboBox.IsEnabled = false;
        RefreshDevicesButton.IsEnabled = false;

        StatusText.Text =
            "Connecting to Google Speech-to-Text…";

        try
        {
            _audioNormalizer.Reset();

            _finalTranscript = string.Empty;

            TranscriptText.Text =
                "Listening for speech…";

            _captionOverlay.ResetCaption();

            if (!_captionOverlay.IsVisible)
            {
                _captionOverlay.Show();
            }

            NormalizationText.Text =
                "Normalized chunks: 0";

            AudioLevelMeter.Value = 0;

            LevelText.Text =
                "Audio level: 0%";

            BytesText.Text =
                "Captured: 0 bytes";

            var configuration =
                new TranscriptionConfiguration(
                    LanguageCode: "en-US",
                    SampleRate: TranscriptionSampleRate,
                    EnableInterimResults: true);

            /*
             * Start Google first so no captured audio is lost
             * while the streaming connection is being created.
             */
            await _transcriptionEngine.StartAsync(
                configuration);

            _captureEngine.Start(
                selectedDevice.Id);

            StatusText.Text =
                $"Listening to: {selectedDevice.Name}";

            StopButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            try
            {
                if (_captureEngine.IsCapturing)
                {
                    _captureEngine.Stop();
                }

                if (_transcriptionEngine.IsRunning)
                {
                    await _transcriptionEngine.StopAsync();
                }
            }
            catch (Exception cleanupException)
            {
                Debug.WriteLine(
                    $"Startup cleanup failed: {cleanupException}");
            }

            RestoreStoppedControls();

            StatusText.Text =
                "Could not start live transcription";

            MessageBox.Show(
                "TransGo could not start live transcription." +
                $"\n\n{exception.Message}",
                "Transcription startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void StopButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_captureEngine.IsCapturing)
        {
            return;
        }

        StopButton.IsEnabled = false;
        StatusText.Text = "Stopping…";

        /*
         * CaptureStopped will stop the Google stream after
         * no more normalized audio can be produced.
         */
        _captureEngine.Stop();
    }

    private void CaptureEngine_AudioFrameAvailable(
        object? sender,
        AudioFrameEventArgs e)
    {
        try
        {
            _audioNormalizer.Process(
                e.Frame);
        }
        catch (Exception exception)
        {
            /*
             * Never allow a normalization failure to escape
             * into NAudio's real-time callback.
             */
            Debug.WriteLine(
                $"Audio normalization failed: {exception}");
        }
    }

    private async void AudioNormalizer_ChunkAvailable(
        object? sender,
        TranscriptionAudioChunkEventArgs e)
    {
        TranscriptionAudioChunk chunk =
            e.Chunk;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            NormalizationText.Text =
                $"Normalized chunks: {chunk.Sequence:N0} · " +
                $"Last: {chunk.ByteCount:N0} bytes · " +
                $"{chunk.Duration.TotalMilliseconds:0} ms";
        }));

        if (!_transcriptionEngine.IsRunning)
        {
            return;
        }

        try
        {
            await _transcriptionEngine.SendAsync(
                chunk);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Sending transcription audio failed: {exception}");
        }
    }

    private void TranscriptionEngine_ResultReceived(
        object? sender,
        TranscriptResultEventArgs e)
    {
        TranscriptResult result =
            e.Result;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            string displayText;

            if (result.IsFinal)
            {
                if (!string.IsNullOrWhiteSpace(
                        _finalTranscript))
                {
                    _finalTranscript += " ";
                }

                _finalTranscript +=
                    result.Text;

                displayText =
                    _finalTranscript;
            }
            else
            {
                displayText =
                    string.IsNullOrWhiteSpace(
                        _finalTranscript)
                        ? result.Text
                        : $"{_finalTranscript} {result.Text}";
            }

            TranscriptText.Text =
                displayText;

            _captionOverlay.SetCaption(
                displayText,
                result.IsFinal);
        }));
    }

    private void CaptureEngine_MetricsUpdated(
        object? sender,
        AudioCaptureMetricsEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AudioLevelMeter.Value =
                e.LevelPercent;

            LevelText.Text =
                $"Audio level: {e.LevelPercent:0}%";

            BytesText.Text =
                $"Captured: {e.CapturedBytes:N0} bytes";
        }));
    }

    private async void CaptureEngine_CaptureStopped(
        object? sender,
        AudioCaptureStoppedEventArgs e)
    {
        Exception? transcriptionError = null;

        try
        {
            if (_transcriptionEngine.IsRunning)
            {
                await _transcriptionEngine.StopAsync();
            }
        }
        catch (Exception exception)
        {
            transcriptionError = exception;

            Debug.WriteLine(
                $"Stopping transcription failed: {exception}");
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            RestoreStoppedControls();

            if (e.Error is not null)
            {
                StatusText.Text =
                    "Capture stopped because of an error";

                MessageBox.Show(
                    e.Error.Message,
                    "Audio capture stopped",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            if (transcriptionError is not null)
            {
                StatusText.Text =
                    "Transcription stopped with an error";

                MessageBox.Show(
                    transcriptionError.Message,
                    "Transcription error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return;
            }

            StatusText.Text = "Stopped";
        }));
    }

    private void RestoreStoppedControls()
    {
        AudioLevelMeter.Value = 0;

        LevelText.Text =
            "Audio level: 0%";

        StartButton.IsEnabled =
            _audioDevices.Count > 0;

        StopButton.IsEnabled = false;
        OutputDeviceComboBox.IsEnabled = true;
        RefreshDevicesButton.IsEnabled = true;
    }

    protected override async void OnClosed(
        EventArgs e)
    {
        _captureEngine.AudioFrameAvailable -=
            CaptureEngine_AudioFrameAvailable;

        _captureEngine.MetricsUpdated -=
            CaptureEngine_MetricsUpdated;

        _captureEngine.CaptureStopped -=
            CaptureEngine_CaptureStopped;

        _audioNormalizer.ChunkAvailable -=
            AudioNormalizer_ChunkAvailable;

        _transcriptionEngine.ResultReceived -=
            TranscriptionEngine_ResultReceived;

        try
        {
            if (_captureEngine.IsCapturing)
            {
                _captureEngine.Stop();
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Audio capture cleanup failed: {exception}");
        }

        _audioNormalizer.Dispose();
        _captureEngine.Dispose();
        _captionOverlay.Close();

        base.OnClosed(e);

        try
        {
            await _transcriptionEngine.DisposeAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Transcription cleanup failed: {exception}");
        }
    }
}