using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DMUP4DebuffHelper;

// Adapted from UMAD Helper's PollKefkaCharge, ReleaseReality and Combine.
// MIT reuse permission confirmed by the user on 2026-10-29.
// See docs/mana-release-analysis.md for provenance and deliberate differences.
internal sealed class ManaReleaseStateMachine
{
    internal const uint ManaChargeActionId = 47780;
    internal const uint ManaReleaseActionId = 47781;
    internal const uint ThunderChargedStatusId = 1485;
    internal const uint IceChargedStatusId = 1484; // Proposed by UMAD; verify in game.

    private readonly TimeProvider clock;
    private readonly Action<string> record;
    private readonly ConcurrentQueue<ManaObservation> observations = new();
    private readonly List<ManaObservation> markers = [];
    private readonly HashSet<uint> lastChargeStatuses = [];
    private readonly Dictionary<ManaElement, Charge> charges = [];
    private readonly HashSet<long> classifiedReleaseMarkers = [];
    private long generation;
    private long sequence;
    private long cycleStarted;
    private long? completedAt;
    private bool completedByEffect;
    private bool cycleActive;
    private bool grandCrossObserved;
    private bool releaseCastObserved;
    private bool wasReleaseCasting;
    private bool publicationRequested;
    private uint kefkaEntityId;
    private ManaReleaseDisplayState? lastPublished;

    public ManaReleaseStateMachine(Action<string> record, TimeProvider? timeProvider = null)
    {
        this.record = record;
        clock = timeProvider ?? TimeProvider.System;
    }

    public event Action<ManaReleaseDisplayState>? StateChanged;
    public ManaReleaseDisplayState? DisplayState { get; private set; }

    // Native hooks only capture and enqueue immutable values. All encounter state,
    // object-table reads, diagnostics and UI publication stay on the framework thread.
    public ManaObservation CaptureObservation(ManaObservationKind kind, uint actorId, uint value, uint arg2 = 0)
    {
        var capturedGeneration = Volatile.Read(ref generation);
        return new(capturedGeneration, Interlocked.Increment(ref sequence), clock.GetTimestamp(), actorId, value, arg2, kind);
    }

    public void Enqueue(ManaObservation observation) => observations.Enqueue(observation);

    public void Update(ManaKefkaSnapshot? kefka, bool hasGrandCross)
    {
        var now = clock.GetTimestamp();
        if (kefka is { } actor)
        {
            if (kefkaEntityId != 0 && kefkaEntityId != actor.EntityId)
                Reset("Kefka actor changed");
            kefkaEntityId = actor.EntityId;
        }

        if (!cycleActive)
            markers.RemoveAll(marker => Seconds(marker.Timestamp, now) > 60);

        if (hasGrandCross && !grandCrossObserved)
            StartCycle(now, "first Grand Cross status");
        grandCrossObserved = hasGrandCross;

        var queued = new List<ManaObservation>();
        while (observations.TryDequeue(out var observation))
            queued.Add(observation);
        foreach (var observation in queued.OrderBy(item => item.Timestamp).ThenBy(item => item.Sequence))
            ProcessObservation(observation, now);

        if (kefka is { } current)
        {
            var statuses = current.StatusIds.Where(IsChargeStatus).ToHashSet();
            foreach (var gained in statuses.Except(lastChargeStatuses).OrderBy(id => id))
            {
                StartCycle(now, "Charged status recovery");
                var element = gained == ThunderChargedStatusId ? ManaElement.Lightning : ManaElement.Ice;
                if (!charges.ContainsKey(element))
                {
                    charges.Add(element, new Charge(now));
                    record($"{element} Charged status {gained} gained on Kefka {current.EntityId:X8}; statuses=[{string.Join(",", current.StatusIds)}].");
                }
            }
            lastChargeStatuses.Clear();
            lastChargeStatuses.UnionWith(statuses);

            var casting = current.CastActionId == ManaReleaseActionId;
            if (cycleActive && casting && !wasReleaseCasting)
            {
                releaseCastObserved = true;
                record("Mana Release cast observed; preserving banked and pre-cast release markers.");
            }
            if (cycleActive && !casting && wasReleaseCasting && completedAt is null)
                Complete(now, byEffect: false);
            wasReleaseCasting = casting;
        }
        // An absent object-table entry is not evidence of cast completion.
        if (!cycleActive)
            return;

        BankPendingCharges(now);
        Publish();
        if (completedAt is { } completion && Seconds(completion, now) >= 10)
        {
            ReportIncomplete();
            Reset("Mana Release ten-second display hold ended");
        }
        else if (Seconds(cycleStarted, now) >= 200)
        {
            ReportIncomplete();
            Reset("Mana cycle exceeded 200 seconds");
        }
    }

