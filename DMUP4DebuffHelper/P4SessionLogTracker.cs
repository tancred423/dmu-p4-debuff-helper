using System;
using System.Collections.Generic;
using System.Linq;

namespace DMUP4DebuffHelper;

/// <summary>In-memory, player-focused pull history. It intentionally has no persistence or size cap.</summary>
internal sealed class P4SessionLogTracker
{
    private sealed class CurrentLog
    {
        public readonly List<P4DebuffRecord> Assignments = [];
        public readonly Dictionary<string, int> AssignmentIndexes = new(StringComparer.Ordinal);
        public readonly Dictionary<P4TimingBucket, RealityState> Gazes = [];
        public RealityState Lightning1 = RealityState.Unknown;
        public RealityState Lightning2 = RealityState.Unknown;
        public RealityState Ice1 = RealityState.Unknown;
        public RealityState Ice2 = RealityState.Unknown;
        public ManaReleaseSafeZone Result = ManaReleaseSafeZone.Unknown;
        public bool HasManaData;
    }

    private CurrentLog? current;
    private readonly List<P4LogPullSnapshot> completed = [];

    public IReadOnlyList<P4LogPullSnapshot> Completed => completed;
    public P4LogPullSnapshot? Current(float elapsed) => current is null ? null : Snapshot(current, "In progress", elapsed);

    public void ObserveAssignments(IEnumerable<P4DebuffRecord> records)
    {
        var values = records.ToList();
        if (values.Count == 0)
        {
            return;
        }

        var log = current ??= new CurrentLog();
        foreach (var record in values)
        {
            if (!log.AssignmentIndexes.TryGetValue(record.Key, out var index))
            {
                log.AssignmentIndexes[record.Key] = log.Assignments.Count;
                log.Assignments.Add(record);
                continue;
            }

            var previous = log.Assignments[index];
            if (previous.Reality == RealityState.Unknown && record.Reality != RealityState.Unknown ||
                previous.WoundColor == WoundColor.None && record.WoundColor != WoundColor.None ||
                previous.FloodSide == FloodSide.None && record.FloodSide != FloodSide.None)
            {
                log.Assignments[index] = record with { SeenAtUtc = previous.SeenAtUtc, RemainingTimeAtCapture = previous.RemainingTimeAtCapture };
            }
        }
    }

    public void ObserveGazes(IEnumerable<P4DebuffAssignment> gazes)
    {
        foreach (var gaze in gazes.Where(gaze => gaze.TimingBucket is P4TimingBucket.Short or P4TimingBucket.Long))
        {
            var log = current ??= new CurrentLog();
            if (!log.Gazes.TryGetValue(gaze.TimingBucket, out var currentReality) ||
                currentReality == RealityState.Unknown && gaze.Reality != RealityState.Unknown)
            {
                log.Gazes[gaze.TimingBucket] = gaze.Reality;
            }
        }
    }

    public void ObserveMana(ManaReleaseDisplayState state)
    {
        var hasState = state.LightningStored != RealityState.Unknown || state.IceStored != RealityState.Unknown ||
            state.LightningRelease != RealityState.Unknown || state.IceRelease != RealityState.Unknown ||
            state.SafeZone != ManaReleaseSafeZone.Unknown;
        if (!hasState)
        {
            return;
        }

        var log = current ??= new CurrentLog();
        Merge(ref log.Lightning1, state.LightningStored);
        Merge(ref log.Ice1, state.IceStored);
        var releaseInvalidated = false;
        MergeRelease(ref log.Lightning2, state.LightningRelease, state.IsManaReleaseActive, ref releaseInvalidated);
        MergeRelease(ref log.Ice2, state.IceRelease, state.IsManaReleaseActive, ref releaseInvalidated);
        if (state.SafeZone != ManaReleaseSafeZone.Unknown)
        {
            log.Result = state.SafeZone;
        }
        else if (releaseInvalidated)
        {
            log.Result = ManaReleaseSafeZone.Unknown;
        }
        log.HasManaData = true;
    }

    public void Complete(string reason, float elapsed)
    {
        if (current is null)
        {
            return;
        }

        completed.Add(Snapshot(current, reason, elapsed));
        current = null;
    }

    private static void Merge(ref RealityState target, RealityState incoming)
    {
        if (target == RealityState.Unknown && incoming != RealityState.Unknown)
        {
            target = incoming;
        }
    }

    private static void MergeRelease(
        ref RealityState target,
        RealityState incoming,
        bool releaseActive,
        ref bool invalidated)
    {
        if (incoming != RealityState.Unknown)
        {
            Merge(ref target, incoming);
        }
        else if (releaseActive && target != RealityState.Unknown)
        {
            target = RealityState.Unknown;
            invalidated = true;
        }
    }

    private static P4LogPullSnapshot Snapshot(CurrentLog log, string reason, float elapsed) => new(
        DateTime.UtcNow,
        reason,
        elapsed,
        log.Assignments.ToList(),
        [
            new P4GazeLogRecord(P4TimingBucket.Short, log.Gazes.GetValueOrDefault(P4TimingBucket.Short, RealityState.Unknown)),
            new P4GazeLogRecord(P4TimingBucket.Long, log.Gazes.GetValueOrDefault(P4TimingBucket.Long, RealityState.Unknown)),
        ],
        log.HasManaData ? new P4ManaReleaseLogRecord(log.Lightning1, log.Lightning2, log.Ice1, log.Ice2, log.Result, true) : null);
}
