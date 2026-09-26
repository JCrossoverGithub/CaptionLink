using CaptionLink.Core.Diarization;
using CaptionLink.Diarization.Sortformer;

namespace CaptionLink.OverlapBenchmark;

public sealed record ProbabilityActivityOptions(
    double StartThreshold,
    double StopThreshold,
    int ConsecutiveStartFrames = 1)
{
    public void Validate()
    {
        if (
            !double.IsFinite(StartThreshold) ||
            !double.IsFinite(StopThreshold) ||
            StopThreshold < 0 ||
            StartThreshold > 1 ||
            StopThreshold > StartThreshold)
        {
            throw new ArgumentException(
                "Thresholds must satisfy 0 <= stop <= start <= 1.");
        }

        if (ConsecutiveStartFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConsecutiveStartFrames));
        }
    }
}

/// <summary>
/// CPU equivalent of the service's SortformerSpeakerActivityTracker.
/// The baseline configuration (0.50/0.35, one start frame) reproduces
/// the production probability-to-activity conversion.
/// </summary>
public sealed class ProbabilityActivityTracker
{
    private readonly int _maximumSpeakers;
    private readonly ProbabilityActivityOptions _options;
    private readonly OpenActivity?[] _openActivities;
    private readonly PendingStart[] _pendingStarts;
    private readonly int[] _activityNumbers;

    private long _nextExpectedFrameIndex;
    private long _sequence;
    private bool _closed;

