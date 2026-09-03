using GatherBuddy.AutoGather.Movement;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    private AdvancedUnstuckCheckResult CheckAntiStuck(bool isPathGenerating, bool isPathing)
    {
        _antiStuckManager.SetContext(CurrentDestination, IsGathering);
        var result = _antiStuckManager.Tick(isPathGenerating, isPathing);

        // Applying a drastic action already changed the world state for this frame, so the
        // caller must abort the current tick instead of continuing with a stale decision.
        // (Upstream 5bc7d3f2b: without this the action kept re-triggering every frame.)
        if (_antiStuckManager.ShouldExecuteDrasticAction() && CanAct && !TaskManager.IsBusy
         && ExecuteAntiStuckDrasticAction())
            return AdvancedUnstuckCheckResult.Fail;

        return result;
    }

    private bool ExecuteAntiStuckDrasticAction()
    {
        StopNavigation();
        switch (_antiStuckManager.GetDrasticAction())
        {
            case AutoGatherConfig.PositionUnstuckAction.ForceUnstuck:
            {
                GatherBuddy.Log.Information("AntiStuck: 區域停滯逾時，強制執行一次隨機位移脫困。");
                _advancedUnstuck.Force();
                _antiStuckManager.MarkDrasticActionExecuted();
                return true;
            }
            case AutoGatherConfig.PositionUnstuckAction.StopAutoGather:
            {
                GatherBuddy.Log.Information("AntiStuck: 區域停滯逾時且近端復原無效，停止自動採集。");
                _antiStuckManager.MarkDrasticActionExecuted();
                // 這一條不經過 AbortAutoGather,所以 honk 與停止通知都得自己標記。
                // 卡住停下來正是最需要讓人知道的情境。
                MarkSelfStop("Stuck in the same area for too long.");
                Enabled = false;
                return true;
            }
        }

        return false;
    }
}
