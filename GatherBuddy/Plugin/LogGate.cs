using Serilog.Events;

namespace GatherBuddy.Plugin;

/// <summary>
///     Dalamud 根層記錄等級的閘門：每幀路徑上的 <c>Svc.Log.Verbose</c>／<c>Svc.Log.Debug</c> 先判這裡，
///     關著時連字串內插都不做。每次即時讀，不可快取（使用者會在遊戲裡切等級）。
/// </summary>
/// <remarks>
///     不能改用 <c>Svc.Log.MinimumLogLevel</c>：ECommons 把插件層設成 Verbose，它恆真。
///     <c>GatherBuddy.Log</c>（OtterGui）本來就判根層，不需要這個閘門。
/// </remarks>
internal static class LogGate
{
    public static bool VerboseEnabled
        => global::Dalamud.EntryPoint.LogLevelSwitch.MinimumLevel <= LogEventLevel.Verbose;

    public static bool DebugEnabled
        => global::Dalamud.EntryPoint.LogLevelSwitch.MinimumLevel <= LogEventLevel.Debug;
}
