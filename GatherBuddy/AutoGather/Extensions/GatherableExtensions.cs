using Dalamud.Plugin.Ipc.Exceptions;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.Extensions;

/// <summary>
/// Extension methods for the IGatherable interface.
/// </summary>
public static class GatherableExtensions
{
    private static IReadOnlyList<InventoryType> _inventoryTypes { get; } =
        [
            InventoryType.RetainerCrystals,
            InventoryType.RetainerPage1,
            InventoryType.RetainerPage2,
            InventoryType.RetainerPage3,
            InventoryType.RetainerPage4,
            InventoryType.RetainerPage5,
            InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
            InventoryType.Crystals
        ];

    /// <summary>
    /// Gets the inventory count for a gatherable item.
    /// </summary>
    /// <param name="gatherable">The gatherable item to check.</param>
    /// <param name="checkRetainers">Check retainer inventory.</param>
    /// <returns>The count of the item in the inventory.</returns>
    public unsafe static int GetInventoryCount(this IGatherable gatherable)
    {
        if (GatherBuddy.Config.AutoGatherConfig.CheckRetainers && AllaganTools.Enabled)
        {
            // 🔴 AllaganTools.Enabled 是節流過的答案(最多 5 秒舊),而這一段從「繪製執行緒」
            //    也走得到(採集視窗與自動採集清單分頁逐項畫背包數量)。AllaganTools 剛好在那個
            //    窗裡被卸載的話,ItemCountOwned 會擲 IpcNotReadyError —— 在繪製路徑上那會變成
            //    Dalamud 的視窗錯誤面板。安全值＝往下走本機背包那條,與「沒裝 AllaganTools」時
            //    走的是同一條路;順手作廢存在性快取,下一次查詢就會重查成「不在」。
            //    只攔 IpcNotReadyError:參數個數/型別寫錯擲的其他 IpcError 必須繼續往上冒。
            try
            {
                return (int)AllaganTools.ItemCountOwned(gatherable.ItemId, true, _inventoryTypes.Select(it => (uint)it).ToArray());
            }
            catch (IpcNotReadyError)
            {
                IPCSubscriber.InvalidatePresence(AllaganTools.InternalName);
            }
        }

        var inventory = InventoryManager.Instance();
        return inventory->GetInventoryItemCount(gatherable.ItemId, false, false, false, (short)(gatherable.ItemData.IsCollectable ? 1 : 0));
    }
}
