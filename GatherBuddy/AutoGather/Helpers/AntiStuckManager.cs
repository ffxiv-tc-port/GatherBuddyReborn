using System;
using System.Numerics;
using ECommons.GameHelpers;
using GatherBuddy.AutoGather.Movement;

namespace GatherBuddy.AutoGather.Helpers;

/// <summary>
/// Supervises <see cref="AdvancedUnstuck"/>. The local recovery only knows about the last
/// couple of seconds, so it happily retries forever while the character makes no real
/// progress. This class watches the bigger picture: how often local recovery had to fire,
/// and whether the character actually left the area it was stuck in.
/// </summary>
public sealed class AntiStuckManager : IDisposable
{
    private readonly AdvancedUnstuck            _localRecovery;
    private readonly PositionStuckTracker       _areaTracker = new();
    private          Vector3                    _destination;
    private          bool                       _enabled;
    private          bool                       _isGathering;
    private          AdvancedUnstuckCheckResult _lastLocalResult;
    private          DateTime                   _lastDrasticAction = DateTime.MinValue;
    private          int                        _drasticActions;

    private AutoGatherConfig.AntiStuckConfig Config => GatherBuddy.Config.AutoGatherConfig.AntiStuck;

    public AntiStuckState State           { get; private set; }
    public int            ConsecutiveFails { get; private set; }

    public double TimeInArea                => _areaTracker.ElapsedSeconds;
    public int    DrasticActionsThisSession => _drasticActions;

    public double CooldownRemaining => State == AntiStuckState.Cooldown
        ? Math.Max(0, Config.DrasticCooldownSeconds - (DateTime.UtcNow - _lastDrasticAction).TotalSeconds)
        : 0;

    public AntiStuckManager(AdvancedUnstuck localRecovery)
        => _localRecovery = localRecovery;

    /// <summary>
    /// A "session" is one run of auto-gather, so the per-session drastic budget is
    /// replenished whenever the user turns auto-gather on again.
    /// </summary>
    public void OnSessionStart()
    {
        _drasticActions = 0;
        Reset("session start");
    }

    public void OnEnabledChanged(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
            OnSessionStart();
        else
            Reset("auto gather disabled");
    }

    public void SetContext(Vector3 destination, bool isGathering)
    {
        var wasGathering = _isGathering;
        _isGathering = isGathering;
        if (_isGathering)
        {
            // Reaching a gathering interaction is a confirmed recovery, so a later
            // obstruction must start a fresh failure chain.
            _destination = destination;
            if (!wasGathering)
                ResetProgress("gathering started");
            return;
        }

        if (destination == default)
        {
            _destination = default;
            // StopNavigation clears the destination during every local recovery.
            // Preserve the failure chain so escalation can actually be reached.
            _areaTracker.Reset();
            if (State != AntiStuckState.Cooldown)
                State = AntiStuckState.Normal;
            return;
        }

        _destination = destination;
    }

    public AdvancedUnstuckCheckResult Tick(bool isPathGenerating, bool isPathing)
    {
        if (!Config.Enabled)
        {
            Reset("anti-stuck disabled");
            return AdvancedUnstuckCheckResult.Pass;
        }

        UpdateEscalationState();
        if (!Config.LocalRecoveryEnabled)
            return AdvancedUnstuckCheckResult.Pass;

        var result = _localRecovery.Check(_destination, isPathGenerating, isPathing);
        if (result == AdvancedUnstuckCheckResult.Fail && _lastLocalResult != AdvancedUnstuckCheckResult.Fail)
        {
            ConsecutiveFails++;
            GatherBuddy.Log.Warning($"AntiStuck: 近端復原第 {ConsecutiveFails} 次觸發。");
        }

        _lastLocalResult = result;
        return result;
    }

    private void UpdateEscalationState()
    {
        if (State == AntiStuckState.Cooldown)
        {
            if (CooldownRemaining > 0)
                return;

            State = AntiStuckState.Normal;
        }

        if (!_enabled || !Config.EscalationEnabled || _isGathering || _destination == default)
        {
            if (State != AntiStuckState.Cooldown)
                State = AntiStuckState.Normal;
            _areaTracker.Reset();
            return;
        }

        if (ConsecutiveFails < Math.Max(1, Config.EscalationAfterFails))
        {
            State = AntiStuckState.Normal;
            _areaTracker.Reset();
            return;
        }

        if (!_areaTracker.IsTracking)
        {
            _areaTracker.Start(Player.Position);
            State = AntiStuckState.EscalationArmed;
            GatherBuddy.Log.Information("AntiStuck: 近端復原多次失敗，開始區域停滯倒數。");
            return;
        }

        if (_areaTracker.Update(Player.Position, Math.Max(5, Config.AreaRadius)))
        {
            ConsecutiveFails = 0;
            State            = AntiStuckState.Normal;
            GatherBuddy.Log.Information("AntiStuck: 偵測到有效位移，取消升級措施。");
            return;
        }

        if (_areaTracker.ElapsedSeconds >= Math.Max(30, Config.AreaTimeSeconds))
            State = AntiStuckState.DrasticActionReady;
    }

    public bool ShouldExecuteDrasticAction()
    {
        if (State != AntiStuckState.DrasticActionReady
         || Config.DrasticAction == AutoGatherConfig.PositionUnstuckAction.Off)
            return false;

        if (_drasticActions < Math.Max(1, Config.MaxDrasticPerSession))
            return true;

        State              = AntiStuckState.Cooldown;
        _lastDrasticAction = DateTime.UtcNow;
        GatherBuddy.Log.Information("AntiStuck: 已達本次自動採集的強制措施上限。");
        return false;
    }

    public AutoGatherConfig.PositionUnstuckAction GetDrasticAction()
        => Config.DrasticAction;

    public void MarkDrasticActionExecuted()
    {
        _drasticActions++;
        _lastDrasticAction = DateTime.UtcNow;
        State              = AntiStuckState.Cooldown;
        ConsecutiveFails   = 0;
        _lastLocalResult   = AdvancedUnstuckCheckResult.Pass;
        _areaTracker.Reset();
        GatherBuddy.Log.Information($"AntiStuck: 強制措施完成，冷卻 {Config.DrasticCooldownSeconds} 秒。");
    }

    private void ResetProgress(string reason)
    {
        ConsecutiveFails = 0;
        _lastLocalResult = AdvancedUnstuckCheckResult.Pass;
        _areaTracker.Reset();
        if (State != AntiStuckState.Cooldown)
            State = AntiStuckState.Normal;
        GatherBuddy.Log.Verbose($"AntiStuck: 進度重置 ({reason})。");
    }

    private void Reset(string reason)
    {
        State = AntiStuckState.Normal;
        ResetProgress(reason);
    }

    public void Dispose()
        => _areaTracker.Reset();
}
