using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TransGo.Core.Audio;

namespace TransGo.Audio.Windows;

public sealed class WasapiLoopbackCaptureEngine : IAudioCaptureEngine
{
    private readonly object _gate = new();

    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _outputDevice;
    private WasapiLoopbackCapture? _capture;
    private WaveFormat? _captureFormat;

    private long _capturedBytes;
    private long _frameSequence;
    private bool _disposed;

    public event EventHandler<AudioFrameEventArgs>? AudioFrameAvailable;

    public event EventHandler<AudioCaptureMetricsEventArgs>? MetricsUpdated;

    public event EventHandler<AudioCaptureStoppedEventArgs>? CaptureStopped;

    public bool IsCapturing
    {
        get
        {
            lock (_gate)
            {
                return _capture is not null;
            }
        }
    }

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        ThrowIfDisposed();

        using var enumerator = new MMDeviceEnumerator();

        string? defaultDeviceId = null;
        MMDevice? defaultDevice = null;

        try
        {
            defaultDevice = enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia);

            defaultDeviceId = defaultDevice.ID;
        }
        catch
        {
            // Windows may temporarily have no default output device.
        }
        finally
        {
            defaultDevice?.Dispose();
        }

        var results = new List<AudioOutputDevice>();

        MMDeviceCollection devices =
            enumerator.EnumerateAudioEndPoints(
                DataFlow.Render,
                DeviceState.Active);

        for (int index = 0; index < devices.Count; index++)
        {
            MMDevice device = devices[index];

            try
            {
                results.Add(
                    new AudioOutputDevice(
                        device.ID,
                        device.FriendlyName,
                        device.ID == defaultDeviceId));
            }
            finally
            {
                device.Dispose();
            }
        }

        return results
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name)
            .ToArray();
    }

    public void Start(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ThrowIfDisposed();

        lock (_gate)
        {
            if (_capture is not null)
            {
                throw new InvalidOperationException(
                    "Audio capture is already running.");
            }

            try
            {
                _deviceEnumerator = new MMDeviceEnumerator();

                _outputDevice =
                    _deviceEnumerator.GetDevice(deviceId);

                _capture =
                    new WasapiLoopbackCapture(_outputDevice);

                _captureFormat = _capture.WaveFormat;

                Debug.WriteLine(
                    $"TransGo capture format: {_captureFormat}");

                _capture.DataAvailable += Capture_DataAvailable;
                _capture.RecordingStopped += Capture_RecordingStopped;

                Interlocked.Exchange(ref _capturedBytes, 0);
                Interlocked.Exchange(ref _frameSequence, 0);

                _capture.StartRecording();
            }
            catch
            {
                CleanupLocked();
                throw;
            }
        }
    }

    public void Stop()
    {
        WasapiLoopbackCapture? capture;

        lock (_gate)
        {
            capture = _capture;
        }

        if (capture is null)
        {
            return;
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception exception)
        {
            CompleteCapture(exception);
        }
    }

    private void Capture_DataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        WaveFormat? captureFormat = _captureFormat;

        if (captureFormat is null)
        {
            return;
        }

        /*
         * NAudio owns and reuses e.Buffer after this callback returns.
         * We must copy the bytes before sending them elsewhere.
         */
        var audioBytes = new byte[e.BytesRecorded];

        Buffer.BlockCopy(
            e.Buffer,
            0,
            audioBytes,
            0,
            e.BytesRecorded);

        long sequence = Interlocked.Increment(
            ref _frameSequence);

        long totalBytes = Interlocked.Add(
            ref _capturedBytes,
            e.BytesRecorded);

        var frame = new AudioFrame(
            Sequence: sequence,
            CapturedAt: DateTimeOffset.UtcNow,
            Data: audioBytes,
            SampleRate: captureFormat.SampleRate,
            Channels: captureFormat.Channels,
            BitsPerSample: captureFormat.BitsPerSample,
            Encoding: GetSampleEncoding(captureFormat));

        /*
         * Publishing the frame must happen before any optional
         * diagnostic calculation can fail.
         */
        AudioFrameAvailable?.Invoke(
            this,
            new AudioFrameEventArgs(frame));

        double levelPercent = 0;

        try
        {
            levelPercent = CalculatePeakLevel(
                audioBytes,
                audioBytes.Length,
                captureFormat);
        }
        catch (Exception exception)
        {
            // Meter failure must never interrupt audio delivery.
            Debug.WriteLine(
                $"TransGo meter calculation failed: {exception}");
        }

        MetricsUpdated?.Invoke(
            this,
            new AudioCaptureMetricsEventArgs(
                levelPercent,
                totalBytes));

        Debug.WriteLine(
            $"Frame {frame.Sequence}: " +
            $"{frame.ByteCount} bytes, " +
            $"{frame.SampleRate} Hz, " +
            $"{frame.Channels} channels, " +
            $"{frame.Encoding}");
    }

    private static AudioSampleEncoding GetSampleEncoding(
    WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            return AudioSampleEncoding.IeeeFloat;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            return AudioSampleEncoding.PcmInteger;
        }

        /*
         * WASAPI shared-mode loopback commonly exposes its mix
         * format as Extensible with 32-bit floating-point samples.
         */
        if (format.Encoding == WaveFormatEncoding.Extensible)
        {
            return format.BitsPerSample == 32
                ? AudioSampleEncoding.IeeeFloat
                : AudioSampleEncoding.PcmInteger;
        }

        return AudioSampleEncoding.Unknown;
    }

    private static double CalculatePeakLevel(
        byte[] buffer,
        int bytesRecorded,
        WaveFormat format)
    {
        if (bytesRecorded <= 0)
        {
            return 0;
        }

        double peak = 0;

        /*
         * WASAPI loopback normally returns 32-bit floating-point
         * samples in shared mode. Extensible 32-bit formats are also
         * treated as floating point for this prototype.
         */
        bool isFloat32 =
            format.BitsPerSample == 32 &&
            (format.Encoding == WaveFormatEncoding.IeeeFloat ||
             format.Encoding == WaveFormatEncoding.Extensible);

        if (isFloat32)
        {
            for (
                int offset = 0;
                offset + sizeof(float) <= bytesRecorded;
                offset += sizeof(float))
            {
                float sample =
                    BitConverter.ToSingle(buffer, offset);

                if (!float.IsFinite(sample))
                {
                    continue;
                }

                double absoluteSample =
                    Math.Abs(sample);

                if (absoluteSample > peak)
                {
                    peak = absoluteSample;
                }
            }
        }
        else if (
            format.Encoding == WaveFormatEncoding.Pcm &&
            format.BitsPerSample == 16)
        {
            for (
                int offset = 0;
                offset + sizeof(short) <= bytesRecorded;
                offset += sizeof(short))
            {
                short sample =
                    BitConverter.ToInt16(buffer, offset);

                double absoluteSample =
                    Math.Abs(sample / 32768.0);

                if (absoluteSample > peak)
                {
                    peak = absoluteSample;
                }
            }
        }

        return Math.Clamp(
            peak * 100.0,
            0,
            100);
    }

    private void Capture_RecordingStopped(
        object? sender,
        StoppedEventArgs e)
    {
        CompleteCapture(e.Exception);
    }

    private void CompleteCapture(Exception? error)
    {
        bool wasCapturing;

        lock (_gate)
        {
            wasCapturing = _capture is not null;

            if (wasCapturing)
            {
                CleanupLocked();
            }
        }

        if (wasCapturing)
        {
            CaptureStopped?.Invoke(
                this,
                new AudioCaptureStoppedEventArgs(error));
        }
    }

    private void CleanupLocked()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= Capture_DataAvailable;
            _capture.RecordingStopped -= Capture_RecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _captureFormat = null;

        _outputDevice?.Dispose();
        _outputDevice = null;

        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(WasapiLoopbackCaptureEngine));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CleanupLocked();
        }

        GC.SuppressFinalize(this);
    }
}