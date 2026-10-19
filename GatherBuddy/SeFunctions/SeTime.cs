using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using GatherBuddy.Time;
using Action = System.Action;

namespace GatherBuddy.SeFunctions;

public class SeTime
{
    // 位址解不出時 GetServerTime()／Instance() 會擲例外（建構子裡就呼叫＝整個插件載入失敗）；改用本機時鐘代替伺服器時間。
    private static readonly bool ServerTimeAvailable = Framework.Addresses.GetServerTime.Value != 0;
    private static readonly bool FrameworkAvailable  = Framework.Addresses.Instance.Value != 0;

    private static TimeStamp GetServerTime()
        => ServerTimeAvailable
            ? new TimeStamp(Framework.GetServerTime() * 1000)
            : new TimeStamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1000);

    public TimeStamp ServerTime         { get; private set; }
    public TimeStamp EorzeaTime         { get; private set; }
    public long      EorzeaTotalMinute  { get; private set; }
    public long      EorzeaTotalHour    { get; private set; }
    public short     EorzeaMinuteOfDay  { get; private set; }
    public byte      EorzeaHourOfDay    { get; private set; }
    public byte      EorzeaMinuteOfHour { get; private set; }

    public event Action? Updated;
    public event Action? HourChanged;
    public event Action? WeatherChanged;

    public SeTime()
    {
        if (!ServerTimeAvailable || !FrameworkAvailable)
            GatherBuddy.Log.Warning(
                $"遊戲改版後 Framework 的位址對不上（GetServerTime {(ServerTimeAvailable ? "有" : "無")}、Instance {(FrameworkAvailable ? "有" : "無")}）：艾奧傑亞時間改用本機時鐘推算，可能差幾秒；其餘功能照常。");
        Update(null!);
        Dalamud.Framework.Update += Update;
    }

    public void Dispose()
        => Dalamud.Framework.Update -= Update;

    private unsafe TimeStamp GetEorzeaTime()
    {
        var framework = FrameworkAvailable ? Framework.Instance() : null;
        if (framework == null)
            return ServerTime.ConvertToEorzea();

        return Math.Abs(new TimeStamp(framework->UtcTime.Timestamp * 1000) - ServerTime) < 5000
            ? new TimeStamp(framework->ClientTime.EorzeaTime * 1000)
            : ServerTime.ConvertToEorzea();
    }

    private void Update(IFramework _)
    {
        ServerTime = GetServerTime();
        EorzeaTime = GetEorzeaTime();
        var minute = EorzeaTime.TotalMinutes;
        if (minute != EorzeaTotalMinute)
        {
            EorzeaTotalMinute  = minute;
            EorzeaMinuteOfDay  = (short)(EorzeaTotalMinute % RealTime.MinutesPerDay);
            EorzeaMinuteOfHour = (byte)(EorzeaMinuteOfDay % RealTime.MinutesPerHour);
        }

        var hour = EorzeaTotalMinute / RealTime.MinutesPerHour;
        if (hour != EorzeaTotalHour)
        {
            // Sometimes the Eorzea time gets seemingly rounded up and triggers before the ServerTime.
            //ServerTime      = ServerTime.AddEorzeaMinutes(30).SyncToEorzeaHour();
            EorzeaTotalHour = hour;
            EorzeaHourOfDay = (byte)(EorzeaMinuteOfDay / RealTime.MinutesPerHour);
            HourChanged?.Invoke();
            if ((EorzeaHourOfDay & 0b111) == 0)
            {
                GatherBuddy.Log.Verbose($"Eorzea Hour and Weather Change triggered. {(long)ServerTime} {(long)EorzeaTime}");
                WeatherChanged?.Invoke();
            }
            else
            {
                GatherBuddy.Log.Verbose($"Eorzea Hour Change triggered. {(long)ServerTime} {(long)EorzeaTime}");
            }
        }

        Updated?.Invoke();
    }
}
