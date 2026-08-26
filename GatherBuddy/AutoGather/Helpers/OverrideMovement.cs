using Dalamud.Game.Config;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.Logging;
using ECommons.MathHelpers;

//Credit: https://github.com/NightmareXIV/Lifestream/blob/main/Lifestream/Movement/OverrideMovement.cs#7ad417a
namespace GatherBuddy.AutoGather.Movement;

[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public unsafe struct PlayerMoveControllerFlyInput
{
    [FieldOffset(0x0)] public float Forward;
    [FieldOffset(0x4)] public float Left;
    [FieldOffset(0x8)] public float Up;
    [FieldOffset(0xC)] public float Turn;
    [FieldOffset(0x10)] public float u10;
    [FieldOffset(0x14)] public byte DirMode;
    [FieldOffset(0x15)] public byte HaveBackwardOrStrafe;
}

// NOTE: the old hand-rolled `CameraEx` struct is gone on purpose (same fix as vnavmesh/Lifestream on
// TC 7.20). Its 0x130-based FieldOffsets are the pre-7.20 layout — TC 7.20 shifted the native struct
// +0x10, so DirH at 0x130 now reads FoV, which sent legacy-mode steering in a garbage direction.
// GetActiveCamera() already returns FFXIVClientStructs.FFXIV.Client.Game.Camera*, which carries the
// current layout, so use it directly.

public unsafe class OverrideMovement : IDisposable
{
    public bool Enabled
    {
        get => _rmiWalkHook?.IsEnabled ?? false;
        set
        {
            if (value)
            {
                _rmiWalkHook?.Enable();
                _rmiFlyHook?.Enable();
            }
            else
            {
                _rmiWalkHook?.Disable();
                _rmiFlyHook?.Disable();
            }
        }
    }

    public bool IgnoreUserInput; // if true - override even if user tries to change camera orientation, otherwise override only if user does nothing
    public Vector3 DesiredPosition;
    public float Precision = 0.01f;

    private bool _legacyMode;

    // Fallible on purpose: a signature that stops matching after a game patch has to degrade to
    // "auto-gather movement assist is off", not throw out of the ctor. This object is a field
    // initializer on AdvancedUnstuck (`new()`), so an exception here propagates out of construction.
    private delegate void RMIWalkDelegate(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);
    [Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D", Fallibility = Fallibility.Fallible)]
    private Hook<RMIWalkDelegate>? _rmiWalkHook;

    private delegate void RMIFlyDelegate(void* self, PlayerMoveControllerFlyInput* result);
    [Signature("E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8", Fallibility = Fallibility.Fallible)]
    private Hook<RMIFlyDelegate>? _rmiFlyHook;

    public OverrideMovement()
    {
        Svc.Hook.InitializeFromAttributes(this);
        if (_rmiWalkHook != null)
            GatherBuddy.Log.Information($"RMIWalk address: 0x{_rmiWalkHook.Address:X}");
        else
            GatherBuddy.Log.Error("RMIWalk signature not found - walk movement override disabled");
        if (_rmiFlyHook != null)
            GatherBuddy.Log.Information($"RMIFly address: 0x{_rmiFlyHook.Address:X}");
        else
            GatherBuddy.Log.Error("RMIFly signature not found - fly movement override disabled");
        Svc.GameConfig.UiControlChanged += OnConfigChanged;
        UpdateLegacyMode();
    }

    public void Dispose()
    {
        Svc.GameConfig.UiControlChanged -= OnConfigChanged;
        _rmiWalkHook?.Dispose();
        _rmiFlyHook?.Dispose();
    }

    // fail-closed: a detour is a managed function the *native* code calls directly, so a managed
    // exception escaping it unwinds through native frames that have no handler for it. Everything we
    // add on top of Original() therefore runs inside a try, and the degraded behaviour is "don't
    // override" - Original has already run, so the player's own movement input passes through intact.
    // NOTE: this does NOT protect against AccessViolationException (corrupted-state, uncatchable in
    // .NET Core). What it catches is managed exceptions - most importantly the
    // InvalidOperationException that ClientStructs' [StaticAddress]/[MemberFunction] members throw
    // when their signature stops resolving after a game patch.
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(Exception ex)
    {
        ++_detourErrors;
        // this runs per frame - never log unthrottled. Information (not Debug) because reporting
        // users run at LogLevel 2.
        var now = DateTime.UtcNow;
        if (now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        GatherBuddy.Log.Information($"OverrideMovement: movement override threw, leaving the game's own movement input alone (total {_detourErrors}): {ex}");
    }

    private void RMIWalkDetour(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        _rmiWalkHook!.OriginalDisposeSafe(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
        try
        {
            // TODO: we really need to introduce some extra checks that PlayerMoveController::readInput does - sometimes it skips reading input, and returning something non-zero breaks stuff...
            bool movementAllowed = bAdditiveUnk == 0 && !Svc.Condition[ConditionFlag.BeingMoved];
            if (movementAllowed && (IgnoreUserInput || *sumLeft == 0 && *sumForward == 0) && DirectionToDestination(false) is var relDir && relDir != null)
            {
                var dir = relDir.Value.h.ToDirection();
                *sumLeft = dir.X;
                *sumForward = dir.Y;
            }
        }
        catch (Exception ex)
        {
            OnDetourError(ex);
        }
    }

    private void RMIFlyDetour(void* self, PlayerMoveControllerFlyInput* result)
    {
        _rmiFlyHook!.OriginalDisposeSafe(self, result);
        try
        {
            // TODO: we really need to introduce some extra checks that PlayerMoveController::readInput does - sometimes it skips reading input, and returning something non-zero breaks stuff...
            if ((IgnoreUserInput || result->Forward == 0 && result->Left == 0 && result->Up == 0) && DirectionToDestination(true) is var relDir && relDir != null)
            {
                var dir = relDir.Value.h.ToDirection();
                result->Forward = dir.Y;
                result->Left = dir.X;
                result->Up = relDir.Value.v.Rad;
            }
        }
        catch (Exception ex)
        {
            OnDetourError(ex);
        }
    }

    private (Angle h, Angle v)? DirectionToDestination(bool allowVertical)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
            return null;

        var dist = DesiredPosition - player.Position;
        if (dist.LengthSquared() <= Precision * Precision)
            return null;

        var dirH = Angle.FromDirectionXZ(dist);
        var dirV = allowVertical ? Angle.FromDirection(new(dist.Y, new Vector2(dist.X, dist.Z).Length())) : default;

        var activeCamera = _legacyMode ? TryGetActiveCamera() : null;
        var refDir = activeCamera != null
            ? activeCamera->DirH.Radians() + 180.Degrees()
            : player.Rotation.Radians();
        return (dirH - refDir, dirV);
    }

    // CameraManager.GetActiveCamera() is a ClientStructs [MemberFunction], and CameraManager.Instance()
    // just forwards to Control.Instance(), a [StaticAddress]. When either signature stops resolving
    // they *throw* InvalidOperationException (InteropGenerator's ThrowHelper.ThrowNullAddress) instead
    // of returning null - so `CameraManager.Instance() != null` was never a guard against a broken
    // signature. This path is reached from the RMIWalk/RMIFly detours, i.e. it would be a managed
    // exception thrown inside a detour on every single frame. Check the resolved addresses up front
    // and skip the whole camera-reference path instead; legacy mode then falls back to the
    // character's own facing (steering is wrong-ish rather than fatal).
    private static bool CameraApiResolved
        => FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Addresses.Instance.Value != 0
        && CameraManager.Addresses.GetActiveCamera.Value != 0;

    private static FFXIVClientStructs.FFXIV.Client.Game.Camera* TryGetActiveCamera()
    {
        if (!CameraApiResolved)
            return null;
        var mgr = CameraManager.Instance();
        return mgr != null ? mgr->GetActiveCamera() : null;
    }

    private void OnConfigChanged(object? sender, ConfigChangeEvent evt) => UpdateLegacyMode();
    private void UpdateLegacyMode()
    {
        _legacyMode = Svc.GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1;
        GatherBuddy.Log.Debug($"Legacy mode is now {(_legacyMode ? "enabled" : "disabled")}");
    }
}