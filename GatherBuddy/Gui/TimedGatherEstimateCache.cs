using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using ECommons.LanguageHelpers;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.Time;

namespace GatherBuddy.Gui;

// 自動採集清單每列「限時點還要多久採完」：純顯示，不做任何購買或市場動作。
// 只從繪製執行緒呼叫（沒有鎖）；剩餘數量或求解器產量有變才重算字串，其餘幀只查字典。
internal static class TimedGatherEstimateCache
{
    // 本次載入還沒有任何求解器結果時的每窗產量，列上會標「估」。
    private const double DefaultYield = 20;

    private sealed class Entry
    {
        public int     Remaining = int.MinValue;
        public int     Version   = -1;
        public string? Text;
        public string  Tooltip = string.Empty;
    }

    private static readonly Dictionary<IGatherable, Entry>                       Rows    = new();
    private static readonly Dictionary<Gatherable, (int Windows, string? Reason)> Windows = new();

    public static void Draw(IGatherable item, int remaining)
    {
        if (!Rows.TryGetValue(item, out var entry))
            Rows[item] = entry = new Entry();

        var version = TimedYieldTracker.Version;
        if (entry.Remaining != remaining || entry.Version != version)
            Recompute(item, remaining, version, entry);

        if (entry.Text == null)
            return;

        ImGui.SameLine();
        ImGui.TextDisabled(entry.Text);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(entry.Tooltip + NextWindowLine(item as Gatherable));
    }

    private static void Recompute(IGatherable item, int remaining, int version, Entry entry)
    {
        entry.Remaining = remaining;
        entry.Version   = version;
        entry.Text      = null;
        if (remaining <= 0 || item is not Gatherable gatherable || gatherable.NodeType == NodeType.Regular)
            return;

        var (windows, reason) = GetWindows(gatherable);
        if (windows <= 0)
        {
            entry.Text    = "?";
            entry.Tooltip = reason ?? string.Empty;
            return;
        }

        var source = TimedYieldTracker.TryGet(gatherable.ItemId, out var yieldPerWindow);
        if (source == TimedYieldTracker.Source.None)
            yieldPerWindow = DefaultYield;

        var result = TimedGatherMath.Estimate(remaining, yieldPerWindow, windows);
        if (!result.Valid)
        {
            entry.Text    = "?";
            entry.Tooltip = "Could not estimate the gathering time for this item.".Loc();
            return;
        }

        var hour     = " " + "hours".Loc();
        var minute   = " " + "minutes".Loc();
        var duration = TimedGatherMath.FormatDuration(result.Seconds, hour, minute);
        entry.Text = source == TimedYieldTracker.Source.SameItem
            ? "About ?? windows / ??".Loc(result.Rounds, duration)
            : "About ?? windows / ?? (rough)".Loc(result.Rounds, duration);

        var sourceText = source switch
        {
            TimedYieldTracker.Source.SameItem   => "rotation solver estimate for this item, this session".Loc(),
            TimedYieldTracker.Source.OtherItems => "average of other timed items the rotation solver handled this session".Loc(),
            _                                   => "default guess - the rotation solver has not run on a timed node yet this session".Loc(),
        };
        entry.Tooltip = "Timed node: ?? window(s) per Eorzean day, one every ?? on average.".Loc(windows,
                TimedGatherMath.FormatDuration(TimedGatherMath.EorzeaDaySeconds / windows, hour, minute))
          + "\n" + "Yield per window: about ?? (??).".Loc(yieldPerWindow.ToString("0.#"), sourceText)
          + "\n" + "Still need ?? -> about ?? window(s), ??.".Loc(remaining, result.Rounds, duration)
          + "\n" + "Flying and waiting for the first window are not included. Display only.".Loc();
    }

    private static (int Windows, string? Reason) GetWindows(Gatherable gatherable)
    {
        if (Windows.TryGetValue(gatherable, out var cached))
            return cached;

        var windows = 0;
        string? reason = null;
        if (gatherable.NodeList.Count == 0)
            reason = "No gathering point data for this item.".Loc();
        foreach (var node in gatherable.NodeList)
        {
            if (node.NodeType == NodeType.Ephemeral)
            {
                windows = 0;
                reason  = "Ephemeral nodes can be gathered several times per window, so the yield per window cannot be estimated.".Loc();
                break;
            }

            var times = node.Times;
            windows += TimedGatherMath.CountWindows(times.IsUp);
        }

        if (windows == 0)
            reason ??= "This item's node has no usable spawn times.".Loc();

        return Windows[gatherable] = (windows, reason);
    }

    private static string NextWindowLine(Gatherable? gatherable)
    {
        if (gatherable == null || gatherable.NodeList.Count == 0)
            return string.Empty;

        var now  = GatherBuddy.Time.ServerTime;
        var best = TimeInterval.Never;
        foreach (var node in gatherable.NodeList)
        {
            var next = node.Times.NextUptime(now);
            if (next != TimeInterval.Never && (best == TimeInterval.Never || next.Start < best.Start))
                best = next;
        }

        if (best == TimeInterval.Never)
            return string.Empty;

        return "\n" + (best.Start <= now
            ? "Up now, ?? left.".Loc(TimeInterval.DurationString(best.End, now, false))
            : "Next window in ??.".Loc(TimeInterval.DurationString(best.Start, now, false)));
    }
}
