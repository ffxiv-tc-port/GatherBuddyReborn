using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.SeFunctions;

namespace GatherBuddy.FishTimer.Parser;

public partial class FishingParser : IDisposable
{
    private delegate bool UseActionDelegate(IntPtr manager, ActionType actionType, uint actionId, ulong targetId, uint a4, uint a5,
        uint a6, IntPtr a7);

    public event Action<FishingSpot?>?                   BeganFishing;
    public event Action?                                 BeganMooching;
    public event Action<Fish, ushort, byte, bool, bool>? CaughtFish;
    public event Action<FishingSpot>?                    IdentifiedSpot;
    public event Action<HookSet>?                        HookedIn;
    private readonly Hook<UpdateCatchDelegate>?          _catchHook;
    private readonly Hook<UseActionDelegate>?            _hookHook;

    public unsafe FishingParser(IGameInteropProvider provider)
    {
        FishingSpotNames = SetupFishingSpotNames();
        _catchHook       = new UpdateFishCatch(Dalamud.SigScanner).CreateHook(provider, OnCatchUpdate);
        var hookPtr = (IntPtr)ActionManager.MemberFunctionPointers.UseAction;
        _hookHook = provider.HookFromAddress<UseActionDelegate>(hookPtr, OnUseAction);
    }

    public void Enable()
    {
        _hookHook?.Enable();
        _catchHook?.Enable();
        Dalamud.Chat.CheckMessageHandled += OnMessageDelegate;
    }

    public void Disable()
    {
        _catchHook?.Disable();
        _hookHook?.Disable();
        Dalamud.Chat.CheckMessageHandled -= OnMessageDelegate;
    }

    public void Dispose()
    {
        Disable();
        _catchHook?.Dispose();
        _hookHook?.Dispose();
    }

    // fail-closed: these two are detours, i.e. managed functions that native code calls directly. A managed
    // exception escaping one of them unwinds through native frames that have no handler, which terminates
    // the process. Both bodies below raise events (CaughtFish / HookedIn) whose subscribers are ordinary
    // managed code - FishRecorder, the fish timer window, ... - so "a subscriber threw" is a real and
    // entirely normal failure mode. Our own logic therefore runs inside a try; Original() is deliberately
    // kept OUTSIDE it, so the game's own behaviour is never skipped or duplicated because we failed.
    // NOTE: this does NOT protect against AccessViolationException (corrupted-state, uncatchable).
    private static long _detourErrors;
    private static DateTime _lastDetourErrorLog = DateTime.MinValue;

    private static void OnDetourError(string site, Exception ex)
    {
        ++_detourErrors;
        var now = DateTime.UtcNow;
        if (now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        // Information, not Debug: users who report problems run at LogLevel 2.
        GatherBuddy.Log.Information($"[FishingParser] {site} threw and was swallowed so it could not escape into native code (total {_detourErrors}); the game's own call went through unchanged: {ex}");
    }

    private void OnCatchUpdate(IntPtr module, uint fishId, bool large, ushort size, byte amount, byte level, byte unk7, byte unk8, byte unk9,
        byte unk10, byte unk11, byte unk12)
    {
        if (!GatherBuddy.Config.HideFishSizePopup)
            _catchHook!.Original(module, fishId, large, size, amount, level, unk7, unk8, unk9, unk10, unk11, unk12);

        try
        {
            // Check against collectibles.
            var collectible = false;
            if (fishId > 500000)
            {
                fishId      -= 500000;
                collectible =  true;
            }

            if (!GatherBuddy.GameData.Fishes.TryGetValue(fishId, out var fish))
            {
                GatherBuddy.Log.Error($"Unknown fish id {fishId} caught.");
                return;
            }

            CaughtFish?.Invoke(fish, size, amount, large, collectible);
        }
        catch (Exception ex)
        {
            OnDetourError(nameof(OnCatchUpdate), ex);
        }
    }

    private bool OnUseAction(IntPtr manager, ActionType actionType, uint actionId, ulong targetId, uint a4, uint a5, uint a6, IntPtr a7)
    {
        try
        {
            if (actionType == ActionType.Action)
                switch (actionId)
                {
                    case 296:   HookedIn?.Invoke(HookSet.Hook); break;
                    case 269:   HookedIn?.Invoke(HookSet.DoubleHook); break;
                    case 4103:  HookedIn?.Invoke(HookSet.Powerful); break;
                    case 4179:  HookedIn?.Invoke(HookSet.Precise); break;
                    case 27523: HookedIn?.Invoke(HookSet.TripleHook); break;
                    case 41278: HookedIn?.Invoke(HookSet.Stellar); break;
                }
        }
        catch (Exception ex)
        {
            OnDetourError(nameof(OnUseAction), ex);
        }

        return _hookHook!.Original(manager, actionType, actionId, targetId, a4, a5, a6, a7);
    }
}
