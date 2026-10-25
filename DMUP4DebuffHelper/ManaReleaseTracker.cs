using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DMUP4DebuffHelper;

/// <summary>Opt-in, defensive tracker for Kefka's stored Mana Release mechanics.</summary>
internal sealed unsafe class ManaReleaseTracker : IDisposable
{
    private const uint KefkaDataId = 18475;
    private const uint ManaChargeActionId = 47780;
    private const uint ManaReleaseActionId = 47781;
    private const uint LightningRealActionId = 47775;
    private const uint LightningFakeFirstActionId = 47776;
    private const uint LightningFakeSecondActionId = 47777;
    private const uint IceRealActionId = 47768;
    private const uint IceFakeFirstActionId = 47771;
    private const uint IceFakeSecondActionId = 47774;
    private const uint TargetIconCategory = 34;
    private const uint IceFakeTargetIconId = 675;
    private const uint IceRealTargetIconId = 676;
    private const uint LightningFakeTargetIconId = 677;
    private const uint LightningRealTargetIconId = 678;

    private readonly Plugin plugin;
    private readonly ConcurrentQueue<TargetIconObservation> targetIconObservations = new();
    private readonly ConcurrentQueue<string> deferredErrors = new();
    private readonly List<ManaReleaseDiagnosticEntry> diagnostics = [];
    private readonly HashSet<string> activeRelevantCasts = new(StringComparer.Ordinal);
    private Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? actorControlHook;
    private bool isEnabled;
    private bool runtimeDisabled;
    private bool warnedRuntimeFailure;
    private bool warnedUpdateFailure;
    private bool armed;
    private RealityState lightningStored;
    private RealityState iceStored;
    private bool releaseActive;
    private RealityState lightningRelease;
    private RealityState iceRelease;
    private bool lightningReleaseConflicted;
    private bool iceReleaseConflicted;
    private bool releaseResolved;
    private bool manaChargeObserved;
    private ManaReleaseSafeZone safeZone;

    public ManaReleaseTracker(Plugin plugin) => this.plugin = plugin;

    public IReadOnlyList<ManaReleaseDiagnosticEntry> Diagnostics
    {
        get
        {
            lock (diagnostics)
            {
                return diagnostics.ToList();
            }
        }
    }

    public ManaReleaseDisplayState? DisplayState => armed || releaseActive
        ? new ManaReleaseDisplayState(
            armed,
            lightningStored,
            iceStored,
            lightningRelease,
            iceRelease,
            releaseActive,
            safeZone)
        : null;

    public void Reconcile(bool shouldRun)
    {
        if (!shouldRun)
        {
            if (isEnabled)
            {
                Disable("Feature unavailable");
            }
            return;
        }

        isEnabled = true;
        if (actorControlHook is not null || runtimeDisabled)
        {
            return;
        }

        try
        {
            actorControlHook = Plugin.GameInteropProvider.HookFromAddress<PacketDispatcher.Delegates.HandleActorControlPacket>(
                PacketDispatcher.MemberFunctionPointers.HandleActorControlPacket,
                OnHandleActorControlPacket);
            actorControlHook.Enable();
            Record("Target-icon hook initialized.");
        }
        catch (Exception ex)
        {
            runtimeDisabled = true;
            FailRuntime("Mana Release tracker could not initialize; calls will remain Unknown until it is re-enabled.", ex);
        }
    }

    public void Update()
    {
        // Plugin only invokes this while the feature, P4 tracking, and DMU are all active.
        try
        {
            if (runtimeDisabled)
            {
                return;
            }

            var casters = CaptureCastingActors(out var kefka);
            var actionIds = casters.Select(caster => caster.ActionId).ToList();
            var manaReleaseCasting = kefka is not null && kefka.Value.ActionId == ManaReleaseActionId;
            RecordRelevantCasts(casters);

            var manaChargeCasting = actionIds.Contains(ManaChargeActionId);
            if (manaChargeCasting && !manaChargeObserved)
            {
                Arm();
            }
            manaChargeObserved = manaChargeCasting;

            if (armed)
            {
                CaptureStoredReality(actionIds);
                PublishLogState();
                if (manaReleaseCasting && !releaseActive && kefka is { } releaseKefka)
                {
                    BeginRelease(releaseKefka);
                }
            }

            // Detect the cast before consuming observations so icons received on the
            // cast-start frame are attributed to this Mana Release.
            DrainObservations(releaseActive && manaReleaseCasting);
            if (!armed)
            {
                return;
            }

            if (releaseActive)
            {
                if (HasCompleteRealityData() && !releaseResolved)
                {
                    ResolveRelease();
                }

                if (!manaReleaseCasting)
                {
                    if (!releaseResolved)
                    {
                        safeZone = ManaReleaseSafeZone.Unknown;
                        Record($"Mana Release resolved as Unknown: {DescribeIncompleteRealityData()}.");
                        PublishLogState();
                    }

                    Reset("Mana Release ended");
                }
            }
        }
        catch (Exception ex)
        {
            Reset("Mana Release update failed");
            Record($"Mana Release update error: {ex.GetType().Name}. State cleared; tracking will retry.");
            if (!warnedUpdateFailure)
            {
                warnedUpdateFailure = true;
                Plugin.Log.Warning(ex, "Mana Release tracker update failed; state cleared and tracking will retry.");
            }
        }
    }

