using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

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
    private const string LightningVfx = "m0462trg_c03c.avfx";
    private const string IceVfx = "m0462trg_c05c.avfx";
    private const string VfxSignature = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";

    private delegate nint ActorVfxCreateDelegate(byte* path, nint targetAddress, nint a3, float a4, byte a5, ushort a6, byte a7);

    private readonly Plugin plugin;
    private readonly ConcurrentQueue<VfxObservation> vfxObservations = new();
    private readonly ConcurrentQueue<string> deferredErrors = new();
    private readonly List<ManaReleaseDiagnosticEntry> diagnostics = [];
    private readonly HashSet<string> activeRelevantCasts = new(StringComparer.Ordinal);
    private Hook<ActorVfxCreateDelegate>? vfxHook;
    private bool isEnabled;
    private bool runtimeDisabled;
    private bool warnedRuntimeFailure;
    private bool armed;
    private RealityState lightningStored;
    private RealityState iceStored;
    private bool releaseActive;
    private bool releaseLightningFake;
    private bool releaseIceFake;
    private bool releaseVfxObserved;
    private bool releaseResolved;
    private bool manaChargeObserved;
    private nint releaseKefkaAddress;
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
        ? new ManaReleaseDisplayState(armed, lightningStored, iceStored, releaseActive, safeZone)
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

        if (vfxHook is not null || runtimeDisabled)
        {
            return;
        }

        try
        {
            vfxHook = Plugin.GameInteropProvider.HookFromSignature<ActorVfxCreateDelegate>(VfxSignature, OnActorVfxCreate);
            vfxHook.Enable();
            Record("VFX hook initialized.");
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
            DrainObservations();
            if (runtimeDisabled)
            {
                return;
            }

            var casters = Plugin.ObjectTable
                .Where(obj => obj is IBattleChara { IsCasting: true })
                .Cast<IBattleChara>()
                .ToList();
            var actionIds = casters.Select(caster => caster.CastActionId).ToList();
            RecordRelevantCasts(casters);
            var kefka = Plugin.ObjectTable
                .OfType<IBattleChara>()
                .FirstOrDefault(actor => actor.BaseId == KefkaDataId && actor.IsCasting);

            var manaChargeCasting = actionIds.Contains(ManaChargeActionId);
            if (manaChargeCasting && !manaChargeObserved)
            {
                Arm();
            }
            manaChargeObserved = manaChargeCasting;

            if (!armed)
            {
                return;
            }

            CaptureStoredReality(actionIds);
            if (kefka is not null && kefka.CastActionId == ManaReleaseActionId && !releaseActive)
            {
                BeginRelease(kefka);
            }

            if (releaseActive)
            {
                if (releaseVfxObserved && !releaseResolved)
                {
                    ResolveRelease();
                }

                if (kefka is null || kefka.CastActionId != ManaReleaseActionId)
                {
                    if (!releaseVfxObserved)
                    {
                        safeZone = ManaReleaseSafeZone.Unknown;
                        Record("Mana Release resolved as Unknown: no Kefka lock-on VFX observed.");
                    }

                    Reset("Mana Release ended");
                }
            }
        }
        catch (Exception ex)
        {
            safeZone = ManaReleaseSafeZone.Unknown;
            releaseActive = true;
            FailRuntime("Mana Release tracker encountered an error; calls will remain Unknown until it is re-enabled.", ex);
        }
    }

    public void Reset(string reason)
    {
        armed = false;
        lightningStored = RealityState.Unknown;
        iceStored = RealityState.Unknown;
        releaseActive = false;
        releaseLightningFake = false;
        releaseIceFake = false;
        releaseVfxObserved = false;
        releaseResolved = false;
        manaChargeObserved = false;
        releaseKefkaAddress = nint.Zero;
        safeZone = ManaReleaseSafeZone.Unknown;
        while (vfxObservations.TryDequeue(out _)) { }
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
            vfxHook?.Dispose();
        }
        catch (Exception ex)
        {
            FailRuntime("Mana Release VFX hook disposal failed.", ex);
        }
        finally
        {
            vfxHook = null;
            isEnabled = false;
            runtimeDisabled = false;
            warnedRuntimeFailure = false;
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

    private void RecordRelevantCasts(IReadOnlyList<IBattleChara> casters)
    {
        if (!plugin.Configuration.EnableManaReleaseDiagnostics)
        {
            return;
        }

        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var caster in casters)
        {
            if (caster.CastActionId is not (
                ManaChargeActionId or ManaReleaseActionId or
                LightningRealActionId or LightningFakeFirstActionId or LightningFakeSecondActionId or
                IceRealActionId or IceFakeFirstActionId or IceFakeSecondActionId))
            {
                continue;
            }

            var key = $"{caster.EntityId:X8}:{caster.CastActionId}";
            current.Add(key);
            if (!activeRelevantCasts.Contains(key))
            {
                Record($"Relevant cast {caster.CastActionId} from actor {caster.EntityId:X8}.");
            }
        }

        activeRelevantCasts.Clear();
        activeRelevantCasts.UnionWith(current);
    }

    private void BeginRelease(IBattleChara kefka)
    {
        releaseActive = true;
        safeZone = ManaReleaseSafeZone.Unknown;
        releaseKefkaAddress = kefka.Address;
        Record($"Mana Release (47781) detected from actor {kefka.EntityId:X8}.");
    }

    private void DrainObservations()
    {
        while (deferredErrors.TryDequeue(out var error))
        {
            Record(error);
        }

        while (vfxObservations.TryDequeue(out var observation))
        {
            if (!releaseActive || observation.TargetAddress != releaseKefkaAddress)
            {
                continue;
            }

            releaseVfxObserved = true;
            if (observation.IsLightning)
            {
                releaseLightningFake = true;
            }
            else if (observation.IsIce)
            {
                releaseIceFake = true;
            }

            if (plugin.Configuration.EnableManaReleaseDiagnostics)
            {
                Record($"Mana Release lock-on VFX: {observation.Path}.");
            }
        }
    }

    private void ResolveRelease()
    {
        releaseResolved = true;
        if (lightningStored == RealityState.Unknown || iceStored == RealityState.Unknown)
        {
            safeZone = ManaReleaseSafeZone.Unknown;
            Record("Mana Release resolved as Unknown: stored data missing.");
            return;
        }

        var finalLightningFake = (lightningStored == RealityState.Fake) ^ releaseLightningFake;
        var finalIceFake = (iceStored == RealityState.Fake) ^ releaseIceFake;
        safeZone = (finalLightningFake, finalIceFake) switch
        {
            (true, false) => ManaReleaseSafeZone.Lightning,
            (false, true) => ManaReleaseSafeZone.Ice,
            (true, true) => ManaReleaseSafeZone.Both,
            _ => ManaReleaseSafeZone.None,
        };
        Record($"Mana Release resolved: Lightning fake={finalLightningFake}, Ice fake={finalIceFake}, safe={safeZone}.");
    }

    private nint OnActorVfxCreate(byte* path, nint targetAddress, nint a3, float a4, byte a5, ushort a6, byte a7)
    {
        // Keep the hook instance alive across the detour so teardown cannot cause a second
        // lookup or skip the original invocation.
        var hook = vfxHook;
        try
        {
            if (releaseActive && targetAddress == releaseKefkaAddress && path is not null)
            {
                var value = Marshal.PtrToStringAnsi((nint)path, 256) ?? string.Empty;
                var isLightning = value.EndsWith(LightningVfx, StringComparison.OrdinalIgnoreCase);
                var isIce = value.EndsWith(IceVfx, StringComparison.OrdinalIgnoreCase);
                if (value.Contains("/lockon/", StringComparison.OrdinalIgnoreCase))
                {
                    vfxObservations.Enqueue(new VfxObservation(targetAddress, value, isLightning, isIce));
                }
            }
        }
        catch (Exception ex)
        {
            deferredErrors.Enqueue($"VFX observation error: {ex.GetType().Name}.");
        }

        // This function must always call the original exactly once.
        return hook!.Original(path, targetAddress, a3, a4, a5, a6, a7);
    }

    private void FailRuntime(string message, Exception ex)
    {
        runtimeDisabled = true;
        try
        {
            vfxHook?.Dispose();
        }
        catch
        {
            // A failed hook teardown must never escape into Dalamud.
        }

        vfxHook = null;
        armed = false;
        lightningStored = RealityState.Unknown;
        iceStored = RealityState.Unknown;
        releaseActive = true;
        safeZone = ManaReleaseSafeZone.Unknown;
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

    private sealed record VfxObservation(nint TargetAddress, string Path, bool IsLightning, bool IsIce);
}
