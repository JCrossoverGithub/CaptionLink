using System;
using System.Collections.Generic;

namespace CaptionLink.Core.Audio;

public interface IAudioCaptureEngine : IDisposable
{

    event EventHandler<AudioFrameEventArgs>? AudioFrameAvailable;

    event EventHandler<AudioCaptureMetricsEventArgs>? MetricsUpdated;

    event EventHandler<AudioCaptureStoppedEventArgs>? CaptureStopped;

    bool IsCapturing { get; }

    IReadOnlyList<AudioOutputDevice> GetOutputDevices();

    void Start(string deviceId);

    void Stop();
}