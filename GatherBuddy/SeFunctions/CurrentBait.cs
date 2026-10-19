using Dalamud.Game;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using GatherBuddy.Plugin;

namespace GatherBuddy.SeFunctions;

public sealed class CurrentBait : SeAddressBase
{
    public CurrentBait(ISigScanner sigScanner)
        : base(sigScanner, "8B 0D ?? ?? ?? ?? 3B D9 75",
            missingEffect: "讀不到目前的魚餌（宇宙探索以外）：換餌停用，釣魚紀錄的魚餌記為未知，自動採集需要換餌時會停下；其餘功能照常。")
    {
        Dalamud.Interop.InitializeFromAttributes(this);

        if (_executeCommand == null)
            GatherBuddy.Log.Warning($"遊戲改版後「切換魚餌」的遊戲函式對不上。{ChangeBaitUnavailableNotice}");
    }

    public const string ChangeBaitUnavailableNotice =
        "換餌功能停用（魚類清單點魚餌、/gatherfish 的自動換餌、自動採集的換餌），其餘功能照常。請等插件更新。";

    /// <summary>目前掛的魚餌。回 false＝不知道（位址沒解到，而且不在宇宙探索）。</summary>
    public unsafe bool TryGetCurrent(out uint baitId)
    {
        var territoryId = Dalamud.ClientState.TerritoryType;
        if (GatherBuddy.GameData.Territories.TryGetValue(territoryId, out var territory) && territory.Data.TerritoryIntendedUse.RowId is 60)
        {
            var cosmicManager = WKSManager.Instance();
            if (cosmicManager != null)
            {
                baitId = cosmicManager->FishingBait;
                return true;
            }
        }

        if (!IsAvailable)
        {
            baitId = 0;
            return false;
        }

        baitId = *(uint*)Address;
        return true;
    }

    /// <summary>0＝沒有掛餌，或不知道；會依結果動作的呼叫端改用 <see cref="TryGetCurrent"/>。</summary>
    public uint Current
        => TryGetCurrent(out var baitId) ? baitId : 0;

    private delegate byte ExecuteCommandDelegate(int id, int unk1, uint baitId, int unk2, int unk3);

    // null＝特徵碼對不上。刻意宣告成 Fallible：只停換餌，不讓整個插件載入失敗。
    [Signature("E8 ?? ?? ?? ?? 41 C6 04 24", Fallibility = Fallibility.Fallible)]
    private readonly ExecuteCommandDelegate? _executeCommand = null;

    public enum ChangeBaitReturn
    {
        Success,
        AlreadyEquipped,
        NotInInventory,
        InvalidBait,
        UnknownError,
        Unavailable,
    }

    public static unsafe int HasItem(uint itemId)
        => InventoryManager.Instance()->GetInventoryItemCount(itemId);

    private long _nextUnavailableNoticeTick;

    /// <summary>換餌做不到時回 <see cref="ChangeBaitReturn.Unavailable"/>，並在聊天視窗提示（最多 10 秒一次）。</summary>
    private ChangeBaitReturn ReportUnavailable()
    {
        var now = System.Environment.TickCount64;
        if (now >= _nextUnavailableNoticeTick)
        {
            _nextUnavailableNoticeTick = now + 10_000;
            Communicator.PrintError($"[GatherBuddy Reborn] 遊戲改版後有遊戲位址對不上：{ChangeBaitUnavailableNotice}");
        }

        return ChangeBaitReturn.Unavailable;
    }

    public ChangeBaitReturn ChangeBait(uint baitId)
    {
        // 刻意：不知道目前掛的是什麼餌就不送換餌指令（無法判斷是不是已經裝備）。
        if (!TryGetCurrent(out var current))
            return ReportUnavailable();

        if (baitId == current)
            return ChangeBaitReturn.AlreadyEquipped;

        if (baitId == 0 || !GatherBuddy.GameData.Bait.ContainsKey(baitId))
            return ChangeBaitReturn.InvalidBait;

        if (HasItem(baitId) <= 0)
            return ChangeBaitReturn.NotInInventory;

        if (_executeCommand is not { } executeCommand)
            return ReportUnavailable();

        return executeCommand(701, 4, baitId, 0, 0) == 1 ? ChangeBaitReturn.Success : ChangeBaitReturn.UnknownError;
    }
}