    private void ProcessObservation(ManaObservation observation, long now)
    {
        var description = $"#{observation.Sequence} t={observation.Timestamp} generation={observation.Generation}, actor={observation.EntityId:X8}, value={observation.Value} (0x{observation.Value:X}), arg2={observation.Arg2}";
        if (observation.Generation != Volatile.Read(ref generation))
        {
            record($"Rejected old-generation {observation.Kind}: {description}.");
            return;
        }
        if (observation.EntityId != kefkaEntityId || kefkaEntityId == 0)
        {
            record($"Rejected non-Kefka {observation.Kind}: {description}.");
            return;
        }
        if (observation.Kind == ManaObservationKind.ActionEffect)
        {
            if (observation.Value == ManaReleaseActionId)
            {
                StartCycle(observation.Timestamp, "Mana Release effect recovery");
                Complete(observation.Timestamp, byEffect: true);
            }
            return;
        }
        if (ManaMarker(observation.Value) is null)
        {
            record($"Unknown Kefka target icon: {description}.");
            return;
        }
        if (!cycleActive && Seconds(observation.Timestamp, now) > 60)
        {
            record($"Expired prebuffer target icon: {description}.");
            return;
        }
        if (completedAt is { } completion && Seconds(completion, observation.Timestamp) >= 10)
        {
            record($"Late target icon after completion hold: {description}.");
            return;
        }
        markers.Add(observation);
        record($"Verified Kefka target icon {(cycleActive ? "retained in cycle" : "prebuffered")}: {description}.");
    }

    private void StartCycle(long timestamp, string reason)
    {
        if (cycleActive)
            return;
        markers.RemoveAll(marker => Seconds(marker.Timestamp, timestamp) > 60);
        cycleStarted = timestamp;
        cycleActive = true;
        record($"Mana cycle started from {reason}; retained {markers.Count} prebuffered markers, generation={generation}, clock frequency={clock.TimestampFrequency} ticks/second.");
    }

    private void BankPendingCharges(long now)
    {
        foreach (var (element, charge) in charges.Where(pair => !pair.Value.Banked))
        {
            // UMAD uses the latest matching marker within a three-second lead.
            // Bound the trailing side to its ten-second wait using receipt time.
            var marker = markers.Where(item => ManaMarker(item.Value)?.Element == element &&
                    Seconds(charge.StatusAt, item.Timestamp) >= -3 &&
                    Seconds(charge.StatusAt, item.Timestamp) <= 10 &&
                    (completedAt is null || item.Timestamp <= completedAt.Value))
                .OrderByDescending(item => item.Timestamp).ThenByDescending(item => item.Sequence).FirstOrDefault();
            if (marker is not null)
            {
                charge.Reality = ManaMarker(marker.Value)!.Value.Reality;
                charge.MarkerAt = marker.Timestamp;
                charge.Banked = true;
                record($"Banked {element}: {charge.Reality}, marker #{marker.Sequence} {marker.Value}, offset={Seconds(charge.StatusAt, marker.Timestamp):F3}s from Charged status.");
            }
            else if (Seconds(charge.StatusAt, now) >= 10 || completedAt is not null)
            {
                charge.Banked = true;
                charge.MarkerAt = charge.StatusAt;
                record($"Banked {element}: Unknown; charge marker missing ({(completedAt is null ? "ten-second timeout" : "release completed")}).");
            }
        }
    }

    private (RealityState Reality, bool Observed) ReleaseReality(ManaElement element, long boundary)
    {
        var candidates = markers.Where(item => ManaMarker(item.Value)?.Element == element &&
                Seconds(boundary, item.Timestamp) > 0.5)
            .OrderBy(item => item.Timestamp).ThenBy(item => item.Sequence).ToList();
        if (candidates.Count == 0)
            return (RealityState.Unknown, false);
        var reality = ManaMarker(candidates[0].Value)!.Value.Reality;
        foreach (var candidate in candidates)
        {
            if (classifiedReleaseMarkers.Add(candidate.Sequence))
            {
                var classification = candidate == candidates[0] ? "selected" :
                    ManaMarker(candidate.Value)!.Value.Reality == reality ? "duplicate" : "conflicting";
                record($"{element} release marker {classification}: #{candidate.Sequence}, icon={candidate.Value} (0x{candidate.Value:X}), actor={candidate.EntityId:X8}, t={candidate.Timestamp}, offset={Seconds(boundary, candidate.Timestamp):F3}s after last charge.");
            }
        }
        return (candidates.Any(item => ManaMarker(item.Value)!.Value.Reality != reality)
            ? RealityState.Unknown : reality, true);
    }

