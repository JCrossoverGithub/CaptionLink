using System.Diagnostics;
using CaptionLink.Core.Audio;
using CaptionLink.Core.Diarization;

namespace CaptionLink.Diarization.Simulated;

/// <summary>
/// Produces deterministic speaker activity for development and
/// testing. Speaker labels alternate at a fixed interval.
/// </summary>
public sealed class SimulatedDiarizationEngine
    : IDiarizationEngine
{
    private static readonly TimeSpan DefaultSpeakerInterval =
        TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly TimeSpan _speakerInterval;

    private DiarizationConfiguration? _configuration;
    private SpeakerActivity? _lastActivity;

    private long _activitySequence;
    private bool _isRunning;
    private bool _disposed;

    public SimulatedDiarizationEngine()
        : this(DefaultSpeakerInterval)
    {
    }

    public SimulatedDiarizationEngine(
        TimeSpan speakerInterval)
    {
        if (speakerInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speakerInterval),
                "The speaker interval must be positive.");
        }

        _speakerInterval = speakerInterval;
    }

    public event EventHandler<SpeakerActivityEventArgs>?
        ActivityReceived;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _isRunning;
            }
        }
    }

    public Task StartAsync(
        DiarizationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateConfiguration(configuration);

        lock (_gate)
        {
            ThrowIfDisposed();

            if (_isRunning)
            {
                throw new InvalidOperationException(
                    "The simulated diarization engine is " +
                    "already running.");
            }

            _configuration = configuration;
            _activitySequence = 0;
            _lastActivity = null;
            _isRunning = true;
        }

        return Task.CompletedTask;
    }

    public ValueTask SendAsync(
        TranscriptionAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();

        List<SpeakerActivity> activities;

        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureRunning();

            DiarizationConfiguration configuration =
                _configuration!;

            if (chunk.SampleRate != configuration.SampleRate)
            {
                throw new InvalidOperationException(
                    "The audio chunk sample rate does not match " +
                    "the diarization configuration.");
            }

            if (chunk.SessionStartTime < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunk),
                    "The session start time cannot be negative.");
            }

            activities = CreateActivities(
                chunk,
                configuration);
        }

        foreach (SpeakerActivity activity in activities)
        {
            PublishActivity(activity);
        }

        return ValueTask.CompletedTask;
    }

    private List<SpeakerActivity> CreateActivities(
        TranscriptionAudioChunk chunk,
        DiarizationConfiguration configuration)
    {
        var activities =
            new List<SpeakerActivity>();

        TimeSpan cursor =
            chunk.SessionStartTime;

        TimeSpan chunkEnd =
            chunk.SessionEndTime;

        int speakerCount =
            Math.Clamp(
                configuration.MaximumSpeakers,
                1,
                2);

        while (cursor < chunkEnd)
        {
            long windowIndex =
                cursor.Ticks /
                _speakerInterval.Ticks;

            TimeSpan windowStart =
                TimeSpan.FromTicks(
                    windowIndex *
                    _speakerInterval.Ticks);

            TimeSpan windowEnd =
                windowStart +
                _speakerInterval;

            TimeSpan activityEnd =
                chunkEnd < windowEnd
                    ? chunkEnd
                    : windowEnd;

            int speakerNumber =
                (int)(windowIndex % speakerCount) + 1;

            bool isFinal =
                activityEnd >= windowEnd;

            var activity =
                new SpeakerActivity(
                    ActivityId:
                        $"simulated-activity-{windowIndex:D6}",

                    Sequence:
                        ++_activitySequence,

                    SpeakerId:
                        $"speaker-{speakerNumber}",

                    StartTime:
                        windowStart,

                    EndTime:
                        activityEnd,

                    IsFinal:
                        isFinal,

                    Confidence:
                        1.0);

            activities.Add(activity);
            _lastActivity = activity;

            cursor = activityEnd;
        }

        return activities;
    }

    public Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SpeakerActivity? finalActivity = null;

        lock (_gate)
        {
            ThrowIfDisposed();

            if (!_isRunning)
            {
                return Task.CompletedTask;
            }

            if (
                _lastActivity is not null &&
                !_lastActivity.IsFinal)
            {
                finalActivity =
                    _lastActivity with
                    {
                        Sequence =
                            ++_activitySequence,

                        IsFinal = true,
                    };

                _lastActivity = finalActivity;
            }

            _isRunning = false;
            _configuration = null;
        }

        if (finalActivity is not null)
        {
            PublishActivity(finalActivity);
        }

        return Task.CompletedTask;
    }

    private void PublishActivity(
        SpeakerActivity activity)
    {
        EventHandler<SpeakerActivityEventArgs>? handlers =
            ActivityReceived;

        if (handlers is null)
        {
            return;
        }

        var eventArgs =
            new SpeakerActivityEventArgs(activity);

        foreach (
            EventHandler<SpeakerActivityEventArgs> handler
            in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    "A simulated diarization event handler " +
                    $"failed: {exception}");
            }
        }
    }

    private void EnsureRunning()
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException(
                "The simulated diarization engine is not running.");
        }
    }

    private static void ValidateConfiguration(
        DiarizationConfiguration configuration)
    {
        if (configuration.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "The sample rate must be positive.");
        }

        if (configuration.MaximumSpeakers <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "The maximum speaker count must be positive.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        await StopAsync();

        lock (_gate)
        {
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}