    public void Reset(string reason)
    {
        armed = false;
        lightningStored = RealityState.Unknown;
        iceStored = RealityState.Unknown;
        releaseActive = false;
        lightningRelease = RealityState.Unknown;
        iceRelease = RealityState.Unknown;
        lightningReleaseConflicted = false;
        iceReleaseConflicted = false;
        releaseResolved = false;
        manaChargeObserved = false;
        safeZone = ManaReleaseSafeZone.Unknown;
        while (targetIconObservations.TryDequeue(out _)) { }
        while (deferredErrors.TryDequeue(out _)) { }
        activeRelevantCasts.Clear();
        Record($"Reset: {reason}.");
    }

    public void ClearDiagnostics()
    {
        lock (diagnostics)
        {
            diagnostics.Clear();
        }
    }

    public void Dispose() => Disable("Plugin disposed");

    private void Disable(string reason)
    {
        try
        {
            actorControlHook?.Dispose();
        }
        catch (Exception ex)
        {
            Record($"Error: Mana Release target-icon hook disposal failed ({ex.GetType().Name}).");
            Plugin.Log.Warning(ex, "Mana Release target-icon hook disposal failed.");
        }
        finally
        {
            actorControlHook = null;
            isEnabled = false;
            runtimeDisabled = false;
            warnedRuntimeFailure = false;
            warnedUpdateFailure = false;
            Reset(reason);
        }
    }

    private void Arm()
    {
        Reset("Mana Charge");
        armed = true;
        Record("Mana Charge (47780) detected; tracker armed.");
    }

    private void CaptureStoredReality(IReadOnlyCollection<uint> actions)
    {
        if (lightningStored == RealityState.Unknown)
        {
            if (actions.Count(id => id == LightningRealActionId) >= 2)
            {
                lightningStored = RealityState.Real;
                Record("Lightning 1 captured: Real (47775 x2).");
            }
            else if (actions.Count(id => id == LightningFakeFirstActionId) >= 2 && actions.Count(id => id == LightningFakeSecondActionId) >= 2)
            {
                lightningStored = RealityState.Fake;
                Record("Lightning 1 captured: Fake (47776/47777 x2).");
            }
        }

        if (iceStored == RealityState.Unknown)
        {
            if (actions.Count(id => id == IceRealActionId) >= 2)
            {
                iceStored = RealityState.Real;
                Record("Ice 1 captured: Real (47768 x2).");
            }
            else if (actions.Count(id => id == IceFakeFirstActionId) >= 2 && actions.Count(id => id == IceFakeSecondActionId) >= 2)
            {
                iceStored = RealityState.Fake;
                Record("Ice 1 captured: Fake (47771/47774 x2).");
            }
        }
    }

    private List<CastingActor> CaptureCastingActors(out CastingActor? kefka)
    {
        kefka = null;
        var casters = new List<CastingActor>();
        foreach (var gameObject in Plugin.ObjectTable)
        {
            try
            {
                if (gameObject is not IBattleChara actor || !actor.IsCasting)
                {
                    continue;
                }

                var snapshot = new CastingActor(actor.EntityId, actor.BaseId, actor.CastActionId);
                casters.Add(snapshot);
                if (snapshot.BaseId == KefkaDataId)
                {
                    kefka = snapshot;
                }
            }
            catch (Exception)
            {
                // Ignore
            }
        }

        return casters;
    }

    private void RecordRelevantCasts(IReadOnlyList<CastingActor> casters)
    {
        if (!plugin.Configuration.EnableManaReleaseDiagnostics)
        {
            return;
        }

        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var caster in casters)
        {
            if (caster.ActionId is not (
                ManaChargeActionId or ManaReleaseActionId or
                LightningRealActionId or LightningFakeFirstActionId or LightningFakeSecondActionId or
                IceRealActionId or IceFakeFirstActionId or IceFakeSecondActionId))
            {
                continue;
            }

            var key = $"{caster.EntityId:X8}:{caster.ActionId}";
            current.Add(key);
            if (!activeRelevantCasts.Contains(key))
            {
                Record($"Relevant cast {caster.ActionId} from actor {caster.EntityId:X8}.");
            }
        }

