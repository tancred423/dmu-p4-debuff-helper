using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace DMUP4DebuffHelper;

// Synthetic ordering tests and a scripted UMAD example; these are not packet replays.
public sealed class ManaReleaseStateMachineTests
{
    private const uint Kefka = 0x4000B055;
    private const uint Thunder = ManaReleaseStateMachine.ThunderChargedStatusId;
    private const uint Ice = ManaReleaseStateMachine.IceChargedStatusId;
    private const uint ChargeAction = ManaReleaseStateMachine.ManaChargeActionId;
    private const uint ReleaseAction = ManaReleaseStateMachine.ManaReleaseActionId;

    [Fact]
    public void ScriptedUmadSequenceResolvesBeforeTheReleaseCast()
    {
        var f = new Fixture();
        f.Start();
        f.At(54); f.Icon(678); f.Frame([Thunder]);
        f.At(72); f.Icon(675); f.Frame([Thunder, Ice]);
        f.At(83); f.Icon(676); f.Icon(677); f.Frame();
        Assert.Equal(ManaReleaseSafeZone.Both, f.State.SafeZone);
        Assert.True(f.State.IsManaReleaseActive);
        f.At(84); f.Frame(cast: ReleaseAction);
        Assert.Equal(ManaReleaseSafeZone.Both, f.State.SafeZone);
        Assert.Equal(RealityState.Real, f.State.LightningStored);
        Assert.Equal(RealityState.Fake, f.State.IceStored);
    }

