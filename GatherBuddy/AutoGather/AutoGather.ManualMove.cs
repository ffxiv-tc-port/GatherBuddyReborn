using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using GatherBuddy.AutoGather.Movement;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    private readonly ManualMoveYield _manualMove = new();
    private          Vector3         _manualMoveDestination;
    private          bool            _manualMoveHeld;

    // 與 AdvancedUnstuck 的「已到目的地附近」同一個距離：那以內本來就不重送、不算卡住。
    private const float ManualMoveNearDestination = 3.5f;

    /// <summary>
    /// 使用者手動移動讓 vnavmesh 取消路徑（CancelMoveOnUserInput）時，不在下一幀就重送、也不把「沒在導航」當成卡住。
    /// 回 true＝這一輪 DoAutoGather 到此為止。只在 framework 執行緒（DoAutoGather）呼叫。
    /// </summary>
    private bool YieldToManualMovement(bool isPathGenerating, bool isPathing)
    {
        var destination = CurrentDestination;
        var player      = Player.Position;
        if (destination == default
         || IsGathering
         || _advancedUnstuck.IsRunning
         || Vector2.Distance(new Vector2(destination.X, destination.Z), new Vector2(player.X, player.Z)) < ManualMoveNearDestination)
        {
            _manualMove.Reset();
            _manualMoveDestination = default;
            EndManualMoveHold();
            return false;
        }

        if (destination != _manualMoveDestination)
        {
            _manualMove.Reset();
            _manualMoveDestination = destination;
        }

        var busy = Dalamud.Conditions[ConditionFlag.Mounting]
         || Dalamud.Conditions[ConditionFlag.Mounting71]
         || Dalamud.Conditions[ConditionFlag.Jumping]
         || Dalamud.Conditions[ConditionFlag.Casting]
         || Dalamud.Conditions[ConditionFlag.BetweenAreas]
         || Dalamud.Conditions[ConditionFlag.BetweenAreas51];
        var step = _manualMove.Observe(player, isPathing || isPathGenerating, busy, Environment.TickCount64);
        switch (step)
        {
            case ManualMoveYield.Step.Started:
                GatherBuddy.Log.Information(
                    $"[自動採集讓位] 導航停了、角色以 {_manualMove.LastSpeed:F1} 米/秒持續移動 {ManualMoveYield.ConfirmMs / 1000f:F1} 秒：判定你在手動移動，暫停重送路徑與脫困；"
                  + $"角色靜止 {ManualMoveYield.ResumeQuietMs / 1000f:F1} 秒後從目前位置接續，單次讓位上限 {ManualMoveYield.MaxYieldMs / 1000} 秒。目的地 {destination:F1}");
                break;
            case ManualMoveYield.Step.Resumed:
                GatherBuddy.Log.Information($"[自動採集讓位] 角色已靜止，從目前位置 {player:F1} 接續前往 {destination:F1}。");
                break;
            case ManualMoveYield.Step.Expired:
                GatherBuddy.Log.Information(
                    $"[自動採集讓位] 讓位超過 {ManualMoveYield.MaxYieldMs / 1000} 秒：前往 {destination:F1} 的這一趟之後不再讓位，照原本的重送與脫困規則。");
                break;
        }

        if (step is ManualMoveYield.Step.Hold or ManualMoveYield.Step.Started)
        {
            _manualMoveHeld = true;
            return true;
        }

        EndManualMoveHold();
        return false;
    }

    private void EndManualMoveHold()
    {
        if (!_manualMoveHeld)
            return;

        _manualMoveHeld = false;
        _advancedUnstuck.ResetTracking();
    }
}
