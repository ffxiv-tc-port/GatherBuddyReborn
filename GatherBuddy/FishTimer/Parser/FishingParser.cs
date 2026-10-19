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

    /// <summary>false＝兩個 hook 沒有都解到，整個解析器不啟用（釣魚計時器與釣魚紀錄不更新）。</summary>
    public bool Available
        => _catchHook != null && _hookHook != null;

    public unsafe FishingParser(IGameInteropProvider provider)
    {
        FishingSpotNames = SetupFishingSpotNames();

        // 刻意兩個 hook 缺一就都不建：少了釣獲通知會把每一竿記成「沒釣到」，少了提鉤偵測紀錄一律無效。
        var catchPtr = new UpdateFishCatch(Dalamud.SigScanner).Address;
        var hookPtr  = (IntPtr)ActionManager.MemberFunctionPointers.UseAction;
        if (hookPtr == IntPtr.Zero)
            GatherBuddy.Log.Warning("遊戲改版後 ActionManager.UseAction 的位址對不上：偵測不到提鉤，釣魚計時器與釣魚紀錄停用；其餘功能照常。");
        if (catchPtr == IntPtr.Zero || hookPtr == IntPtr.Zero)
            return;

        // 只建立、不啟用：啟用統一由 Enable() 做，detour 讀得到欄位之後才會被呼叫。
        _catchHook = provider.HookFromAddress<UpdateCatchDelegate>(catchPtr, OnCatchUpdate);
        _hookHook  = provider.HookFromAddress<UseActionDelegate>(hookPtr, OnUseAction);
    }

    public void Enable()
    {
        // 解析器不可用時連聊天訊息也不訂閱：只靠聊天訊息會產生「沒釣到」的假紀錄。
        if (!Available)
            return;

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

    // fail-closed: these two are detours, i.e. managed functions that native code calls directly; a managed
    // exception escaping one unwinds through native frames that have no handler, which terminates the
    // process. Both bodies raise events (CaughtFish / HookedIn) whose subscribers are ordinary managed code,
    // so "a subscriber threw" is a normal failure mode. Our own logic runs inside a try; Original() is kept
    // OUTSIDE it so the game's own behaviour is never skipped or duplicated. Does NOT stop AccessViolation.
    private static long _detourErrors;
    private static DateTime _lastDetourErrorLog = DateTime.MinValue;

    private static void OnDetourError(string site, Exception ex)
    {
        ++_detourErrors;
        var now = DateTime.UtcNow;
        if (now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        // Information: a diagnostic users are asked to report.
        GatherBuddy.Log.Information($"[FishingParser] {site} threw and was swallowed so it could not escape into native code (total {_detourErrors}); the game's own call went through unchanged: {ex}");
    }

    private void OnCatchUpdate(IntPtr module, uint fishId, bool large, ushort size, byte amount, byte level, byte unk7, byte unk8, byte unk9,
        byte unk10, byte unk11, byte unk12)
    {
        if (!GatherBuddy.Config.HideFishSizePopup)
            _catchHook!.OriginalDisposeSafe(module, fishId, large, size, amount, level, unk7, unk8, unk9, unk10, unk11, unk12);

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

        return _hookHook!.OriginalDisposeSafe(manager, actionType, actionId, targetId, a4, a5, a6, a7);
    }
}