    [Theory]
    [InlineData(-3, true)]
    [InlineData(-3.001, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(10.001, false)]
    public void ChargeMarkerAssociationUsesReceiptTimeAndBoundaries(double markerOffset, bool expected)
    {
        var f = new Fixture();
        f.Start();
        if (markerOffset <= 0)
        {
            f.At(20 + markerOffset); f.Icon(678); f.Frame();
            f.At(20); f.Frame([Thunder]);
        }
        else
        {
            f.At(20); f.Frame([Thunder]);
            f.At(20 + markerOffset); f.Icon(678); f.Frame();
        }
        Assert.Equal(expected ? RealityState.Real : RealityState.Unknown, f.State.LightningStored);
    }

    [Fact]
    public void LatestMatchingMarkerInChargeLookbackWins()
    {
        var f = new Fixture(); f.Start();
        f.At(18); f.Icon(677); f.Frame();
        f.At(19); f.Icon(678); f.Frame();
        f.At(20); f.Frame([Thunder]);
        Assert.Equal(RealityState.Real, f.State.LightningStored);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SameFrameStatusAndMarkerOrderingDoesNotMatter(bool markerFirst)
    {
        var f = new Fixture(); f.Start(); f.At(1);
        if (markerFirst) f.Icon(677);
        f.Frame([Thunder]);
        if (!markerFirst) { f.Icon(677); f.Frame(); }
        Assert.Equal(RealityState.Fake, f.State.LightningStored);
    }

    [Fact]
    public void SecondManaChargeAndPersistentStatusesPreserveFirstBank()
    {
        var f = new Fixture(); f.Start();
        f.At(1); f.Icon(678); f.Frame([Thunder]);
        f.At(18); f.Frame(cast: ChargeAction);
        f.At(19); f.Icon(677); f.Frame(cast: ChargeAction);
        Assert.Equal(RealityState.Real, f.State.LightningStored);
        f.At(20); f.Icon(675); f.Frame([Thunder, Ice]);
        Assert.Equal(RealityState.Real, f.State.LightningStored);
        Assert.Equal(RealityState.Fake, f.State.IceStored);
        Assert.Equal(1, f.Messages.Count(message => message.StartsWith("Banked Lightning:")));
    }

    [Fact]
    public void SimultaneousChargeStatusGainsBankBothElements()
    {
        var f = new Fixture(); f.Start(); f.At(10);
        f.Icon(678); f.Icon(676); f.Frame([Thunder, Ice]);
        Assert.Equal(RealityState.Real, f.State.LightningStored);
        Assert.Equal(RealityState.Real, f.State.IceStored);
    }

    [Fact]
    public void NewlySeenChargeCanStartRecoveryCycleWithoutGrandCross()
    {
        var f = new Fixture();
        f.At(1); f.Icon(677); f.Frame([Thunder]);
        Assert.Equal(RealityState.Fake, f.State.LightningStored);
        Assert.Equal(RealityState.Unknown, f.State.IceStored);
        Assert.Contains(f.Messages, message => message.Contains("Charged status recovery"));
    }

    [Fact]
    public void EarlierGrandCrossMarkersAreNotChargeOrReleaseValues()
    {
        var f = new Fixture(); f.Start();
        f.At(1); f.Icon(677); f.Icon(675); f.Frame();
        f.At(10); f.Frame([Thunder]);
        f.At(20); f.Frame([Thunder, Ice]); // Lightning times out.
        f.At(30); f.Frame(); // Ice times out.
        Assert.Equal(RealityState.Unknown, f.State.LightningStored);
        Assert.Equal(RealityState.Unknown, f.State.IceStored);
        Assert.Equal(RealityState.Unknown, f.State.LightningRelease);
        Assert.Equal(RealityState.Unknown, f.State.IceRelease);
        Assert.Contains(f.Messages, message => message.Contains("ten-second timeout"));
    }

    [Fact]
    public void BufferedReleaseMarkersAreReconsideredAfterMissingChargeTimesOut()
    {
        var f = new Fixture(); f.Start();
        f.At(1); f.Icon(678); f.Frame([Thunder]);
        f.At(20); f.Frame([Thunder, Ice]); // Missing Ice bank.
        f.At(31); f.Icon(677); f.Frame(); // Timeout creates Unknown Ice bank at t=20.
        Assert.Equal(RealityState.Fake, f.State.LightningRelease);
        Assert.Equal(RealityState.Unknown, f.State.IceStored);
        Assert.Equal(ManaReleaseSafeZone.Unknown, f.State.SafeZone);
    }

    [Fact]
    public void ChargeAssociationReevaluatesMarkersRetainedBeforeStatuses()
    {
        var f = new Fixture(); f.Start();
        f.At(10); f.Icon(678); f.Frame([Thunder]);
        f.At(20); f.Icon(675); f.Frame();
        f.At(20.6); f.Icon(677); f.Frame(); // Release Lightning, buffered before Ice status.
        f.At(21); f.Frame([Thunder, Ice]);
        Assert.Equal(RealityState.Fake, f.State.LightningRelease);
        Assert.Equal(RealityState.Fake, f.State.IceStored);
        f.At(22); f.Icon(676); f.Frame();
        Assert.Equal(ManaReleaseSafeZone.Both, f.State.SafeZone);
    }

    [Theory]
    [InlineData(0.499, false)]
    [InlineData(0.5, false)]
    [InlineData(0.501, true)]
    public void ReleaseBoundaryIsStrictlyAfterHalfASecond(double offset, bool expected)
    {
        var f = new Fixture(); f.BankBoth(678, 676);
        f.At(20 + offset); f.Icon(677); f.Icon(675); f.Frame();
        Assert.Equal(expected ? ManaReleaseSafeZone.Both : ManaReleaseSafeZone.Unknown, f.State.SafeZone);
    }

    public static IEnumerable<object[]> RealityCombinations()
    {
        foreach (var lightningBank in new uint[] { 677, 678 })
        foreach (var iceBank in new uint[] { 675, 676 })
        foreach (var lightningRelease in new uint[] { 677, 678 })
        foreach (var iceRelease in new uint[] { 675, 676 })
        {
            var l = lightningBank != lightningRelease;
            var i = iceBank != iceRelease;
            var expected = l ? i ? ManaReleaseSafeZone.Both : ManaReleaseSafeZone.Lightning :
                i ? ManaReleaseSafeZone.Ice : ManaReleaseSafeZone.None;
            yield return [lightningBank, iceBank, lightningRelease, iceRelease, expected];
        }
    }

    [Theory]
    [MemberData(nameof(RealityCombinations))]
    public void AllSixteenCombinationsUseFakeParity(uint lightningBank, uint iceBank,
        uint lightningRelease, uint iceRelease, ManaReleaseSafeZone expected)
    {
        var f = new Fixture(); f.BankBoth(lightningBank, iceBank);
        f.At(25); f.Icon(lightningRelease); f.Icon(iceRelease); f.Frame();
        Assert.Equal(expected, f.State.SafeZone);
    }

    [Theory]
    [InlineData(0x40000002u)] // Clone.
    [InlineData(0x10000001u)] // Player.
    public void IdenticalMarkerIdsOnOtherActorsAreRejected(uint actor)
    {
        var f = new Fixture(); f.BankBoth(678, 676);
        f.At(25); f.Icon(677, actor); f.Icon(675, actor); f.Frame();
        Assert.Equal(RealityState.Unknown, f.State.LightningRelease);
        Assert.Equal(ManaReleaseSafeZone.Unknown, f.State.SafeZone);
        Assert.Contains(f.Messages, message => message.Contains("Rejected non-Kefka"));
    }

    [Fact]
    public void DuplicatesAreHarmlessAndContradictionsInvalidateHistory()
    {
        var f = new Fixture(); f.BankBoth(678, 676);
        var log = new P4SessionLogTracker();
        f.Tracker.StateChanged += log.ObserveMana;
        f.At(25); f.Icon(677); f.Icon(675); f.Frame();
        Assert.Equal(ManaReleaseSafeZone.Both, log.Current(25)!.ManaRelease!.Result);
        f.At(26); f.Icon(677); f.Frame();
        Assert.Equal(ManaReleaseSafeZone.Both, f.State.SafeZone);
        Assert.Contains(f.Messages, message => message.Contains("duplicate"));
        f.At(27); f.Icon(678); f.Frame();
        Assert.Equal(RealityState.Unknown, f.State.LightningRelease);
        Assert.Equal(ManaReleaseSafeZone.Unknown, log.Current(27)!.ManaRelease!.Result);
        log.Complete("wipe", 28);
        Assert.Equal(RealityState.Unknown, log.Completed.Single().ManaRelease!.Lightning2);
        Assert.Contains(f.Messages, message => message.Contains("conflicting"));
    }

    [Fact]
    public void PrebufferSurvivesCycleStartAndUsesSixtySecondExpiry()
    {
        var f = new Fixture();
        f.At(1); f.Icon(678); f.Frame();
        Assert.Null(f.Tracker.DisplayState);
        f.At(3); f.Frame([Thunder], grandCross: true);
        Assert.Equal(RealityState.Real, f.State.LightningStored);
        Assert.Contains(f.Messages, message => message.Contains("retained 1 prebuffered"));

        var expired = new Fixture();
        expired.At(1); expired.Icon(678); expired.Frame();
        expired.At(62); expired.Frame([Thunder], grandCross: true);
        Assert.Equal(RealityState.Unknown, expired.State.LightningStored);
        Assert.Contains(expired.Messages, message => message.Contains("retained 0 prebuffered"));
    }

    [Fact]
    public void DelayedEnqueueUsesOriginalTimestampForAssociation()
    {
        var f = new Fixture(); f.Start(); f.At(19);
        var packet = f.Tracker.CaptureObservation(ManaObservationKind.TargetIcon, Kefka, 678);
        f.At(20); f.Frame([Thunder]);
        f.At(21); f.Tracker.Enqueue(packet); f.Frame();
        Assert.Equal(RealityState.Real, f.State.LightningStored);
        Assert.Contains(f.Messages, message => message.Contains("offset=-1.000s"));
    }

    [Fact]
    public void EffectCompletionDeduplicatesAndHoldsForTenSeconds()
    {
        var f = new Fixture(); f.BankBoth(678, 676);
        f.At(25); f.Icon(677); f.Icon(675); f.Frame(cast: ReleaseAction);
        f.At(30); f.Effect(); f.Frame();
        f.At(35); f.Effect(); f.Frame();
        Assert.Contains(f.Messages, message => message.Contains("Duplicate Mana Release"));
        f.At(39.999); f.Frame();
        Assert.NotNull(f.Tracker.DisplayState);
        f.At(40); f.Frame();
        Assert.Null(f.Tracker.DisplayState);
    }

    [Fact]
    public void MarkersRemainAcceptedDuringCompletionHold()
    {
        var f = new Fixture(); f.BankBoth(678, 676);
        f.At(25); f.Effect(); f.Frame();
        f.At(30); f.Icon(677); f.Icon(675); f.Frame();
        Assert.Equal(ManaReleaseSafeZone.Both, f.State.SafeZone);
        f.At(35); f.Icon(678); f.Frame(); // Exact deadline: must not invalidate.
        Assert.Null(f.Tracker.DisplayState);
        Assert.Contains(f.Messages, message => message.Contains("Late target icon"));
    }

    [Fact]
    public void WrongActorEffectCannotCompleteCycle()
    {
        var f = new Fixture(); f.BankBoth(678, 676);
        f.At(25); f.Effect(0x40000002); f.Frame();
        f.At(40); f.Frame();
        Assert.NotNull(f.Tracker.DisplayState);
        Assert.DoesNotContain(f.Messages, message => message.Contains("resolved by Kefka action effect"));
    }

    [Fact]
    public void CastDisappearanceIsFallbackAndMissingActorIsNotCompletion()
    {
        var f = new Fixture(); f.BankBoth(678, 676);
        f.At(25); f.Frame(cast: ReleaseAction);
        f.At(26); f.Tracker.Update(null, false);
        Assert.DoesNotContain(f.Messages, message => message.Contains("completion fallback"));
        f.At(27); f.Frame();
        Assert.Contains(f.Messages, message => message.Contains("completion fallback"));
        f.At(37); f.Frame();
        Assert.Null(f.Tracker.DisplayState);
    }

    [Fact]
    public void DelayedEffectReplacesCastFallbackDeadline()
    {
        var f = new Fixture(); f.BankBoth(678, 676);
        f.At(25); f.Frame(cast: ReleaseAction);
        f.At(26); f.Frame();
        f.At(27); f.Effect(); f.Frame();
        f.At(36); f.Frame();
        Assert.NotNull(f.Tracker.DisplayState);
        f.At(37); f.Frame();
        Assert.Null(f.Tracker.DisplayState);
    }

    [Fact]
    public void AbandonedCycleExpiresAtTwoHundredSeconds()
    {
        var f = new Fixture(); f.Start();
        f.At(199.999); f.Frame();
        Assert.NotNull(f.Tracker.DisplayState);
        f.At(200); f.Frame();
        Assert.Null(f.Tracker.DisplayState);
        Assert.Contains(f.Messages, message => message.Contains("200 seconds"));
    }

    [Theory]
    [InlineData("Wipe/reset detected")]
    [InlineData("Territory departure")]
    [InlineData("Logged out")]
    [InlineData("Feature unavailable")]
    [InlineData("Plugin disposed")]
    public void ResetClearsPrebufferAndRejectsInflightPreviousGeneration(string reason)
    {
        var f = new Fixture(); f.At(1); f.Icon(678); f.Frame();
        var inFlight = f.Tracker.CaptureObservation(ManaObservationKind.TargetIcon, Kefka, 678);
        f.Tracker.Reset(reason);
        f.Tracker.Enqueue(inFlight);
        f.At(2); f.Frame([Thunder]);
        Assert.Equal(RealityState.Unknown, f.State.LightningStored);
        Assert.Contains(f.Messages, message => message.Contains("Rejected old-generation"));
        f.Icon(677); f.Frame();
        Assert.Equal(RealityState.Fake, f.State.LightningStored);
    }

    [Fact]
    public void ResetClearsBanksConflictsStatusEdgesAndCompletionDeadline()
    {
        var f = new Fixture(); f.BankBoth(678, 676);
        f.At(25); f.Icon(677); f.Icon(678); f.Icon(675); f.Effect(); f.Frame();
        Assert.Equal(ManaReleaseSafeZone.Unknown, f.State.SafeZone);
        f.Tracker.Reset("wipe");
        f.At(26); f.Icon(677); f.Icon(675); f.Frame([Thunder, Ice]);
        Assert.Equal(RealityState.Fake, f.State.LightningStored);
        Assert.Equal(RealityState.Fake, f.State.IceStored);
        f.At(37); f.Icon(678); f.Icon(676); f.Frame();
        Assert.Equal(ManaReleaseSafeZone.Both, f.State.SafeZone);
    }

    [Fact]
    public void UnknownIconsIncludeRawFieldsWithoutChangingValues()
    {
        var f = new Fixture(); f.Start();
        f.Icon(999, arg2: 42); f.Frame();
        Assert.Equal(ManaReleaseSafeZone.Unknown, f.State.SafeZone);
        Assert.Contains(f.Messages, message => message.Contains("Unknown Kefka") &&
            message.Contains("999 (0x3E7)") && message.Contains("arg2=42") && message.Contains("generation="));
    }

    [Fact]
    public void SessionHistoryCanClearAnEntirelyUnknownActiveSnapshot()
    {
        var log = new P4SessionLogTracker();
        log.ObserveMana(new(true, RealityState.Real, RealityState.Real, RealityState.Fake, RealityState.Fake, true, ManaReleaseSafeZone.Both));
        log.ObserveMana(new(true, RealityState.Unknown, RealityState.Unknown, RealityState.Unknown, RealityState.Unknown, true, ManaReleaseSafeZone.Unknown));
        Assert.Equal(ManaReleaseSafeZone.Unknown, log.Current(0)!.ManaRelease!.Result);
    }

    private sealed class Fixture
    {
        public readonly ManualClock Clock = new();
        public readonly List<string> Messages = [];
        public readonly ManaReleaseStateMachine Tracker;
        private uint[] statuses = [];

        public Fixture() => Tracker = new(Messages.Add, Clock);
        public ManaReleaseDisplayState State => Assert.IsType<ManaReleaseDisplayState>(Tracker.DisplayState);
        public void At(double seconds) => Clock.Timestamp = TimeSpan.FromSeconds(seconds).Ticks;
        public void Start() => Frame(grandCross: true);
        public void Frame(uint[]? statusIds = null, uint cast = 0, bool grandCross = false)
        {
            if (statusIds is not null) statuses = statusIds;
            Tracker.Update(new(Kefka, cast, statuses), grandCross);
        }
        public void Icon(uint id, uint actor = Kefka, uint arg2 = 0) =>
            Tracker.Enqueue(Tracker.CaptureObservation(ManaObservationKind.TargetIcon, actor, id, arg2));
        public void Effect(uint actor = Kefka) =>
            Tracker.Enqueue(Tracker.CaptureObservation(ManaObservationKind.ActionEffect, actor, ReleaseAction));
        public void BankBoth(uint lightning, uint ice)
        {
            Start();
            At(1); Icon(lightning); Frame([Thunder]);
            At(20); Icon(ice); Frame([Thunder, Ice]);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero).AddTicks(Timestamp);
    }
}
