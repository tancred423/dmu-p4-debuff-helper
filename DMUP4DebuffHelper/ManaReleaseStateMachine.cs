using System;
using System.Collections.Generic;
using System.Linq;

namespace DMUP4DebuffHelper;

/// <summary>
/// Owns Mana Release's timing-sensitive state. Target icons are deliberately the only
/// source for the second Lightning and Ice realities.
/// </summary>
internal sealed class ManaReleaseStateMachine
{
    internal const uint ManaChargeActionId = 47780;
    internal const uint ManaReleaseActionId = 47781;
    internal const uint LightningRealActionId = 47775;
    internal const uint LightningFakeFirstActionId = 47776;
    internal const uint LightningFakeSecondActionId = 47777;
    internal const uint IceRealActionId = 47768;
    internal const uint IceFakeFirstActionId = 47771;
    internal const uint IceFakeSecondActionId = 47774;
    internal const uint IceFakeTargetIconId = 675;
    internal const uint IceRealTargetIconId = 676;
    internal const uint LightningFakeTargetIconId = 677;
    internal const uint LightningRealTargetIconId = 678;

    private static readonly TimeSpan PostCastIconWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostCastDisplayHold = TimeSpan.FromSeconds(5);

    private readonly TimeProvider timeProvider;
    private readonly Action<string> record;
    private bool armed;
    private RealityState lightningStored;
    private RealityState iceStored;
    private ManaReleasePhase phase;
    private RealityState lightningRelease;
    private RealityState iceRelease;
    private bool lightningReleaseConflicted;
    private bool iceReleaseConflicted;
    private bool releaseResolved;
    private DateTimeOffset iconWindowEndsAtUtc;
    private DateTimeOffset displayEndsAtUtc;
    private ManaReleaseSafeZone safeZone;