        activeRelevantCasts.Clear();
        activeRelevantCasts.UnionWith(current);
    }

    private void BeginRelease(CastingActor kefka)
    {
        releaseActive = true;
        lightningRelease = RealityState.Unknown;
        iceRelease = RealityState.Unknown;
        lightningReleaseConflicted = false;
        iceReleaseConflicted = false;
        releaseResolved = false;
        safeZone = ManaReleaseSafeZone.Unknown;
        Record($"Mana Release (47781) detected from actor {kefka.EntityId:X8}.");
        PublishLogState();
    }

    private void DrainObservations(bool acceptTargetIcons)
    {
        while (deferredErrors.TryDequeue(out var error))
        {
            Record(error);
        }

        var changed = false;
        while (targetIconObservations.TryDequeue(out var observation))
        {
            if (!acceptTargetIcons)
            {
                continue;
            }

            changed |= observation.IconId switch
            {
                IceFakeTargetIconId => CaptureReleaseReality(ref iceRelease, ref iceReleaseConflicted, RealityState.Fake, "Ice 2", observation),
                IceRealTargetIconId => CaptureReleaseReality(ref iceRelease, ref iceReleaseConflicted, RealityState.Real, "Ice 2", observation),
                LightningFakeTargetIconId => CaptureReleaseReality(ref lightningRelease, ref lightningReleaseConflicted, RealityState.Fake, "Lightning 2", observation),
                LightningRealTargetIconId => CaptureReleaseReality(ref lightningRelease, ref lightningReleaseConflicted, RealityState.Real, "Lightning 2", observation),
                _ => false,
            };
        }

        if (changed)
        {
            PublishLogState();
        }
    }

    private bool CaptureReleaseReality(
        ref RealityState current,
        ref bool conflicted,
        RealityState incoming,
        string label,
        TargetIconObservation observation)
    {
        if (conflicted || current == incoming)
        {
            return false;
        }

        if (current == RealityState.Unknown)
        {
            current = incoming;
            Record($"{label} captured: {incoming} (target icon {observation.IconId} on actor {observation.TargetEntityId:X8}).");
            return true;
        }

        var previous = current;
        current = RealityState.Unknown;
        conflicted = true;
        releaseResolved = false;
        safeZone = ManaReleaseSafeZone.Unknown;
        Record($"{label} target-icon conflict: observed both {previous} and {incoming}; result will remain Unknown.");
        return true;
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

    private void ResolveRelease()
    {
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
        Record(
            $"Mana Release resolved: Lightning 1={lightningStored}, Lightning 2={lightningRelease}, " +
            $"Ice 1={iceStored}, Ice 2={iceRelease}, safe={safeZone}.");
        PublishLogState();
    }

    private void PublishLogState()
    {
        if (DisplayState is { } state)
        {
            plugin.ObserveManaReleaseForLog(state);
        }
    }

    private void OnHandleActorControlPacket(
        uint entityId,
        uint category,
        uint arg1,
        uint arg2,
        uint arg3,
        uint arg4,
        uint arg5,
        uint arg6,
        uint arg7,
        uint arg8,
        GameObjectId targetId,
        bool isRecorded)
    {
        // Keep the hook instance alive across the detour so teardown cannot cause a second
        // lookup or skip the original invocation.
        var hook = actorControlHook;
        try
        {
            if (category == TargetIconCategory && IsManaReleaseTargetIcon(arg1))
            {
                targetIconObservations.Enqueue(new TargetIconObservation(entityId, arg1));
            }
        }
        catch (Exception ex)
        {
            deferredErrors.Enqueue($"Target-icon observation error: {ex.GetType().Name}.");
        }

        // This function must always call the original exactly once.
        hook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded);
    }

    private static bool IsManaReleaseTargetIcon(uint iconId) => iconId is
        IceFakeTargetIconId or IceRealTargetIconId or LightningFakeTargetIconId or LightningRealTargetIconId;

    private void FailRuntime(string message, Exception ex)
    {
        runtimeDisabled = true;
        try
        {
            actorControlHook?.Dispose();
        }
        catch
        {
            // A failed hook teardown must never escape into Dalamud.
        }

        actorControlHook = null;
        Reset("Runtime failure");
        Record($"Error: {message} ({ex.GetType().Name}).");
        if (warnedRuntimeFailure)
        {
            return;
        }

        warnedRuntimeFailure = true;
        Plugin.Log.Warning(ex, message);
    }

    private void Record(string message)
    {
        if (!plugin.Configuration.EnableManaReleaseDiagnostics)
        {
            return;
        }

        lock (diagnostics)
        {
            diagnostics.Add(new ManaReleaseDiagnosticEntry(DateTime.UtcNow, message));
            if (diagnostics.Count > 500)
            {
                diagnostics.RemoveRange(0, diagnostics.Count - 500);
            }
        }
    }

    private readonly record struct CastingActor(uint EntityId, uint BaseId, uint ActionId);

    private readonly record struct TargetIconObservation(uint TargetEntityId, uint IconId);
}
