namespace TransGo.Core.Diarization;

/// <summary>
/// Reconciles Sortformer speaker-activity revisions into stable
/// overlap regions. The detector is independent of caption rendering
/// and can therefore be benchmarked without changing visible text.
/// </summary>
public sealed class SpeakerOverlapDetector
{
    private readonly object _gate = new();
    private readonly OverlapDetectionOptions _options;

    private readonly Dictionary<string, SpeakerActivity>
        _activities = new(StringComparer.Ordinal);

    private readonly Dictionary<TimeSpan, MutableRegion>
        _regionsByStart = new();

    private long _nextRegionNumber;
    private TimeSpan _observedEnd;
    private bool _completed;

    private long _activityUpdatesReceived;
    private long _staleActivityUpdatesIgnored;
    private long _regionUpdatesPublished;
    private long _regionsStarted;
    private long _regionsFinalized;
    private long _unsupportedSpeakerRegions;
    private TimeSpan _totalFinalOverlapDuration;

    public SpeakerOverlapDetector(
        OverlapDetectionOptions? options = null)
    {
        _options = options ??
            new OverlapDetectionOptions();

        _options.Validate();
    }

    public event EventHandler<OverlapRegionEventArgs>?
        RegionUpdated;

    public void Process(
        SpeakerActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ValidateActivity(activity);

        List<OverlapRegion> updates;

        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException(
                    "Overlap detection is already complete. " +
                    "Reset it before processing another session.");
            }

            _activityUpdatesReceived++;

            if (
                _activities.TryGetValue(
                    activity.ActivityId,
                    out SpeakerActivity? existing)
                &&
                activity.Sequence <= existing.Sequence)
            {
                _staleActivityUpdatesIgnored++;
                return;
            }

            _activities[activity.ActivityId] =
                activity;

            if (activity.EndTime > _observedEnd)
            {
                _observedEnd = activity.EndTime;
            }

            updates = RecalculateLocked(
                forceCompletion: false);

