using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DMUP4DebuffHelper;

/// <summary>Opt-in adapter for Kefka's marker/status-based Mana tracker.</summary>
internal sealed unsafe class ManaReleaseTracker : IDisposable
{
    private const uint KefkaDataId = 18475;
    private const uint TargetIconCategory = 34;

    private readonly Plugin plugin;
    private readonly TimeProvider clock;
    private readonly ManaReleaseStateMachine state;
    private readonly ConcurrentQueue<string> deferredErrors = new();
    private readonly List<ManaReleaseDiagnosticEntry> diagnostics = [];
    private readonly HashSet<string> activeRelevantCasts = new(StringComparer.Ordinal);
    private Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? actorControlHook;
    private volatile bool isEnabled;
    private bool runtimeDisabled;
    private bool warnedRuntimeFailure;
    private bool warnedUpdateFailure;
    private string lastKefkaStatuses = string.Empty;
    private long? lastChargeCastAt;

    public ManaReleaseTracker(Plugin plugin) : this(plugin, TimeProvider.System) { }

    internal ManaReleaseTracker(Plugin plugin, TimeProvider timeProvider)
    {
        this.plugin = plugin;
        clock = timeProvider;
        state = new ManaReleaseStateMachine(Record, timeProvider);
        state.StateChanged += plugin.ObserveManaReleaseForLog;
    }

    public IReadOnlyList<ManaReleaseDiagnosticEntry> Diagnostics
    {
        get
        {
            lock (diagnostics)
                return diagnostics.ToList();
        }
    }

    public ManaReleaseDisplayState? DisplayState => state.DisplayState;

    public void Reconcile(bool shouldRun)
    {
        if (!shouldRun)
        {
            if (isEnabled)
                Disable("Feature unavailable");
            return;
        }
        isEnabled = true;
        if (actorControlHook is not null || runtimeDisabled)
            return;

        try
        {
            actorControlHook = Plugin.GameInteropProvider.HookFromAddress<PacketDispatcher.Delegates.HandleActorControlPacket>(
                PacketDispatcher.MemberFunctionPointers.HandleActorControlPacket, OnHandleActorControlPacket);
            actorControlHook.Enable();
            Record("Target-icon hook initialized; Kefka markers are buffered before Mana Release.");
        }
        catch (Exception ex)
        {
            FailRuntime("Mana Release tracker could not initialize; re-enable the feature to retry.", ex);
        }
    }

