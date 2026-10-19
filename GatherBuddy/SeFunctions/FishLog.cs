using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Classes;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using Action = System.Action;

namespace GatherBuddy.SeFunctions;

public unsafe class FishLog
{
    public const     uint  SpearFishIdOffset = 20000;
    private readonly byte* _fish;
    private readonly byte* _spearFish;
    private readonly uint  _numFish;
    private readonly uint  _numSpearFish;

    private readonly byte[] _fishStore;
    private readonly byte[] _spearFishStore;

    // 比對長度夾在遊戲位元陣列的大小內：表的列數換算的位元組數比陣列長，多出的部分是 PlayerState 的其他欄位（魚餌等）。
    private readonly int _fishCompareLength;
    private readonly int _spearFishCompareLength;

    public event Action? Change;

    /// <summary> false＝PlayerState 的位址解不出：釣魚筆記一律當成沒釣到、不發 Change。 </summary>
    public bool IsAvailable
        => _fish != null;

    public FishLog(ISigScanner sigScanner, IDataManager gameData)
    {
        _numFish      = (uint) gameData.GetExcelSheet<FishParameter>().Count;
        _numSpearFish = (uint) gameData.GetExcelSheet<SpearfishingItem>().Count;

        _fishStore      = new byte[(_numFish + 7) / 8];
        _spearFishStore = new byte[(_numSpearFish + 7) / 8];

        // 位址解不出或 CS 成員不見時都不擲：指標留 null、比對長度 0（IsAvailable＝false）。
        // 碰 ClientStructs 的部分在另一個方法：成員不見時 JIT 在進方法前就擲，只有呼叫端攔得到。
        try
        {
            if (!TryResolvePlayerState(out _fish, out _spearFish, out var fishLength, out var spearFishLength))
            {
                GatherBuddy.Log.Warning("遊戲改版後 PlayerState 的位址對不上：讀不到釣魚筆記，釣魚筆記欄一律顯示「?」；其餘功能照常。");
                return;
            }

            _fishCompareLength      = Math.Min(_fishStore.Length,      fishLength);
            _spearFishCompareLength = Math.Min(_spearFishStore.Length, spearFishLength);
        }
        catch (Exception e)
        {
            _fish      = null;
            _spearFish = null;
            GatherBuddy.Log.Warning($"遊戲改版後讀不到釣魚筆記（{e.GetType().Name}: {e.Message}），釣魚筆記欄一律顯示「?」；其餘功能照常。");
            return;
        }

        GatherBuddy.Log.Debug($"FishLog pointers: fish 0x{(ulong)_fish:X16}, spearfish 0x{(ulong)_spearFish:X16}.");
        CheckForChanges();
    }

    /// <summary> false＝位址解不出（不擲）；CS 成員不見時由 JIT 擲給呼叫端。 </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryResolvePlayerState(out byte* fish, out byte* spearFish, out int fishLength, out int spearFishLength)
    {
        fish            = null;
        spearFish       = null;
        fishLength      = 0;
        spearFishLength = 0;
        if (PlayerState.Addresses.Instance.Value == 0)
            return false;

        // 用 ClientStructs PlayerState 的捕魚紀錄位元陣列(舊的 FishLogData/SpearFishLogData 靜態簽名在台服已失效)。
        var playerState = PlayerState.Instance();
        fish            = (byte*)Unsafe.AsPointer(ref playerState->CaughtFish[0]);
        spearFish       = (byte*)Unsafe.AsPointer(ref playerState->CaughtSpearfish[0]);
        fishLength      = playerState->CaughtFish.Length;
        spearFishLength = playerState->CaughtSpearfish.Length;
        return true;
    }

    private static bool CheckForChanges(byte* ptr, byte[] data, int num)
    {
        fixed (byte* ptr2 = data)
        {
            if (Functions.MemCmpUnchecked(ptr, ptr2, num))
                return false;
        }

        Marshal.Copy((IntPtr)ptr, data, 0, num);
        return true;
    }

    public bool CheckForChanges()
    {
        if (!IsAvailable)
            return false;

        if (!CheckForChanges(_fish,      _fishStore,      _fishCompareLength)
         && !CheckForChanges(_spearFish, _spearFishStore, _spearFishCompareLength))
            return false;

        Change?.Invoke();
        return true;
    }


    public bool SpearFishIsUnlocked(uint spearFishId)
    {
        spearFishId -= SpearFishIdOffset;
        if (spearFishId >= _numSpearFish)
        {
            GatherBuddy.Log.Error($"Spearfish Id {spearFishId} is larger than number of spearfish in log {_numSpearFish}.");
            return false;
        }

        // 只有建構子判定位址解不出時才是 null，那時已記過一行 Warning；這裡每格都會問，不再逐次記錄。
        if (_spearFish == null)
            return false;

        var offset = spearFishId / 8;
        var bit    = (byte)spearFishId % 8;
        return ((_spearFish[offset] >> bit) & 1) == 1;
    }

    public bool FishIsUnlocked(uint fishId)
    {
        if (fishId >= _numFish)
        {
            GatherBuddy.Log.Error($"Fish Id {fishId} is larger than number of fish in log {_numFish}.");
            return false;
        }

        if (_fish == null)
            return false;

        var offset = fishId / 8;
        var bit    = (byte)fishId % 8;
        return ((_fish[offset] >> bit) & 1) == 1;
    }

    public bool IsUnlocked(Fish fish)
        => !fish.InLog || (fish.IsSpearFish ? SpearFishIsUnlocked(fish.FishId) : FishIsUnlocked(fish.FishId));
}
