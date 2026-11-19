using System;
using System.Globalization;

namespace GatherBuddy.AutoGather.Helpers;

// 純計算，不碰 Dalamud 與遊戲狀態（離線探針直接編譯這個檔）。
internal static class TimedGatherMath
{
    // 一個艾奧傑亞日＝24 艾奧傑亞時 × 175 秒＝現實 70 分鐘。
    public const double EorzeaDaySeconds = 24 * 175;

    public readonly record struct Result(bool Valid, int Rounds, double Seconds);

    // 時間＝窗數 × 平均窗距；不含飛行、也不含等第一窗開的時間。每窗不到 1 個視為算不出來。
    public static Result Estimate(int remaining, double yieldPerWindow, int windowsPerEorzeaDay)
    {
        if (remaining <= 0 || windowsPerEorzeaDay <= 0 || !(yieldPerWindow >= 1) || double.IsInfinity(yieldPerWindow))
            return new Result(false, 0, 0);

        var rounds = (int)Math.Ceiling(remaining / yieldPerWindow);
        return new Result(true, rounds, rounds * EorzeaDaySeconds / windowsPerEorzeaDay);
    }

    // 數「開窗」的次數：某小時開著而前一小時（跨午夜環回）關著。全天開著回 0，交給呼叫端當一般點。
    public static int CountWindows(Func<uint, bool> isUpAtEorzeaHour)
    {
        var windows = 0;
        for (var hour = 0u; hour < 24; ++hour)
        {
            if (isUpAtEorzeaHour(hour) && !isUpAtEorzeaHour((hour + 23) % 24))
                ++windows;
        }

        return windows;
    }

    public static string FormatDuration(double seconds, string hourUnit, string minuteUnit)
        => seconds >= 3600
            ? (seconds / 3600).ToString("0.0", CultureInfo.InvariantCulture) + hourUnit
            : Math.Max(1, (int)Math.Round(seconds / 60)).ToString(CultureInfo.InvariantCulture) + minuteUnit;
}
