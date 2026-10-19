using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.Plugin;

/// <summary>
/// IPC 端點的「遊戲主執行緒閘門」，做法照 Artisan／Lifestream 的同名檔。
/// </summary>
/// <remarks>
/// CallGate 是直接方法呼叫，提供端跑在<b>呼叫端的執行緒</b>上，而自動採集清單
/// 同時被 UI 每幀列舉、被存檔 <c>foreach</c> 走訪。
/// 🔴 用 <c>RunOnFrameworkThread</c> 不是 <c>Framework.Run</c>：前者已在主執行緒時就地執行，同步等不會死結。
/// </remarks>
internal static class IpcFrameworkGate
{
    /// <summary>等主執行緒的上限。超過就當作「現在做不到」。</summary>
    internal const int TimeoutMs = 5000;

    private const int StatePending   = 0;
    private const int StateRunning   = 1;
    private const int StateAbandoned = 2;

    /// <summary>有回傳值的端點。<paramref name="unavailable"/> 是逾時時要回的「不可用」值。</summary>
    internal static T Get<T>(string endpoint, Func<T> body, T unavailable)
    {
        if (Dalamud.Framework.IsInFrameworkUpdateThread)
            return body();

        if (IsUnloading(endpoint))
            return unavailable;

        var state = StatePending;
        var task = Dalamud.Framework.RunOnFrameworkThread(() =>
        {
            // 呼叫端已經逾時走人了就什麼都不做。
            if (Interlocked.CompareExchange(ref state, StateRunning, StatePending) != StatePending)
                return unavailable;

            return body();
        });
        // WaitAny 對已經失敗的工作也回 0（不擲），交給 GetResult 原樣重擲原始例外，
        // 這樣呼叫端看到的例外型別與沒有這層閘門時完全一樣（不會變成 AggregateException）。
        if (Task.WaitAny([task], TimeoutMs) == 0)
            return task.GetAwaiter().GetResult();

        ReportTimeout(endpoint, Interlocked.CompareExchange(ref state, StateAbandoned, StatePending) == StatePending);
        return unavailable;
    }

    /// <summary>沒有回傳值的端點。逾時／卸載期＝這次什麼都沒做，簽章沒有回值可以告知，只留 log。</summary>
    internal static void Run(string endpoint, Action body)
        => Get(endpoint, () =>
        {
            body();
            return true;
        }, false);

    /// <summary>同一個端點的逾時訊息重印間隔。</summary>
    private const long TimeoutLogIntervalMs = 10000;

    /// <summary>節流表上限，避免端點名意外發散時無限成長。</summary>
    private const int MaxTrackedTimeoutKeys = 128;

    private static readonly Dictionary<string, long> TimeoutLogTimes = [];

    /// <summary>
    /// 🔴 Dalamud 卸載期的閘門旁路：<c>RunOnFrameworkThread</c> 在 <c>IsFrameworkUnloading</c>
    /// 為真時<b>就地在呼叫端執行緒</b>執行 body，等於這一層完全失效。
    /// 🔑 所以卸載期一律直接回「不可用」值：那一瞬間功能失效可以接受，資料結構被並行改壞不行。
    /// </summary>
    private static bool IsUnloading(string endpoint)
    {
        if (!Dalamud.Framework.IsFrameworkUnloading || Dalamud.Framework.IsInFrameworkUpdateThread)
            return false;

        if (ShouldLogTimeout(endpoint + "/卸載期"))
            GatherBuddy.Log.Information(
                $"[GatherBuddy IPC 閘門] {endpoint} 在 Dalamud 卸載期從別的執行緒被呼叫，已回傳「不可用」值。");

        return true;
    }

    /// <summary>
    /// 自帶的節流：首次必放行，之後每 <see cref="TimeoutLogIntervalMs"/> 毫秒放行一次。
    /// 🔴 刻意<b>不用</b> <c>EzThrottler</c>：它是整個外掛共用的靜態 Dictionary 且零同步，
    /// 而這條路徑跑在呼叫端的執行緒上，並行插入弄壞的是整張表。
    /// 🔴 鎖內只碰字典 —— 不寫 log、不做 I/O、不呼叫任何別的外掛。
    /// </summary>
    private static bool ShouldLogTimeout(string key)
    {
        var now = Environment.TickCount64;
        lock (TimeoutLogTimes)
        {
            if (TimeoutLogTimes.TryGetValue(key, out var last) && now - last < TimeoutLogIntervalMs)
                return false;
            if (TimeoutLogTimes.Count >= MaxTrackedTimeoutKeys && !TimeoutLogTimes.ContainsKey(key))
                TimeoutLogTimes.Clear();
            TimeoutLogTimes[key] = now;
            return true;
        }
    }

    /// <summary>要使用者回報的診斷寫 Information。</summary>
    private static void ReportTimeout(string endpoint, bool abandoned)
    {
        if (!ShouldLogTimeout(endpoint))
            return;

        var outcome = abandoned
            ? "工作還沒開始就被取消，什麼都沒做"
            : "工作已經開始執行，會照常跑完（呼叫端拿到的回值不代表它沒發生）";
        GatherBuddy.Log.Information(
            $"[GatherBuddy IPC 閘門] {endpoint} 等待遊戲主執行緒超過 {TimeoutMs} 毫秒，已回傳「不可用」值。{outcome}。通常代表遊戲正在讀取畫面或嚴重掉幀；若持續出現請回報。");
    }
}