            PruneFinalActivitiesLocked();
        }

        Publish(updates);
    }

    /// <summary>
    /// Finalizes every qualifying region when the audio session ends.
    /// This is required when there is no later activity available to
    /// advance the ordinary end-hysteresis watermark.
    /// </summary>
    public void Complete(
        TimeSpan sessionEndTime)
    {
        if (sessionEndTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionEndTime));
        }

        List<OverlapRegion> updates;

        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;

            if (sessionEndTime > _observedEnd)
            {
                _observedEnd = sessionEndTime;
            }

            updates = RecalculateLocked(
                forceCompletion: true);
        }

        Publish(updates);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _activities.Clear();
            _regionsByStart.Clear();

            _nextRegionNumber = 0;
            _observedEnd = TimeSpan.Zero;
            _completed = false;

            _activityUpdatesReceived = 0;
            _staleActivityUpdatesIgnored = 0;
            _regionUpdatesPublished = 0;
            _regionsStarted = 0;
            _regionsFinalized = 0;
            _unsupportedSpeakerRegions = 0;
            _totalFinalOverlapDuration =
                TimeSpan.Zero;
        }
    }

    public OverlapDetectionMetrics GetMetrics()
    {
        lock (_gate)
        {
            return new OverlapDetectionMetrics(
                ActivityUpdatesReceived:
                    _activityUpdatesReceived,

                StaleActivityUpdatesIgnored:
                    _staleActivityUpdatesIgnored,

                RegionUpdatesPublished:
                    _regionUpdatesPublished,

                RegionsStarted:
                    _regionsStarted,

                RegionsFinalized:
                    _regionsFinalized,

                UnsupportedSpeakerRegions:
                    _unsupportedSpeakerRegions,

                TotalFinalOverlapDuration:
                    _totalFinalOverlapDuration);
        }
    }

    private List<OverlapRegion> RecalculateLocked(
        bool forceCompletion)
    {
        List<CandidateRegion> candidates =
            BuildCandidatesLocked();

        var updates =
            new List<OverlapRegion>();

        foreach (CandidateRegion candidate in candidates)
        {
            if (
                candidate.OverlapDuration <
                    _options.MinimumOverlapDuration)
            {
                continue;
            }

            bool startIsStable =
                forceCompletion
                ||
                _observedEnd >=
                    candidate.StartTime +
                    _options.StartHysteresis;

            if (!startIsStable)
            {
                continue;
            }

            if (
                !_regionsByStart.TryGetValue(
                    candidate.StartTime,
                    out MutableRegion? mutableRegion))
            {
                mutableRegion =
                    new MutableRegion(
                        RegionId:
                            $"overlap-{++_nextRegionNumber:000000}",

                        StartTime:
                            candidate.StartTime);

                _regionsByStart.Add(
                    candidate.StartTime,
                    mutableRegion);
            }

            if (mutableRegion.IsFinal)
            {
                continue;
            }

            bool shouldFinalize =
                forceCompletion
                ||
                candidate.EndIsSealed
                &&
                _observedEnd >=
                    candidate.EndTime +
                    _options.EndHysteresis;

            bool exceedsSpeakerLimit =
                candidate.ActiveSpeakerIds.Count >
                    _options.MaximumSupportedSpeakers;

            TimeSpan audioStartTime =
                candidate.StartTime >
                    _options.PreRoll
                    ? candidate.StartTime -
                      _options.PreRoll
                    : TimeSpan.Zero;

            TimeSpan audioEndTime =
                candidate.EndTime +
                _options.PostRoll;

            bool changed =
                !mutableRegion.HasPublished
                ||
                mutableRegion.EndTime !=
                    candidate.EndTime
                ||
                mutableRegion.OverlapDuration !=
                    candidate.OverlapDuration
                ||
                !SpeakerIdsEqual(
                    mutableRegion.ActiveSpeakerIds,
                    candidate.ActiveSpeakerIds)
                ||
                mutableRegion.DetectionConfidence !=
                    candidate.DetectionConfidence
                ||
                mutableRegion.ExceedsSpeakerLimit !=
                    exceedsSpeakerLimit
                ||
                mutableRegion.IsFinal !=
                    shouldFinalize;

            mutableRegion.EndTime =
                candidate.EndTime;

            mutableRegion.OverlapDuration =
                candidate.OverlapDuration;

            mutableRegion.ActiveSpeakerIds =
                candidate.ActiveSpeakerIds;

            mutableRegion.DetectionConfidence =
                candidate.DetectionConfidence;

            mutableRegion.ExceedsSpeakerLimit =
                exceedsSpeakerLimit;

            mutableRegion.AudioStartTime =
                audioStartTime;

            mutableRegion.AudioEndTime =
                audioEndTime;

            mutableRegion.IsFinal =
                shouldFinalize;

            if (!changed)
            {
                continue;
            }

            bool isFirstPublication =
                !mutableRegion.HasPublished;

            mutableRegion.HasPublished = true;
            mutableRegion.Revision++;

            OverlapRegion region =
                mutableRegion.ToImmutable();

            updates.Add(region);
            _regionUpdatesPublished++;

            if (isFirstPublication)
            {
                _regionsStarted++;
            }

            if (
                exceedsSpeakerLimit
                &&
                !mutableRegion.UnsupportedWasCounted)
            {
                mutableRegion.UnsupportedWasCounted = true;
                _unsupportedSpeakerRegions++;
            }

            if (shouldFinalize)
            {
                _regionsFinalized++;
                _totalFinalOverlapDuration +=
                    candidate.OverlapDuration;
            }
        }

        return updates;
    }

    private List<CandidateRegion>
        BuildCandidatesLocked()
    {
        SpeakerActivity[] activities =
            _activities.Values
                .OrderBy(activity =>
                    activity.StartTime)
                .ThenBy(activity =>
                    activity.EndTime)
                .ToArray();

        if (activities.Length < 2)
        {
            return [];
        }

        TimeSpan[] boundaries =
            activities
                .SelectMany(activity =>
                    new[]
                    {
                        activity.StartTime,
                        activity.EndTime,
                    })
                .Distinct()
                .Order()
                .ToArray();

        var segments =
            new List<CandidateSegment>();

        for (
            int boundaryIndex = 0;
            boundaryIndex < boundaries.Length - 1;
            boundaryIndex++)
        {
            TimeSpan start =
                boundaries[boundaryIndex];

            TimeSpan end =
                boundaries[boundaryIndex + 1];

            if (end <= start)
            {
                continue;
            }

            IGrouping<string, SpeakerActivity>[]
                activeSpeakerGroups =
                    activities
                        .Where(activity =>
                            activity.StartTime < end
                            &&
                            activity.EndTime > start)
                        .GroupBy(
                            activity =>
                                activity.SpeakerId,

                            StringComparer.OrdinalIgnoreCase)
                        .OrderBy(group =>
                            group.Key,

                            StringComparer.OrdinalIgnoreCase)
                        .ToArray();

            if (activeSpeakerGroups.Length < 2)
            {
                continue;
            }

            string[] speakerIds =
                activeSpeakerGroups
                    .Select(group =>
                        group.Key)
                    .ToArray();

            double[] confidences =
                activeSpeakerGroups
                    .Select(group =>
                        group
                            .Where(activity =>
                                activity.Confidence is not null)
                            .Select(activity =>
                                activity.Confidence!.Value)
                            .DefaultIfEmpty(double.NaN)
                            .Max())
                    .Where(double.IsFinite)
                    .ToArray();

            double? confidence =
                confidences.Length == 0
                    ? null
                    : confidences.Average();

            bool endIsSealed =
                activeSpeakerGroups
                    .SelectMany(group => group)
                    .Any(activity =>
                        activity.IsFinal
                        &&
                        activity.EndTime == end);

            segments.Add(
                new CandidateSegment(
                    StartTime: start,
                    EndTime: end,
                    ActiveSpeakerIds: speakerIds,
                    DetectionConfidence: confidence,
                    EndIsSealed: endIsSealed));
        }

        return MergeSegments(segments);
    }

    private List<CandidateRegion> MergeSegments(
        IReadOnlyList<CandidateSegment> segments)
    {
        var regions =
            new List<CandidateRegion>();

        foreach (CandidateSegment segment in segments)
        {
            TimeSpan duration =
                segment.EndTime -
                segment.StartTime;

            if (regions.Count == 0)
            {
                regions.Add(
                    CandidateRegion.FromSegment(
                        segment,
                        duration));

                continue;
            }

            CandidateRegion previous =
                regions[^1];

            TimeSpan gap =
                segment.StartTime -
                previous.EndTime;

            if (gap > _options.MergeGap)
            {
                regions.Add(
                    CandidateRegion.FromSegment(
                        segment,
                        duration));

                continue;
            }

            previous.AddSegment(
                segment,
                duration);
        }

        foreach (CandidateRegion region in regions)
        {
            region.Finish();
        }

        return regions;
    }

    private void PruneFinalActivitiesLocked()
    {
        TimeSpan retention =
            _options.EndHysteresis +
            _options.MergeGap +
            _options.PostRoll;

        TimeSpan cutoff =
            _observedEnd > retention
                ? _observedEnd - retention
                : TimeSpan.Zero;

        string[] expiredActivityIds =
            _activities
                .Where(entry =>
                    entry.Value.IsFinal
                    &&
                    entry.Value.EndTime < cutoff)
                .Select(entry =>
                    entry.Key)
                .ToArray();

        foreach (string activityId in expiredActivityIds)
        {
            _activities.Remove(activityId);
        }
    }

    private void Publish(
        IEnumerable<OverlapRegion> updates)
    {
        EventHandler<OverlapRegionEventArgs>?
            handlers = RegionUpdated;

        if (handlers is null)
        {
            return;
        }

        foreach (OverlapRegion region in updates)
        {
            var eventArgs =
                new OverlapRegionEventArgs(
                    region);

            foreach (
                EventHandler<OverlapRegionEventArgs>
                    handler
                in handlers.GetInvocationList())
            {
                try
                {
                    handler(
                        this,
                        eventArgs);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "An overlap-region event handler " +
                        $"failed: {exception}");
                }
            }
        }
    }

    private static bool SpeakerIdsEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        return left.SequenceEqual(
            right,
            StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateActivity(
        SpeakerActivity activity)
    {
        if (string.IsNullOrWhiteSpace(activity.ActivityId))
        {
            throw new ArgumentException(
                "An activity ID is required.",
                nameof(activity));
        }

        if (activity.Sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activity),
                "The activity sequence must be positive.");
        }

        if (string.IsNullOrWhiteSpace(activity.SpeakerId))
        {
            throw new ArgumentException(
                "A speaker ID is required.",
                nameof(activity));
        }

        if (
            activity.StartTime < TimeSpan.Zero
            ||
            activity.EndTime <= activity.StartTime)
        {
            throw new ArgumentException(
                "The activity time range is invalid.",
                nameof(activity));
        }

        if (
            activity.Confidence is double confidence
            &&
            (
                !double.IsFinite(confidence)
                ||
                confidence < 0
                ||
                confidence > 1
            ))
        {
            throw new ArgumentOutOfRangeException(
                nameof(activity),
                "Activity confidence must be between zero and one.");
        }
    }

    private sealed class MutableRegion(
        string RegionId,
        TimeSpan StartTime)
    {
        public string RegionId { get; } =
            RegionId;

        public TimeSpan StartTime { get; } =
            StartTime;

        public long Revision { get; set; }

        public TimeSpan EndTime { get; set; }

        public TimeSpan OverlapDuration { get; set; }

        public IReadOnlyList<string>
            ActiveSpeakerIds { get; set; } = [];

        public double?
            DetectionConfidence { get; set; }

        public bool ExceedsSpeakerLimit { get; set; }

        public bool UnsupportedWasCounted { get; set; }

        public TimeSpan AudioStartTime { get; set; }

        public TimeSpan AudioEndTime { get; set; }

        public bool HasPublished { get; set; }

        public bool IsFinal { get; set; }

        public OverlapRegion ToImmutable()
        {
            return new OverlapRegion(
                RegionId:
                    RegionId,

                Revision:
                    Revision,

                StartTime:
                    StartTime,

                EndTime:
                    EndTime,

                OverlapDuration:
                    OverlapDuration,

                ActiveSpeakerIds:
                    ActiveSpeakerIds.ToArray(),

                DetectionConfidence:
                    DetectionConfidence,

                IsFinal:
                    IsFinal,

                ExceedsSupportedSpeakerCount:
                    ExceedsSpeakerLimit,

                AudioStartTime:
                    AudioStartTime,

                AudioEndTime:
                    AudioEndTime);
        }
    }

    private sealed record CandidateSegment(
        TimeSpan StartTime,
        TimeSpan EndTime,
        IReadOnlyList<string> ActiveSpeakerIds,
        double? DetectionConfidence,
        bool EndIsSealed);

    private sealed class CandidateRegion
    {
        private readonly HashSet<string> _speakerIds =
            new(StringComparer.OrdinalIgnoreCase);

        private double _confidenceDurationSeconds;
        private double _weightedConfidence;

        private CandidateRegion()
        {
        }

        public TimeSpan StartTime { get; private set; }

        public TimeSpan EndTime { get; private set; }

        public TimeSpan OverlapDuration { get; private set; }

        public IReadOnlyList<string>
            ActiveSpeakerIds { get; private set; } = [];

        public double? DetectionConfidence { get; private set; }

        public bool EndIsSealed { get; private set; }

        public static CandidateRegion FromSegment(
            CandidateSegment segment,
            TimeSpan duration)
        {
            var region =
                new CandidateRegion
                {
                    StartTime = segment.StartTime,
                    EndTime = segment.EndTime,
                    OverlapDuration = duration,
                    EndIsSealed = segment.EndIsSealed,
                };

            region.AddSpeakers(
                segment.ActiveSpeakerIds);

            region.AddConfidence(
                segment.DetectionConfidence,
                duration);

            return region;
        }

        public void AddSegment(
            CandidateSegment segment,
            TimeSpan duration)
        {
            EndTime = segment.EndTime;
            OverlapDuration += duration;
            EndIsSealed = segment.EndIsSealed;

            AddSpeakers(
                segment.ActiveSpeakerIds);

            AddConfidence(
                segment.DetectionConfidence,
                duration);
        }

        public void Finish()
        {
            ActiveSpeakerIds =
                _speakerIds
                    .Order(
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            DetectionConfidence =
                _confidenceDurationSeconds > 0
                    ? _weightedConfidence /
                      _confidenceDurationSeconds
                    : null;
        }

        private void AddSpeakers(
            IEnumerable<string> speakerIds)
        {
            foreach (string speakerId in speakerIds)
            {
                _speakerIds.Add(speakerId);
            }
        }

        private void AddConfidence(
            double? confidence,
            TimeSpan duration)
        {
            if (confidence is not double value)
            {
                return;
            }

            _weightedConfidence +=
                value * duration.TotalSeconds;

            _confidenceDurationSeconds +=
                duration.TotalSeconds;
        }
    }
}
