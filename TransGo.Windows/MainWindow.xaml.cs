using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TransGo.Audio.Windows;
using TransGo.Core.Audio;
using System.Diagnostics;
using TransGo.Audio.Processing;

namespace TransGo.Windows;

public partial class MainWindow : Window
{
    private readonly IAudioCaptureEngine _captureEngine;

    private readonly IAudioFrameNormalizer _audioNormalizer;

    private readonly List<AudioOutputDevice> _audioDevices = new();

    public MainWindow()
    {
        InitializeComponent();

        _captureEngine = new WasapiLoopbackCaptureEngine();

        _audioNormalizer = new Pcm16MonoFrameNormalizer();

        _captureEngine.AudioFrameAvailable +=
        CaptureEngine_AudioFrameAvailable;

        _audioNormalizer.ChunkAvailable +=
        AudioNormalizer_ChunkAvailable;

        _captureEngine.MetricsUpdated +=
            CaptureEngine_MetricsUpdated;

        _captureEngine.CaptureStopped +=
            CaptureEngine_CaptureStopped;

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
                $"TransGo could not list the available audio devices.\n\n{exception.Message}",
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

    private void CaptureEngine_AudioFrameAvailable(
    object? sender,
    AudioFrameEventArgs e)
    {
        try
        {
            _audioNormalizer.Process(e.Frame);
        }
        catch (Exception exception)
        {
            /*
             * Normalization errors must not escape into the
             * WASAPI callback and interrupt desktop capture.
             */
            Debug.WriteLine(
                $"Audio normalization failed: {exception}");
        }
    }

    private void AudioNormalizer_ChunkAvailable(
    object? sender,
    TranscriptionAudioChunkEventArgs e)
    {
        TranscriptionAudioChunk chunk = e.Chunk;

        Debug.WriteLine(
            $"Normalized chunk {chunk.Sequence}: " +
            $"{chunk.ByteCount} bytes, " +
            $"{chunk.SampleRate} Hz, " +
            $"{chunk.Duration.TotalMilliseconds:0} ms");

        Dispatcher.BeginInvoke(new Action(() =>
        {
            NormalizationText.Text =
                $"Normalized chunks: {chunk.Sequence:N0} · " +
                $"Last: {chunk.ByteCount:N0} bytes · " +
                $"{chunk.Duration.TotalMilliseconds:0} ms";
        }));
    }

    private void StartButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_captureEngine.IsCapturing)
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

        try
        {
            _audioNormalizer.Reset();
            NormalizationText.Text = "Normalized chunks: 0";

            _captureEngine.Start(selectedDevice.Id);

            StatusText.Text =
                $"Listening to: {selectedDevice.Name}";

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;

            OutputDeviceComboBox.IsEnabled = false;
            RefreshDevicesButton.IsEnabled = false;
        }
        catch (Exception exception)
        {
            RestoreStoppedControls();

            MessageBox.Show(
                $"TransGo could not start desktop audio capture.\n\n{exception.Message}",
                "Audio capture error",
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

        _captureEngine.Stop();
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

    private void CaptureEngine_CaptureStopped(
        object? sender,
        AudioCaptureStoppedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RestoreStoppedControls();

            if (e.Error is null)
            {
                StatusText.Text = "Stopped";
                return;
            }

            StatusText.Text =
                "Capture stopped because of an error";

            MessageBox.Show(
                e.Error.Message,
                "Audio capture stopped",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }));
    }

    private void RestoreStoppedControls()
    {
        AudioLevelMeter.Value = 0;
        LevelText.Text = "Audio level: 0%";

        StartButton.IsEnabled =
            _audioDevices.Count > 0;

        StopButton.IsEnabled = false;
        OutputDeviceComboBox.IsEnabled = true;
        RefreshDevicesButton.IsEnabled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _captureEngine.AudioFrameAvailable -=
            CaptureEngine_AudioFrameAvailable;

        _audioNormalizer.ChunkAvailable -=
            AudioNormalizer_ChunkAvailable;

        _captureEngine.MetricsUpdated -=
            CaptureEngine_MetricsUpdated;

        _captureEngine.CaptureStopped -=
            CaptureEngine_CaptureStopped;

        _captureEngine.Dispose();

        _audioNormalizer.Dispose();
        _captureEngine.Dispose();

        base.OnClosed(e);
    }
}