    public ManaReleaseStateMachine(Action<string> record, TimeProvider? timeProvider = null)
    {
        this.record = record;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsArmed => armed;

    public bool IsReleaseActive => phase != ManaReleasePhase.Inactive;

    public bool IsAcceptingTargetIcons => phase is ManaReleasePhase.Casting or ManaReleasePhase.PostCastIconWindow;

    public ManaReleaseDisplayState? DisplayState => armed || IsReleaseActive
        ? new ManaReleaseDisplayState(
            armed,
            lightningStored,
            iceStored,
            lightningRelease,
            iceRelease,
            IsReleaseActive,
            safeZone)
        : null;

    public void Arm()
    {
        ClearState();
        armed = true;
        record("Mana Charge (47780) detected; tracker armed.");
    }

    public void CaptureStoredReality(IReadOnlyCollection<uint> actions)
    {
        // Lightning 1 and Ice 1 are immutable once Mana Release starts. The same
        // actions can appear in the released wave and must never be treated as stored data.
        if (!armed || phase != ManaReleasePhase.Inactive)
        {
            return;
        }

        if (lightningStored == RealityState.Unknown)
        {
            if (actions.Count(id => id == LightningRealActionId) >= 2)
            {
                lightningStored = RealityState.Real;
                record("Lightning 1 captured: Real (47775 x2).");
            }
            else if (actions.Count(id => id == LightningFakeFirstActionId) >= 2 && actions.Count(id => id == LightningFakeSecondActionId) >= 2)
            {
                lightningStored = RealityState.Fake;
                record("Lightning 1 captured: Fake (47776/47777 x2).");
            }
        }

        if (iceStored == RealityState.Unknown)
        {
            if (actions.Count(id => id == IceRealActionId) >= 2)
            {
                iceStored = RealityState.Real;
                record("Ice 1 captured: Real (47768 x2).");
            }
            else if (actions.Count(id => id == IceFakeFirstActionId) >= 2 && actions.Count(id => id == IceFakeSecondActionId) >= 2)
            {
                iceStored = RealityState.Fake;
                record("Ice 1 captured: Fake (47771/47774 x2).");
            }
        }
    }

    public void BeginRelease(uint kefkaEntityId)
    {
        if (!armed || phase != ManaReleasePhase.Inactive)
        {
            return;
        }

        phase = ManaReleasePhase.Casting;
        lightningRelease = RealityState.Unknown;
        iceRelease = RealityState.Unknown;
        lightningReleaseConflicted = false;
        iceReleaseConflicted = false;
        releaseResolved = false;
        safeZone = ManaReleaseSafeZone.Unknown;
        record($"Mana Release (47781) detected from actor {kefkaEntityId:X8}.");
    }

    /// <summary>Advances release deadlines and returns true only when the display hold is complete.</summary>
    public bool Advance(bool manaReleaseCasting)
    {
        if (phase == ManaReleasePhase.Inactive)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (phase == ManaReleasePhase.Casting && !manaReleaseCasting)
        {
            phase = ManaReleasePhase.PostCastIconWindow;
            iconWindowEndsAtUtc = now.Add(PostCastIconWindow);
            displayEndsAtUtc = now.Add(PostCastDisplayHold);
            record("Mana Release cast ended; accepting target icons for 2 seconds and holding the callout for 5 seconds.");
        }

        if (phase == ManaReleasePhase.PostCastIconWindow && now >= iconWindowEndsAtUtc)
        {
            phase = ManaReleasePhase.DisplayHold;
            if (!releaseResolved)
            {
                safeZone = ManaReleaseSafeZone.Unknown;
                record($"Mana Release resolved as Unknown: {DescribeIncompleteRealityData()}.");
            }
        }

        return phase == ManaReleasePhase.DisplayHold && now >= displayEndsAtUtc;
    }

    public bool ObserveTargetIcon(uint iconId, uint targetEntityId)
    {
        var phaseName = phase.ToString();
        if (!IsAcceptingTargetIcons)
        {
            record($"Target icon {FormatIcon(iconId)} on actor {targetEntityId:X8} ignored as late (phase {phaseName}).");
            return false;
        }

        if (!IsManaReleaseTargetIcon(iconId))
        {
            record($"Target icon {FormatIcon(iconId)} on actor {targetEntityId:X8} observed as unknown during phase {phaseName}.");
            return false;
        }

        return iconId switch
        {
            IceFakeTargetIconId => CaptureReleaseReality(ref iceRelease, ref iceReleaseConflicted, RealityState.Fake, "Ice 2", iconId, targetEntityId),
            IceRealTargetIconId => CaptureReleaseReality(ref iceRelease, ref iceReleaseConflicted, RealityState.Real, "Ice 2", iconId, targetEntityId),
            LightningFakeTargetIconId => CaptureReleaseReality(ref lightningRelease, ref lightningReleaseConflicted, RealityState.Fake, "Lightning 2", iconId, targetEntityId),
            LightningRealTargetIconId => CaptureReleaseReality(ref lightningRelease, ref lightningReleaseConflicted, RealityState.Real, "Lightning 2", iconId, targetEntityId),
            _ => false,
        };
    }

    public void Reset()
    {
        ClearState();
    }

    private bool CaptureReleaseReality(
        ref RealityState current,
        ref bool conflicted,
        RealityState incoming,
        string label,
        uint iconId,
        uint targetEntityId)
    {
        if (conflicted)
        {
            record($"{label} target icon {FormatIcon(iconId)} on actor {targetEntityId:X8} ignored after an earlier conflict (phase {phase}).");
            return false;
        }

        if (current == incoming)
        {
            record($"{label} target icon {FormatIcon(iconId)} on actor {targetEntityId:X8} duplicated (phase {phase}).");
            return false;
        }

        if (current == RealityState.Unknown)
        {
            current = incoming;
            record($"{label} captured: {incoming} (target icon {FormatIcon(iconId)} on actor {targetEntityId:X8}, phase {phase}).");
            ResolveIfComplete();
            return true;
        }

        var previous = current;
        current = RealityState.Unknown;
        conflicted = true;
        releaseResolved = false;
        safeZone = ManaReleaseSafeZone.Unknown;
        record($"{label} target-icon conflict in phase {phase}: observed both {previous} and {incoming}; result will remain Unknown.");
        return true;
    }

    private void ResolveIfComplete()
    {
        if (!HasCompleteRealityData() || releaseResolved)
        {
            return;
        }

        releaseResolved = true;
        var lightningSafe = lightningStored != lightningRelease;
        var iceSafe = iceStored != iceRelease;
        safeZone = (lightningSafe, iceSafe) switch
        {
            (true, false) => ManaReleaseSafeZone.Lightning,
            (false, true) => ManaReleaseSafeZone.Ice,
            (true, true) => ManaReleaseSafeZone.Both,
            _ => ManaReleaseSafeZone.None,
        };
        record(
            $"Mana Release resolved: Lightning 1={lightningStored}, Lightning 2={lightningRelease}, " +
            $"Ice 1={iceStored}, Ice 2={iceRelease}, safe={safeZone}.");
    }

    private bool HasCompleteRealityData() =>
        lightningStored != RealityState.Unknown && iceStored != RealityState.Unknown &&
        !lightningReleaseConflicted && !iceReleaseConflicted &&
        lightningRelease != RealityState.Unknown && iceRelease != RealityState.Unknown;

    private string DescribeIncompleteRealityData()
    {
        var issues = new List<string>();
        if (lightningStored == RealityState.Unknown)
        {
            issues.Add("Lightning 1 cast data missing");
        }
        if (iceStored == RealityState.Unknown)
        {
            issues.Add("Ice 1 cast data missing");
        }
        if (lightningReleaseConflicted)
        {
            issues.Add("Lightning 2 target icons conflicted");
        }
        else if (lightningRelease == RealityState.Unknown)
        {
            issues.Add("Lightning 2 target icon missing");
        }
        if (iceReleaseConflicted)
        {
            issues.Add("Ice 2 target icons conflicted");
        }
        else if (iceRelease == RealityState.Unknown)
        {
            issues.Add("Ice 2 target icon missing");
        }

        return issues.Count > 0 ? string.Join("; ", issues) : "release data incomplete";
    }

    private void ClearState()
    {
        armed = false;
        lightningStored = RealityState.Unknown;
        iceStored = RealityState.Unknown;
        phase = ManaReleasePhase.Inactive;
        lightningRelease = RealityState.Unknown;
        iceRelease = RealityState.Unknown;
        lightningReleaseConflicted = false;
        iceReleaseConflicted = false;
        releaseResolved = false;
        iconWindowEndsAtUtc = default;
        displayEndsAtUtc = default;
        safeZone = ManaReleaseSafeZone.Unknown;
    }

    private static bool IsManaReleaseTargetIcon(uint iconId) => iconId is
        IceFakeTargetIconId or IceRealTargetIconId or LightningFakeTargetIconId or LightningRealTargetIconId;

    private static string FormatIcon(uint iconId) => $"{iconId} (0x{iconId:X})";

    private enum ManaReleasePhase
    {
        Inactive,
        Casting,
        PostCastIconWindow,
        DisplayHold,
    }
}
