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
    private const uint TargetIconCategory = 34;

    private readonly Plugin plugin;
    private readonly ManaReleaseStateMachine state;
    private readonly ConcurrentQueue<TargetIconObservation> targetIconObservations = new();
    private readonly ConcurrentQueue<string> deferredErrors = new();
    private readonly List<ManaReleaseDiagnosticEntry> diagnostics = [];
    private readonly HashSet<string> activeRelevantCasts = new(StringComparer.Ordinal);
    private Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? actorControlHook;
    private bool isEnabled;
    private bool runtimeDisabled;
    private bool warnedRuntimeFailure;
    private bool warnedUpdateFailure;
    private bool manaChargeObserved;

    public ManaReleaseTracker(Plugin plugin) : this(plugin, TimeProvider.System)
    {
    }

    internal ManaReleaseTracker(Plugin plugin, TimeProvider timeProvider)
    {
        this.plugin = plugin;
        state = new ManaReleaseStateMachine(Record, timeProvider);
    }

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

    public ManaReleaseDisplayState? DisplayState => state.DisplayState;

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
            var manaReleaseCasting = kefka is not null && kefka.Value.ActionId == ManaReleaseStateMachine.ManaReleaseActionId;
            RecordRelevantCasts(casters);

            var manaChargeCasting = actionIds.Contains(ManaReleaseStateMachine.ManaChargeActionId);
            if (manaChargeCasting && !manaChargeObserved)
            {
                Reset("Mana Charge");
                state.Arm();
            }
            manaChargeObserved = manaChargeCasting;

            if (state.IsArmed)
            {
                state.CaptureStoredReality(actionIds);
                if (manaReleaseCasting && kefka is { } releaseKefka)
                {
                    state.BeginRelease(releaseKefka.EntityId);
                }
            }

            // Detect the cast before consuming observations so icons received on the
            // cast-start frame are attributed to this Mana Release.
            var displayHoldEnded = state.Advance(manaReleaseCasting);
            DrainObservations();
            displayHoldEnded |= state.Advance(manaReleaseCasting);
            PublishLogState();

            if (displayHoldEnded)
            {
                Reset("Mana Release display hold ended");
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
        state.Reset();
        manaChargeObserved = false;
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
                ManaReleaseStateMachine.ManaChargeActionId or ManaReleaseStateMachine.ManaReleaseActionId or
                ManaReleaseStateMachine.LightningRealActionId or ManaReleaseStateMachine.LightningFakeFirstActionId or ManaReleaseStateMachine.LightningFakeSecondActionId or
                ManaReleaseStateMachine.IceRealActionId or ManaReleaseStateMachine.IceFakeFirstActionId or ManaReleaseStateMachine.IceFakeSecondActionId))
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

    private void DrainObservations()
    {
        while (deferredErrors.TryDequeue(out var error))
        {
            Record(error);
        }

        while (targetIconObservations.TryDequeue(out var observation))
        {
            // Icons outside a Mana Release are intentionally discarded. During an active
            // release, the state machine records both recognized and unknown IDs so its
            // diagnostics can distinguish a late packet from a bad ID mapping.
            if (!state.IsReleaseActive)
            {
                continue;
            }

            state.ObserveTargetIcon(observation.IconId, observation.TargetEntityId);
        }
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
            if (category == TargetIconCategory)
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
