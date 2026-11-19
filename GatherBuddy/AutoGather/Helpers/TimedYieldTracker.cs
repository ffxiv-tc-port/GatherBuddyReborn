using System.Collections.Generic;
using System.Threading;

namespace GatherBuddy.AutoGather.Helpers;

// 限時點每窗產量：記本次載入求解器在整窗開頭算出的期望總產量，只存在記憶體。
// 求解器 await 之後的接續可能在執行緒池上 ⇒ 寫讀都加鎖；Version 讓介面只在有新值時重算。
internal static class TimedYieldTracker
{
    public enum Source
    {
        None,
        SameItem,
        OtherItems,
    }

    private static readonly object                   Lock   = new();
    private static readonly Dictionary<uint, double> ByItem = new();
    private static          int                      _version;

    public static int Version
        => Volatile.Read(ref _version);

    public static void Record(uint itemId, double yieldPerWindow)
    {
        if (!(yieldPerWindow >= 1) || yieldPerWindow > 999)
            return;

        lock (Lock)
            ByItem[itemId] = yieldPerWindow;
        Interlocked.Increment(ref _version);
    }

    public static Source TryGet(uint itemId, out double yieldPerWindow)
    {
        lock (Lock)
        {
            if (ByItem.TryGetValue(itemId, out yieldPerWindow))
                return Source.SameItem;

            if (ByItem.Count == 0)
                return Source.None;

            var sum = 0.0;
            foreach (var value in ByItem.Values)
                sum += value;
            yieldPerWindow = sum / ByItem.Count;
            return Source.OtherItems;
        }
    }
}
