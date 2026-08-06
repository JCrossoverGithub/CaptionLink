using TransGo.Core.Transcription;

namespace TransGo.Core.Diarization;

/// <summary>
/// Assigns a transcript segment to the speaker whose activity
/// has the greatest total overlap with the segment's time range.
/// </summary>
public static class SpeakerAttributionResolver
{
    public static string? ResolveSpeakerId(
        TranscriptResult result,
        IEnumerable<SpeakerActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(activities);

        if (
            result.ResultStartTime is not TimeSpan resultStart
            ||
            result.ResultEndTime is not TimeSpan resultEnd
            ||
            resultEnd <= resultStart)
        {
            return null;
        }

        var overlapBySpeaker =
            new Dictionary<
                string,
                (TimeSpan Total, TimeSpan LatestEnd)>(
                StringComparer.OrdinalIgnoreCase);

        foreach (SpeakerActivity activity in activities)
        {
            if (
                string.IsNullOrWhiteSpace(
                    activity.SpeakerId)
                ||
                activity.EndTime <= activity.StartTime)
            {
                continue;
            }

            TimeSpan overlapStart =
                resultStart > activity.StartTime
                    ? resultStart
                    : activity.StartTime;

            TimeSpan overlapEnd =
                resultEnd < activity.EndTime
                    ? resultEnd
                    : activity.EndTime;

            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            TimeSpan overlap =
                overlapEnd - overlapStart;

            string speakerId =
                activity.SpeakerId.Trim();

            if (
                overlapBySpeaker.TryGetValue(
                    speakerId,
                    out var existing))
            {
                overlapBySpeaker[speakerId] =
                    (
                        existing.Total + overlap,
                        overlapEnd > existing.LatestEnd
                            ? overlapEnd
                            : existing.LatestEnd
                    );
            }
            else
            {
                overlapBySpeaker[speakerId] =
                    (
                        overlap,
                        overlapEnd
                    );
            }
        }

        string? bestSpeakerId = null;
        TimeSpan bestTotalOverlap =
            TimeSpan.Zero;

        TimeSpan bestLatestEnd =
            TimeSpan.Zero;

        foreach (
            KeyValuePair<
                string,
                (TimeSpan Total, TimeSpan LatestEnd)> entry
            in overlapBySpeaker)
        {
            bool isBetter =
                bestSpeakerId is null
                ||
                entry.Value.Total >
                    bestTotalOverlap
                ||
                (
                    entry.Value.Total ==
                        bestTotalOverlap
                    &&
                    entry.Value.LatestEnd >
                        bestLatestEnd
                )
                ||
                (
                    entry.Value.Total ==
                        bestTotalOverlap
                    &&
                    entry.Value.LatestEnd ==
                        bestLatestEnd
                    &&
                    StringComparer.OrdinalIgnoreCase.Compare(
                        entry.Key,
                        bestSpeakerId) < 0
                );

            if (!isBetter)
            {
                continue;
            }

            bestSpeakerId =
                entry.Key;

            bestTotalOverlap =
                entry.Value.Total;

            bestLatestEnd =
                entry.Value.LatestEnd;
        }

        return bestSpeakerId;
    }
}