    private void Publish()
    {
        var lightning = Bank(ManaElement.Lightning);
        var ice = Bank(ManaElement.Ice);
        var lightningRelease = RealityState.Unknown;
        var iceRelease = RealityState.Unknown;
        var releaseObserved = false;
        if (charges.Count == 2 && charges.Values.All(charge => charge.Banked))
        {
            var boundary = charges.Values.Max(charge => charge.MarkerAt);
            var lightningResult = ReleaseReality(ManaElement.Lightning, boundary);
            var iceResult = ReleaseReality(ManaElement.Ice, boundary);
            lightningRelease = lightningResult.Reality;
            iceRelease = iceResult.Reality;
            releaseObserved = lightningResult.Observed || iceResult.Observed;
        }
        var finalLightning = Combine(lightning, lightningRelease);
        var finalIce = Combine(ice, iceRelease);
        var safe = finalLightning == RealityState.Unknown || finalIce == RealityState.Unknown
            ? ManaReleaseSafeZone.Unknown
            : (finalLightning == RealityState.Fake, finalIce == RealityState.Fake) switch
            {
                (true, false) => ManaReleaseSafeZone.Lightning,
                (false, true) => ManaReleaseSafeZone.Ice,
                (true, true) => ManaReleaseSafeZone.Both,
                _ => ManaReleaseSafeZone.None,
            };
        DisplayState = new(true, lightning, ice, lightningRelease, iceRelease,
            releaseObserved || releaseCastObserved || completedAt is not null, safe);
        if (DisplayState != lastPublished || publicationRequested)
        {
            if (charges.Count == 2 && charges.Values.All(charge => charge.Banked))
            {
                var boundary = charges.Values.Max(charge => charge.MarkerAt);
                record($"Release boundary t={boundary} + 0.5s; Lightning bank={lightning}, release={lightningRelease}, combined={finalLightning}; Ice bank={ice}, release={iceRelease}, combined={finalIce}; safe={safe}. Contradictory candidates remain Unknown.");
            }
            lastPublished = DisplayState;
            publicationRequested = false;
            StateChanged?.Invoke(DisplayState);
        }
    }

    private void Complete(long timestamp, bool byEffect)
    {
        if (byEffect && completedByEffect)
        {
            record("Duplicate Mana Release action effect ignored.");
            return;
        }
        if (!byEffect && completedAt is not null)
            return;
        completedAt = timestamp;
        completedByEffect = byEffect;
        publicationRequested = true;
        record(byEffect
            ? "Mana Release resolved by Kefka action effect 47781; retaining markers and display for ten seconds."
            : "Mana Release completion fallback: cast disappeared without an action effect; retaining markers and display for ten seconds.");
    }

    private RealityState Bank(ManaElement element) =>
        charges.TryGetValue(element, out var charge) && charge.Banked ? charge.Reality : RealityState.Unknown;

    private void ReportIncomplete()
    {
        if (DisplayState is { SafeZone: ManaReleaseSafeZone.Unknown } state)
            record($"Mana Release final result Unknown: Lightning charge={Bank(ManaElement.Lightning)}, release={state.LightningRelease}; Ice charge={Bank(ManaElement.Ice)}, release={state.IceRelease}. Missing or conflicting marker/status evidence.");
    }

    public void Reset(string reason)
    {
        var hadEvidence = cycleActive || markers.Count > 0 || kefkaEntityId != 0 || !observations.IsEmpty;
        Interlocked.Increment(ref generation);
        while (observations.TryDequeue(out _)) { }
        markers.Clear();
        charges.Clear();
        classifiedReleaseMarkers.Clear();
        lastChargeStatuses.Clear();
        cycleActive = false;
        grandCrossObserved = false;
        releaseCastObserved = false;
        wasReleaseCasting = false;
        publicationRequested = false;
        completedAt = null;
        completedByEffect = false;
        cycleStarted = 0;
        kefkaEntityId = 0;
        DisplayState = null;
        lastPublished = null;
        if (hadEvidence)
            record($"Reset: {reason}; generation={generation}.");
    }

    private double Seconds(long start, long end) => clock.GetElapsedTime(start, end).TotalSeconds;
    private static bool IsChargeStatus(uint id) => id is ThunderChargedStatusId or IceChargedStatusId;
    private static (ManaElement Element, RealityState Reality)? ManaMarker(uint id) => id switch
    {
        675 => (ManaElement.Ice, RealityState.Fake),
        676 => (ManaElement.Ice, RealityState.Real),
        677 => (ManaElement.Lightning, RealityState.Fake),
        678 => (ManaElement.Lightning, RealityState.Real),
        _ => null,
    };

    private static RealityState Combine(RealityState bank, RealityState release) =>
        bank == RealityState.Unknown || release == RealityState.Unknown ? RealityState.Unknown :
        bank == release ? RealityState.Real : RealityState.Fake;

    private sealed class Charge(long statusAt)
    {
        public long StatusAt { get; } = statusAt;
        public long MarkerAt { get; set; }
        public RealityState Reality { get; set; }
        public bool Banked { get; set; }
    }
}

internal enum ManaElement { Lightning, Ice }
internal enum ManaObservationKind { TargetIcon, ActionEffect }
internal sealed record ManaObservation(long Generation, long Sequence, long Timestamp, uint EntityId, uint Value, uint Arg2, ManaObservationKind Kind);
internal sealed record ManaKefkaSnapshot(uint EntityId, uint CastActionId, IReadOnlyList<uint> StatusIds);
