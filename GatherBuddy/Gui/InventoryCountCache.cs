using System;
using System.Collections.Generic;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.Interfaces;

namespace GatherBuddy.Gui;

/// <summary>
/// 介面顯示用的持有數：每個道具最多每 <see cref="RefreshMs"/> 毫秒真的查一次，其餘沿用上一次的答案（最多晚這麼久）。
/// 開「檢查僱員」時每次查詢都是一次 AllaganTools IPC，原本每列每幀都打。
/// </summary>
/// <remarks>
/// 只從繪製執行緒呼叫（沒有鎖）。自動採集的判斷不走這裡，照舊每次現查。
/// 刻意不訂 AllaganTools.ItemAdded／ItemRemoved 來作廢：參數是含 ClientStructs 列舉的值元組，跨插件型別相容性沒驗過；只靠到期。
/// </remarks>
internal static class InventoryCountCache
{
    private const long RefreshMs = 500;

    private static readonly Dictionary<IGatherable, int> Counts = new();
    private static          long                         _clearedAt = -RefreshMs;

    public static int Get(IGatherable item)
    {
        var now = Environment.TickCount64;
        if (now - _clearedAt >= RefreshMs)
        {
            Counts.Clear();
            _clearedAt = now;
        }

        if (!Counts.TryGetValue(item, out var count))
        {
            count        = item.GetInventoryCount();
            Counts[item] = count;
        }

        return count;
    }
}
