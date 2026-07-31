using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TransGo.Windows
{
    public partial class MainWindow : Window
    {
        private WasapiLoopbackCapture? _capture;
        private MMDeviceEnumerator? _deviceEnumerator;
        private MMDevice? _outputDevice;

        private readonly DispatcherTimer _meterTimer;
        private long _capturedBytes;

        public MainWindow()
        {
            InitializeComponent();

            _meterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };

            _meterTimer.Tick += MeterTimer_Tick;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_capture is not null)
            {
                return;
            }

            try
            {
                _deviceEnumerator = new MMDeviceEnumerator();

                _outputDevice = _deviceEnumerator.GetDefaultAudioEndpoint(
                    DataFlow.Render,
                    Role.Multimedia);

                _capture = new WasapiLoopbackCapture(_outputDevice);

                _capture.DataAvailable += Capture_DataAvailable;
                _capture.RecordingStopped += Capture_RecordingStopped;

                Interlocked.Exchange(ref _capturedBytes, 0);

                _capture.StartRecording();
                _meterTimer.Start();

                StatusText.Text = $"Listening to: {_outputDevice.FriendlyName}";
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                CleanupCapture();

                MessageBox.Show(
                    $"TransGo could not start desktop audio capture.\n\n{ex.Message}",
                    "Audio capture error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Capture_DataAvailable(
            object? sender,
            WaveInEventArgs e)
        {
            // This confirms that actual desktop-audio bytes are arriving.
            Interlocked.Add(ref _capturedBytes, e.BytesRecorded);
        }

        private void MeterTimer_Tick(
            object? sender,
            EventArgs e)
        {
            if (_outputDevice is null)
            {
                return;
            }

            try
            {
                float peakLevel =
                    _outputDevice.AudioMeterInformation.MasterPeakValue;

                double percentage = Math.Clamp(
                    peakLevel * 100.0,
                    0,
                    100);

                long byteCount =
                    Interlocked.Read(ref _capturedBytes);

                AudioLevelMeter.Value = percentage;
                LevelText.Text = $"Audio level: {percentage:0}%";
                BytesText.Text = $"Captured: {byteCount:N0} bytes";
            }
            catch
            {
                // The output device may temporarily disappear when
                // headphones or other audio devices are changed.
            }
        }

        private void StopButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_capture is null)
            {
                return;
            }

            StopButton.IsEnabled = false;
            StatusText.Text = "Stopping…";

            _capture.StopRecording();
        }

        private void Capture_RecordingStopped(
            object? sender,
            StoppedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                CleanupCapture();

                AudioLevelMeter.Value = 0;
                LevelText.Text = "Audio level: 0%";

                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;

                if (e.Exception is null)
                {
                    StatusText.Text = "Stopped";
                }
                else
                {
                    StatusText.Text = "Capture stopped because of an error";

                    MessageBox.Show(
                        e.Exception.Message,
                        "Audio capture stopped",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            });
        }

        private void CleanupCapture()
        {
            _meterTimer.Stop();

            if (_capture is not null)
            {
                _capture.DataAvailable -= Capture_DataAvailable;
                _capture.RecordingStopped -= Capture_RecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            _outputDevice?.Dispose();
            _outputDevice = null;

            _deviceEnumerator?.Dispose();
            _deviceEnumerator = null;
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _capture?.StopRecording();
            }
            catch
            {
                // The capture device may already be stopped.
            }

            CleanupCapture();
            base.OnClosed(e);
        }
    }
}