    public void Update()
    {
        try
        {
            if (!isEnabled || runtimeDisabled)
                return;

            ManaKefkaSnapshot? kefka = null;
            var currentCasts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gameObject in Plugin.ObjectTable)
            {
                try
                {
                    if (gameObject is not IBattleChara actor)
                        continue;
                    // Charged statuses and actor identity must be read even between casts.
                    if (actor.BaseId == KefkaDataId)
                    {
                        kefka = new(actor.EntityId, actor.IsCasting ? actor.CastActionId : 0,
                            actor.StatusList.Where(status => status.StatusId != 0).Select(status => status.StatusId).ToArray());
                    }
                    if (plugin.Configuration.EnableManaReleaseDiagnostics && actor.IsCasting &&
                        actor.CastActionId is 47780 or 47781 or 47775 or 47776 or 47777 or 47768 or 47771 or 47774)
                    {
                        var key = $"{actor.EntityId:X8}:{actor.CastActionId}";
                        currentCasts.Add(key);
                        if (!activeRelevantCasts.Contains(key))
                            Record($"Relevant cast {actor.CastActionId} from actor {actor.EntityId:X8} (diagnostic only).");
                    }
                }
                catch (Exception ex)
                {
                    Record($"Skipped unavailable actor snapshot: {ex.GetType().Name}.");
                }
            }
            activeRelevantCasts.Clear();
            activeRelevantCasts.UnionWith(currentCasts);

            if (kefka is { } observed)
            {
                var statuses = string.Join(",", observed.StatusIds.OrderBy(id => id));
                var context = $"{observed.EntityId:X8}:{observed.CastActionId}:{statuses}";
                if (observed.CastActionId == ManaReleaseStateMachine.ManaChargeActionId)
                    lastChargeCastAt = clock.GetTimestamp();
                if (context != lastKefkaStatuses &&
                    (lastChargeCastAt is { } chargeAt && clock.GetElapsedTime(chargeAt).TotalSeconds <= 10 ||
                     observed.StatusIds.Any(id => id is ManaReleaseStateMachine.IceChargedStatusId or ManaReleaseStateMachine.ThunderChargedStatusId)))
                    Record($"Kefka {observed.EntityId:X8} charge transition: all observed status IDs=[{statuses}]. Ice Charged 1484 still requires live verification.");
                lastKefkaStatuses = context;
            }

            var hasGrandCross = Plugin.PartyList.Any(member => member.Statuses.Any(status => status.StatusId is >= 5543 and <= 5546));
            while (deferredErrors.TryDequeue(out var error))
                Record(error);
            state.Update(kefka, hasGrandCross);
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

    // Called by Plugin's existing action-effect hook; this method never reads game objects.
    public void QueueActionEffect(uint casterEntityId, uint actionId)
    {
        if (!isEnabled || actionId != ManaReleaseStateMachine.ManaReleaseActionId)
            return;
        try
        {
            state.Enqueue(state.CaptureObservation(ManaObservationKind.ActionEffect, casterEntityId, actionId));
        }
        catch (Exception ex)
        {
            deferredErrors.Enqueue($"Mana Release action-effect observation error: {ex.GetType().Name}.");
        }
    }

    public void Reset(string reason)
    {
        state.Reset(reason);
        while (deferredErrors.TryDequeue(out _)) { }
        activeRelevantCasts.Clear();
        lastKefkaStatuses = string.Empty;
        lastChargeCastAt = null;
    }

    public void ClearDiagnostics()
    {
        lock (diagnostics)
            diagnostics.Clear();
    }

    public void Dispose()
    {
        Disable("Plugin disposed");
        state.StateChanged -= plugin.ObserveManaReleaseForLog;
    }

    private void Disable(string reason)
    {
        isEnabled = false;
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
            runtimeDisabled = false;
            warnedRuntimeFailure = false;
            warnedUpdateFailure = false;
            Reset(reason);
        }
    }

    private void OnHandleActorControlPacket(
        uint entityId, uint category, uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8, GameObjectId targetId, bool isRecorded)
    {
        var hook = actorControlHook;
        try
        {
            if (isEnabled && category == TargetIconCategory)
                state.Enqueue(state.CaptureObservation(ManaObservationKind.TargetIcon, entityId, arg1, arg2));
        }
        catch (Exception ex)
        {
            deferredErrors.Enqueue($"Target-icon observation error: {ex.GetType().Name}.");
        }
        finally
        {
            // Always invoke the original once, even if observation collection fails.
            hook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded);
        }
    }

    private void FailRuntime(string message, Exception ex)
    {
        runtimeDisabled = true;
        try { actorControlHook?.Dispose(); }
        catch { /* Hook teardown must never escape into Dalamud. */ }
        actorControlHook = null;
        Reset("Runtime failure");
        Record($"Error: {message} ({ex.GetType().Name}).");
        if (!warnedRuntimeFailure)
        {
            warnedRuntimeFailure = true;
            Plugin.Log.Warning(ex, message);
        }
    }

    private void Record(string message)
    {
        if (!plugin.Configuration.EnableManaReleaseDiagnostics)
            return;
        lock (diagnostics)
        {
            diagnostics.Add(new ManaReleaseDiagnosticEntry(DateTime.UtcNow, message));
            if (diagnostics.Count > 500)
                diagnostics.RemoveRange(0, diagnostics.Count - 500);
        }
    }
}
