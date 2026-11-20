using System;
using Dalamud.Game.ClientState.Conditions;
using ECommons.LanguageHelpers;
using GatherBuddy.AutoGather.Helpers;

namespace GatherBuddy.AutoGather;

/// <summary>
/// 本檔契約：自動採集的每個傳送出口（以太之光、Lifestream 乙太網、回家）送出前都先問 <see cref="HoldTeleportForBusyPlayer"/>；
/// 使用者手動下的 /gather、清單上的傳送鈕不經這裡（那是他自己要傳）。
/// 等待靠「這一幀不送、下一幀再問」，不靠 TaskManager 逾時重試。
/// </summary>
public partial class AutoGather
{
    private readonly TeleportBusyGate _teleportGate = new();

    // OccupiedInEvent、WaitingForDuty 有 log 為證：送出後等不到換區（市場佈告板、兌換商店、任務搜尋器的參加確認）。
    // 其餘是同類的「使用者正在操作」狀態（部分已被 CanAct 擋住），列進來是為了忙完後一樣等安靜期。
    // 刻意不列 InDutyQueue：排隊中照樣能傳送（log 裡有 95 次成功）。
    private static string? TeleportBusyReason()
    {
        var c = Dalamud.Conditions;
        if (c[ConditionFlag.OccupiedInEvent])
            return "OccupiedInEvent（與 NPC、商店或市場佈告板互動中）";
        if (c[ConditionFlag.WaitingForDuty] || c[ConditionFlag.WaitingForDutyFinder])
            return "WaitingForDuty（任務搜尋器的參加確認中）";
        if (c[ConditionFlag.OccupiedSummoningBell])
            return "OccupiedSummoningBell（使用傳喚鈴中）";
        if (c[ConditionFlag.OccupiedInQuestEvent])
            return "OccupiedInQuestEvent（對話中）";
        if (c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78])
            return "OccupiedInCutSceneEvent／WatchingCutscene（觀看過場動畫中）";
        if (c[ConditionFlag.TradeOpen])
            return "TradeOpen（交易中）";
        return null;
    }

    private void ObserveTeleportBusy()
    {
        if (_teleportGate.Observe(Environment.TickCount64, TeleportBusyReason()) is { } gone)
            GatherBuddy.Log.Information(
                $"[傳送等待] 這次傳送已經不需要了（目的地或流程變了），先前等了 {gone.WaitedMs / 1000.0:F1} 秒（{gone.Reason}）。");
    }

    private void ResetTeleportBusyGate()
    {
        if (_teleportGate.Reset() is { } gone)
            GatherBuddy.Log.Information($"[傳送等待] 自動採集已停用，取消等待（已等 {gone.WaitedMs / 1000.0:F1} 秒，{gone.Reason}）。");
    }

    /// <summary>回 true＝這一幀不要送傳送，呼叫端直接 return、下一幀再問。<paramref name="target"/> 只在寫 log 時用。</summary>
    private bool HoldTeleportForBusyPlayer(string what, string? target)
    {
        var d = _teleportGate.Check(Environment.TickCount64, TeleportBusyReason());
        if (d.Started)
            GatherBuddy.Log.Information(
                $"[傳送等待] 要{what}{FormatTarget(target)}，但{(d.BusyNow ? "你正在忙" : "你剛忙完")}：{d.Reason}；先不送，忙完再等 {TeleportBusyGate.QuietMs / 1000.0:F1} 秒。");
        else if (d.Released)
            GatherBuddy.Log.Information($"[傳送等待] 等了 {d.WaitedMs / 1000.0:F1} 秒（{d.Reason}），現在{what}{FormatTarget(target)}。");

        if (d.Hold)
            AutoStatus = d.BusyNow ? "Waiting until you are free to teleport...".Loc() : "Teleporting shortly...".Loc();
        return d.Hold;
    }

    private static string FormatTarget(string? target)
        => string.IsNullOrEmpty(target) ? string.Empty : $"「{target}」";
}
