using System;
using System.Collections.Generic;
using Xunit;

namespace DMUP4DebuffHelper;

public sealed class ManaReleaseStateMachineTests
{
    [Fact]
    public void LateIconsFromTheFirstLoggedPullResolveToNone()
    {
        var fixture = new TrackerFixture();
        fixture.BeginRelease();

        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        Assert.False(fixture.Tracker.Advance(manaReleaseCasting: false));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(82));
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.LightningFakeTargetIconId, 0x400056E1);
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.IceRealTargetIconId, 0x400056E4);

        var state = Assert.IsType<ManaReleaseDisplayState>(fixture.Tracker.DisplayState);
        Assert.Equal(RealityState.Fake, state.LightningStored);
        Assert.Equal(RealityState.Real, state.IceStored);
        Assert.Equal(RealityState.Fake, state.LightningRelease);
        Assert.Equal(RealityState.Real, state.IceRelease);
        Assert.Equal(ManaReleaseSafeZone.None, state.SafeZone);
        Assert.True(state.IsManaReleaseActive);
    }

    [Fact]
    public void LateIconsFromTheSecondLoggedPullResolveToLightning()
    {
        var fixture = new TrackerFixture();
        fixture.BeginRelease();

        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        fixture.Tracker.Advance(manaReleaseCasting: false);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(84));
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.LightningRealTargetIconId, 0x40006A97);
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.IceRealTargetIconId, 0x40006A99);

        var state = Assert.IsType<ManaReleaseDisplayState>(fixture.Tracker.DisplayState);
        Assert.Equal(ManaReleaseSafeZone.Lightning, state.SafeZone);
    }

    [Fact]
    public void IconsDuringManaReleaseResolveImmediatelyAndSurviveTheDisplayHold()
    {
        var fixture = new TrackerFixture();
        fixture.BeginRelease();

        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.LightningFakeTargetIconId, 0x40000001);
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.IceRealTargetIconId, 0x40000002);
        Assert.Equal(ManaReleaseSafeZone.None, Assert.IsType<ManaReleaseDisplayState>(fixture.Tracker.DisplayState).SafeZone);

        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        Assert.False(fixture.Tracker.Advance(manaReleaseCasting: false));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Tracker.Advance(manaReleaseCasting: false));
        Assert.True(Assert.IsType<ManaReleaseDisplayState>(fixture.Tracker.DisplayState).IsManaReleaseActive);
        fixture.Tracker.Reset(); // The adapter performs this reset after receiving the true result.
        Assert.Null(fixture.Tracker.DisplayState);
    }

    [Fact]
    public void ConflictingIconsInvalidateTheResolvedElement()
    {
        var fixture = new TrackerFixture();
        fixture.BeginRelease();

        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.LightningFakeTargetIconId, 0x40000001);
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.LightningRealTargetIconId, 0x40000001);
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.IceRealTargetIconId, 0x40000002);

        var state = Assert.IsType<ManaReleaseDisplayState>(fixture.Tracker.DisplayState);
        Assert.Equal(RealityState.Unknown, state.LightningRelease);
        Assert.Equal(ManaReleaseSafeZone.Unknown, state.SafeZone);
        Assert.Contains(fixture.Messages, message => message.Contains("target-icon conflict", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingIconFinalizesAsUnknownAfterThePostCastWindow()
    {
        var fixture = new TrackerFixture();
        fixture.BeginRelease();
        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        fixture.Tracker.Advance(manaReleaseCasting: false);
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.LightningFakeTargetIconId, 0x40000001);

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(fixture.Tracker.Advance(manaReleaseCasting: false));

        var state = Assert.IsType<ManaReleaseDisplayState>(fixture.Tracker.DisplayState);
        Assert.Equal(ManaReleaseSafeZone.Unknown, state.SafeZone);
        Assert.Contains(fixture.Messages, message => message.Contains("Ice 2 target icon missing", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownAndLateIconsAreDiagnosedAndIgnored()
    {
        var fixture = new TrackerFixture();
        fixture.BeginRelease();
        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        fixture.Tracker.Advance(manaReleaseCasting: false);

        fixture.Tracker.ObserveTargetIcon(999, 0x40000001);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Tracker.Advance(manaReleaseCasting: false);
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.IceFakeTargetIconId, 0x40000002);

        Assert.Contains(fixture.Messages, message => message.Contains("999 (0x3E7)", StringComparison.Ordinal) && message.Contains("unknown", StringComparison.Ordinal));
        Assert.Contains(fixture.Messages, message => message.Contains("ignored as late", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseWaveActionsCannotOverwriteFrozenStoredReality()
    {
        var fixture = new TrackerFixture();
        fixture.BeginRelease();

        fixture.Tracker.CaptureStoredReality([
            ManaReleaseStateMachine.LightningRealActionId,
            ManaReleaseStateMachine.LightningRealActionId,
            ManaReleaseStateMachine.IceFakeFirstActionId,
            ManaReleaseStateMachine.IceFakeFirstActionId,
            ManaReleaseStateMachine.IceFakeSecondActionId,
            ManaReleaseStateMachine.IceFakeSecondActionId,
        ]);

        var state = Assert.IsType<ManaReleaseDisplayState>(fixture.Tracker.DisplayState);
        Assert.Equal(RealityState.Fake, state.LightningStored);
        Assert.Equal(RealityState.Real, state.IceStored);
    }

    [Fact]
    public void ResetClearsReleaseValuesAndDeadlines()
    {
        var fixture = new TrackerFixture();
        fixture.BeginRelease();
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.LightningFakeTargetIconId, 0x40000001);
        fixture.Tracker.ObserveTargetIcon(ManaReleaseStateMachine.IceRealTargetIconId, 0x40000002);

        fixture.Tracker.Reset();

        Assert.Null(fixture.Tracker.DisplayState);
        Assert.False(fixture.Tracker.Advance(manaReleaseCasting: false));
        fixture.Tracker.Arm();
        var state = Assert.IsType<ManaReleaseDisplayState>(fixture.Tracker.DisplayState);
        Assert.Equal(RealityState.Unknown, state.LightningStored);
        Assert.Equal(RealityState.Unknown, state.LightningRelease);
        Assert.Equal(ManaReleaseSafeZone.Unknown, state.SafeZone);
    }

    private sealed class TrackerFixture
    {
        public readonly List<string> Messages = [];
        public readonly TestTimeProvider Clock = new(new DateTimeOffset(2026, 10, 27, 19, 20, 8, TimeSpan.Zero));
        public readonly ManaReleaseStateMachine Tracker;

        public TrackerFixture()
        {
            Tracker = new ManaReleaseStateMachine(Messages.Add, Clock);
            Tracker.Arm();
            Tracker.CaptureStoredReality([
                ManaReleaseStateMachine.LightningFakeFirstActionId,
                ManaReleaseStateMachine.LightningFakeFirstActionId,
                ManaReleaseStateMachine.LightningFakeSecondActionId,
                ManaReleaseStateMachine.LightningFakeSecondActionId,
                ManaReleaseStateMachine.IceRealActionId,
                ManaReleaseStateMachine.IceRealActionId,
            ]);
        }

        public void BeginRelease()
        {
            Tracker.BeginRelease(0x4000573A);
            Tracker.Advance(manaReleaseCasting: true);
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset currentTime = initialTime;

        public override DateTimeOffset GetUtcNow() => currentTime;

        public void Advance(TimeSpan duration) => currentTime = currentTime.Add(duration);
    }
}
