using System;

namespace GatherBuddy.AutoGather.Helpers;

// 純判斷，不碰 Dalamud 與遊戲狀態（離線探針直接編譯這個檔）。
internal static class ReloadResumeRules
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(120);

    public enum WriteDecision
    {
        None,
        NewMarker,
        CarryOver,
    }

    // 時鐘倒退（經過時間為負）一律當過期，寧可不提示。
    public static bool TryGetRemaining(DateTime stoppedAtUtc, DateTime nowUtc, out TimeSpan remaining)
    {
        var age = nowUtc - stoppedAtUtc;
        remaining = Window - age;
        return age >= TimeSpan.Zero && remaining > TimeSpan.Zero;
    }

    // 遊戲結束不寫：下次啟動不該提示「繼續」。提示還沒處理又被重載時延續原記號，不重設時間。
    public static WriteDecision OnDispose(bool frameworkUnloading, bool autoGatherRunning, ulong contentId, bool offerStillAlive)
    {
        if (frameworkUnloading)
            return WriteDecision.None;
        if (autoGatherRunning && contentId != 0)
            return WriteDecision.NewMarker;
        return offerStillAlive ? WriteDecision.CarryOver : WriteDecision.None;
    }
}