    public ProbabilityActivityTracker(
        int maximumSpeakers,
        ProbabilityActivityOptions options)
    {
        if (maximumSpeakers <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSpeakers));
        }

        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _maximumSpeakers = maximumSpeakers;
        _options = options;
        _openActivities = new OpenActivity[maximumSpeakers];
        _pendingStarts = Enumerable
            .Range(0, maximumSpeakers)
            .Select(_ => new PendingStart())
            .ToArray();
        _activityNumbers = new int[maximumSpeakers];
    }

    public long NextExpectedFrameIndex =>
        _nextExpectedFrameIndex;

    public IReadOnlyList<SpeakerActivity> Consume(
        SortformerProbabilityBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (_closed)
        {
            throw new InvalidOperationException(
                "Cannot consume probability batches after flush.");
        }

        ValidateBatch(batch);

        var updates = new List<SpeakerActivity>();

        for (int localFrameIndex = 0;
            localFrameIndex < batch.FrameCount;
            localFrameIndex++)
        {
            long frameIndex =
                batch.StartFrameIndex + localFrameIndex;

            float[] probabilities =
                batch.Probabilities[localFrameIndex];

            for (int speakerIndex = 0;
                speakerIndex < _maximumSpeakers;
                speakerIndex++)
            {
                double probability = Math.Clamp(
                    probabilities[speakerIndex],
                    0,
                    1);

                OpenActivity? open =
                    _openActivities[speakerIndex];

                if (open is not null)
                {
                    if (probability >= _options.StopThreshold)
                    {
                        open.AddFrame(frameIndex, probability);
                    }
                    else
                    {
                        updates.Add(
                            CreateUpdate(
                                open,
                                batch.FrameDurationSeconds,
                                isFinal: true));

                        _openActivities[speakerIndex] = null;
                        _pendingStarts[speakerIndex].Reset();
                    }

                    continue;
                }

                PendingStart pending =
                    _pendingStarts[speakerIndex];

                if (probability < _options.StartThreshold)
                {
                    pending.Reset();
                    continue;
                }

                pending.AddFrame(frameIndex, probability);

                if (pending.FrameCount <
                    _options.ConsecutiveStartFrames)
                {
                    continue;
                }

                open = StartActivity(
                    speakerIndex,
                    pending.StartFrameIndex,
                    pending.EndFrameIndex,
                    pending.ConfidenceSum,
                    pending.FrameCount);

                _openActivities[speakerIndex] = open;
                pending.Reset();
            }
        }

        _nextExpectedFrameIndex += batch.FrameCount;

        for (int speakerIndex = 0;
            speakerIndex < _maximumSpeakers;
            speakerIndex++)
        {
            OpenActivity? open =
                _openActivities[speakerIndex];

            if (open is not null)
            {
                updates.Add(
                    CreateUpdate(
                        open,
                        batch.FrameDurationSeconds,
                        isFinal: false));
            }
        }

        return updates;
    }

    public IReadOnlyList<SpeakerActivity> Flush(
        double frameDurationSeconds)
    {
        if (_closed)
        {
            return [];
        }

        if (
            !double.IsFinite(frameDurationSeconds) ||
            frameDurationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameDurationSeconds));
        }

        _closed = true;

        var updates = new List<SpeakerActivity>();

        for (int speakerIndex = 0;
            speakerIndex < _maximumSpeakers;
            speakerIndex++)
        {
            OpenActivity? open =
                _openActivities[speakerIndex];

            if (open is not null)
            {
                updates.Add(
                    CreateUpdate(
                        open,
                        frameDurationSeconds,
                        isFinal: true));

                _openActivities[speakerIndex] = null;
            }

            _pendingStarts[speakerIndex].Reset();
        }

        return updates;
    }

    private void ValidateBatch(
        SortformerProbabilityBatch batch)
    {
        if (batch.StartFrameIndex != _nextExpectedFrameIndex)
        {
            throw new InvalidDataException(
                "Sortformer probability batches must be continuous.");
        }

        if (batch.FrameCount <= 0 ||
            batch.SpeakerCount != _maximumSpeakers)
        {
            throw new InvalidDataException(
                "The probability batch dimensions do not match the tracker.");
        }

        if (
            !double.IsFinite(batch.FrameDurationSeconds) ||
            batch.FrameDurationSeconds <= 0)
        {
            throw new InvalidDataException(
                "The probability frame duration is invalid.");
        }
    }

    private OpenActivity StartActivity(
        int speakerIndex,
        long startFrameIndex,
        long endFrameIndex,
        double confidenceSum,
        int confidenceFrameCount)
    {
        int activityNumber =
            ++_activityNumbers[speakerIndex];

        return new OpenActivity(
            $"sortformer-speaker-{speakerIndex + 1}-" +
                $"{activityNumber:000000}",
            speakerIndex,
            startFrameIndex,
            endFrameIndex,
            confidenceSum,
            confidenceFrameCount);
    }

    private SpeakerActivity CreateUpdate(
        OpenActivity activity,
        double frameDurationSeconds,
        bool isFinal)
    {
        return new SpeakerActivity(
            activity.ActivityId,
            ++_sequence,
            $"speaker-{activity.SpeakerIndex + 1}",
            TimeSpan.FromSeconds(
                Math.Round(
                    activity.StartFrameIndex * frameDurationSeconds,
                    3)),
            TimeSpan.FromSeconds(
                Math.Round(
                    activity.EndFrameIndex * frameDurationSeconds,
                    3)),
            isFinal,
            Math.Round(
                activity.ConfidenceSum /
                    activity.ConfidenceFrameCount,
                6));
    }

    private sealed class PendingStart
    {
        public long StartFrameIndex { get; private set; }
        public long EndFrameIndex { get; private set; }
        public double ConfidenceSum { get; private set; }
        public int FrameCount { get; private set; }

        public void AddFrame(
            long frameIndex,
            double confidence)
        {
            if (FrameCount == 0)
            {
                StartFrameIndex = frameIndex;
            }
            else if (frameIndex != EndFrameIndex)
            {
                throw new InvalidDataException(
                    "Pending speaker-activity frames must be contiguous.");
            }

            EndFrameIndex = frameIndex + 1;
            ConfidenceSum += confidence;
            FrameCount++;
        }

        public void Reset()
        {
            StartFrameIndex = 0;
            EndFrameIndex = 0;
            ConfidenceSum = 0;
            FrameCount = 0;
        }
    }

    private sealed class OpenActivity(
        string activityId,
        int speakerIndex,
        long startFrameIndex,
        long endFrameIndex,
        double confidenceSum,
        int confidenceFrameCount)
    {
        public string ActivityId { get; } = activityId;
        public int SpeakerIndex { get; } = speakerIndex;
        public long StartFrameIndex { get; } = startFrameIndex;
        public long EndFrameIndex { get; private set; } = endFrameIndex;
        public double ConfidenceSum { get; private set; } = confidenceSum;
        public int ConfidenceFrameCount { get; private set; } =
            confidenceFrameCount;

        public void AddFrame(
            long frameIndex,
            double confidence)
        {
            if (frameIndex != EndFrameIndex)
            {
                throw new InvalidDataException(
                    "Open speaker-activity frames must be contiguous.");
            }

            EndFrameIndex++;
            ConfidenceSum += confidence;
            ConfidenceFrameCount++;
        }
    }
}
