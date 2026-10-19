using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using ECommons.LanguageHelpers;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Gui;
using GatherBuddy.Interfaces;
using GatherBuddy.Time;
using Dalamud.Bindings.ImGui;
using OtterGui;
using Functions = GatherBuddy.Plugin.Functions;
using ImRaii = OtterGui.Raii.ImRaii;

namespace GatherBuddy.GatherHelper;

public class GatherWindow : Window
{
    private readonly GatherBuddy _plugin;

    private int       _deleteSet              = -1;
    private int       _deleteItemIdx          = -1;
    private bool      _deleteAutoGather;
    private TimeStamp _earliestKeyboardToggle = TimeStamp.Epoch;
    private Vector2   _lastSize               = Vector2.Zero;
    private Vector2   _newPosition            = Vector2.Zero;

    public GatherWindow(GatherBuddy plugin)
        : base("##GatherHelperReborn",
            ImGuiWindowFlags.AlwaysAutoResize
          | ImGuiWindowFlags.NoTitleBar
          | ImGuiWindowFlags.NoFocusOnAppearing
          | ImGuiWindowFlags.NoNavFocus
          | ImGuiWindowFlags.NoScrollbar)
    {
        _plugin            = plugin;
        _byUptime          = CompareByUptime;
        IsOpen             = GatherBuddy.Config.ShowGatherWindow;
        RespectCloseHotkey = false;
        Namespace          = "GatherHelperReborn";
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = Vector2.Zero,
            MaximumSize = Vector2.One * 10000,
        };
    }

    private static void DrawTime(ILocation? loc, TimeInterval time)
    {
        if (!GatherBuddy.Config.ShowGatherWindowTimers || !ImGui.TableNextColumn())
            return;
        if (time.Equals(TimeInterval.Always))
            return;

        if (time.Start > GatherBuddy.Time.ServerTime)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, ColorId.UpcomingItem.Value());
            ImGui.Text(TimeInterval.DurationString(time.Start, GatherBuddy.Time.ServerTime, false));
        }
        else
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, ColorId.AvailableItem.Value());
            ImGui.Text(TimeInterval.DurationString(time.End, GatherBuddy.Time.ServerTime, false));
        }

        CreateTooltip(null, loc, time);
    }

    private static string TooltipText(ILocation? loc, TimeInterval time)
    {
        var sb = new StringBuilder();
        sb.Append(loc == null
            ? "Unknown Location\nUnknown Territory\nUnknown Aetheryte\n"
            : $"{loc.Name}\n{loc.Territory.Name}\n{loc.ClosestAetheryte?.Name ?? "No Aetheryte"}\n");

        sb.Append(time.Equals(TimeInterval.Always)
            ? "Always Up"
            : $"{time.Start}\n{time.End}\n{time.DurationString()}\n{TimeInterval.DurationString(time.Start > GatherBuddy.Time.ServerTime ? time.Start : time.End, GatherBuddy.Time.ServerTime, false)}");

        return sb.ToString();
    }

    private static void CreateTooltip(IGatherable? item, ILocation? loc, TimeInterval time)
    {
        if (!ImGui.IsItemHovered())
            return;

        if (item is not Fish fish)
        {
            ImGui.SetTooltip(TooltipText(loc, time));
            return;
        }

        var extendedFish = Interface.ExtendedFishList.FirstOrDefault(f => f.Data == fish);
        if (extendedFish == null)
        {
            ImGui.SetTooltip(TooltipText(loc, time));
            return;
        }

        using var tt = ImRaii.Tooltip();

        ImGui.Text(TooltipText(loc, time));
        ImGui.NewLine();
        extendedFish.SetTooltip(loc?.Territory ?? Territory.Invalid, ImGuiHelpers.ScaledVector2(40, 40), ImGuiHelpers.ScaledVector2(20, 20),
            ImGuiHelpers.ScaledVector2(30,                                                          30),
            false);
    }

    private readonly List<(IGatherable Item, ILocation Location, TimeInterval Uptime, uint Quantity)> _data = new();

    // CheckAvailable 每幀重用：品項在 _data 裡的位置、自動採集清單裡已經見過的（品項, 數量）、排序用的比較委派。
    private readonly Dictionary<IGatherable, int>                                                           _groupIndex     = new();
    private readonly HashSet<(IGatherable Item, uint Quantity)>                                             _seenAutoGather = new();
    private readonly Comparison<(IGatherable Item, ILocation Location, TimeInterval Uptime, uint Quantity)> _byUptime;

    private void DrawItem(IGatherable item, ILocation loc, TimeInterval time, uint quantity)
    {
        if (GatherBuddy.Config.ShowGatherWindowOnlyAvailable && time.Start > GatherBuddy.Time.ServerTime)
            return;

        var inventoryCount = item.GetInventoryCount();

        if (quantity > 0 && inventoryCount >= quantity && GatherBuddy.Config.HideGatherWindowCompletedItems)
            return;

        if (ImGui.TableNextColumn())
        {
            using var style = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, ImGui.GetStyle().ItemSpacing / 2);
            if (Icons.DefaultStorage.TryLoadIcon(item.ItemData.Icon, out var icon))
                ImGuiUtil.HoverIcon(icon.Handle, icon.Size, new Vector2(ImGui.GetTextLineHeight()));
            else
                ImGui.Dummy(new Vector2(ImGui.GetTextLineHeight()));
            ImGui.SameLine();
            var colorId = time == TimeInterval.Always    ? ColorId.GatherWindowText :
                time.Start > GatherBuddy.Time.ServerTime ? ColorId.GatherWindowUpcoming : ColorId.GatherWindowAvailable;

            if (quantity > 0 && inventoryCount >= quantity)
                colorId = ColorId.DisabledText;
            using var color                         = ImRaii.PushColor(ImGuiCol.Text, colorId.Value());
            var quantityText = quantity > 0 ? $" ({inventoryCount}/{quantity})" : "";
            if (ImGui.Selectable($"{item.Name[GatherBuddy.Language]}{quantityText}", false))
            {
                if (_plugin.Executor.LastItem != item)
                    _plugin.Executor.GatherItem(item);
                else if (item is Gatherable)
                    _plugin.Executor.GatherItemByName("next");
                else
                    _plugin.Executor.GatherFishByName("next");
            }

            var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
            color.Pop();
            CreateTooltip(item, loc, time);

            if (clicked && Functions.CheckModifier(GatherBuddy.Config.GatherWindowDeleteModifier, false))
                if (quantity > 0)
                    for (var i = 0; i < _plugin.AutoGatherListsManager.Lists.Count; ++i)
                    {
                        var list = _plugin.AutoGatherListsManager.Lists[i];
                        if (!list.Enabled)
                            continue;

                        if (item is not Gatherable gatherable)
                            continue;
                        var idx = list.Items.IndexOf(gatherable);
                        if (idx < 0)
                            continue;

                        _deleteSet = i;
                        _deleteItemIdx = idx;
                        _deleteAutoGather = true;
                        break;
                    }
                else
                    for (var i = 0; i < _plugin.GatherWindowManager.Presets.Count; ++i)
                    {
                        var preset = _plugin.GatherWindowManager.Presets[i];
                        if (!preset.Enabled)
                            continue;

                        var idx = preset.Items.IndexOf(item);
                        if (idx < 0)
                            continue;

                        _deleteSet = i;
                        _deleteItemIdx = idx;
                        _deleteAutoGather = false;
                        break;
                    }
            else
                Interface.CreateGatherWindowContextMenu(item, clicked);
        }

        DrawTime(loc, time);
    }

    private void DeleteItem()
    {
        if (_deleteSet < 0 || _deleteItemIdx < 0)
            return;

        if (_deleteAutoGather)
        {
            var list = _plugin.AutoGatherListsManager.Lists[_deleteSet];
            _plugin.AutoGatherListsManager.RemoveItem(list, _deleteItemIdx);
        }
        else
        {
            var preset = _plugin.GatherWindowManager.Presets[_deleteSet];
            _plugin.GatherWindowManager.RemoveItem(preset, _deleteItemIdx);
        }
        _deleteSet     = -1;
        _deleteItemIdx = -1;
    }

    private void CheckHotkeys()
    {
        if (_earliestKeyboardToggle > GatherBuddy.Time.ServerTime || !Functions.CheckKeyState(GatherBuddy.Config.GatherWindowHotkey, false))
            return;

        _earliestKeyboardToggle             = GatherBuddy.Time.ServerTime.AddMilliseconds(500);
        GatherBuddy.Config.ShowGatherWindow = !GatherBuddy.Config.ShowGatherWindow;
        GatherBuddy.Config.Save();
    }

    private static bool CheckHoldKey()
    {
        if (!GatherBuddy.Config.OnlyShowGatherWindowHoldingKey || GatherBuddy.Config.GatherWindowHoldKey == VirtualKey.NO_KEY)
            return false;

        return !Dalamud.Keys[GatherBuddy.Config.GatherWindowHoldKey];
    }

    private static bool CheckDuty()
        => GatherBuddy.Config.HideGatherWindowInDuty && Functions.BoundByDuty();

    // 每幀都會跑（視窗最後不畫也一樣），所以三個集合都重用、不走 LINQ。
    // 結果要和原本的 Union→Concat→GroupBy→Sum→OrderBy 逐筆相同，順序＝品項第一次出現的順序。
    private bool CheckAvailable()
    {
        _data.Clear();
        _groupIndex.Clear();
        _seenAutoGather.Clear();

        // 自動採集清單（採集物＋魚）裡完全相同的（品項, 數量）只算一次；採集視窗預設組的品項不去重、數量記 0。
        var activeItems = _plugin.AutoGatherListsManager.ActiveItems;
        for (var i = 0; i < activeItems.Count; ++i)
        {
            var (item, quantity) = activeItems[i];
            if (_seenAutoGather.Add((item, quantity)))
                AddQuantity(item, quantity);
        }

        var activeFish = _plugin.AutoGatherListsManager.ActiveFish;
        for (var i = 0; i < activeFish.Count; ++i)
        {
            var (fish, quantity) = activeFish[i];
            if (_seenAutoGather.Add((fish, quantity)))
                AddQuantity(fish, quantity);
        }

        var windowItems = _plugin.GatherWindowManager.ActiveItems;
        for (var i = 0; i < windowItems.Count; ++i)
            AddQuantity(windowItems[i], 0);

        // BestLocation 會更新快取並發 UptimeChange：等分組做完才照分組順序各呼叫一次。
        for (var i = 0; i < _data.Count; ++i)
        {
            var entry = _data[i];
            var (loc, time) = GatherBuddy.UptimeManager.BestLocation(entry.Item);
            _data[i] = (entry.Item, loc, time, entry.Quantity);
        }

        if (GatherBuddy.Config.SortGatherWindowByUptime)
            _data.Sort(_byUptime);

        if (_data.Count == 0)
            return true;
        if (!GatherBuddy.Config.ShowGatherWindowOnlyAvailable)
            return false;

        foreach (var entry in _data)
        {
            if (!(entry.Uptime.Start > GatherBuddy.Time.ServerTime))
                return false;
        }

        return true;
    }

    // 數量照原本的「加總成 long 再轉 uint」處理：超過 uint 上限時截斷，不擲例外。
    private void AddQuantity(IGatherable item, uint quantity)
    {
        if (_groupIndex.TryGetValue(item, out var idx))
        {
            var entry = _data[idx];
            _data[idx] = (entry.Item, entry.Location, entry.Uptime, unchecked(entry.Quantity + quantity));
            return;
        }

        _groupIndex.Add(item, _data.Count);
        _data.Add((item, null!, default, quantity));
    }

    // 時間相同時照分組順序排，結果等同穩定排序；_groupIndex 記的是排序前的位置，排序途中不更新。
    private int CompareByUptime((IGatherable Item, ILocation Location, TimeInterval Uptime, uint Quantity) x,
        (IGatherable Item, ILocation Location, TimeInterval Uptime, uint Quantity) y)
    {
        var c = x.Uptime.Compare(y.Uptime);
        return c != 0 ? c : _groupIndex[x.Item].CompareTo(_groupIndex[y.Item]);
    }

    public override void PreOpenCheck()
    {
        CheckHotkeys();
        IsOpen = GatherBuddy.Config.ShowGatherWindow;
    }

    public override bool DrawConditions()
        => !(CheckHoldKey() || CheckDuty() || CheckAvailable()) && Dalamud.ClientState.IsLoggedIn;

    public override void PreDraw()
    {
        // Dalamud 的每視窗不透明度是在 Window.PreDraw() 裡 push 的,不呼叫 base 會讓右鍵選單的「不透明度」滑桿靜默失效。
        base.PreDraw();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, ColorId.GatherWindowBackground.Value());
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.One * 2 * ImGuiHelpers.GlobalScale);
        if (GatherBuddy.Config.LockGatherWindow)
            Flags |= ImGuiWindowFlags.NoMove;
        else
            Flags &= ~
                ImGuiWindowFlags.NoMove;

        if (_newPosition.Y != 0)
        {
            ImGui.SetNextWindowPos(_newPosition);
            _newPosition = Vector2.Zero;
        }
    }

    public override void PostDraw()
    {
        DeleteItem();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        // 後進先出:本類別在 PreDraw 推的樣式先 pop 完,最後才讓 base 收掉它自己推的那一個。
        base.PostDraw();
    }

    private void CheckAnchorPosition()
    {
        if (!GatherBuddy.Config.GatherWindowBottomAnchor)
            return;

        // Can not use Y size since a single text row is smaller than the minimal window size
        // for some reason. 50 is arbitrary. Default window size was 32,32 for me.
        if (_lastSize.X < 50 * ImGuiHelpers.GlobalScale)
            _lastSize = ImGui.GetWindowSize();

        var size = ImGui.GetWindowSize();
        if (_lastSize == size)
            return;

        _newPosition   =  ImGui.GetWindowPos();
        _newPosition.Y += _lastSize.Y - size.Y;
        _lastSize      =  size;
    }

    public override void Draw()
    {
        var       colorId = GatherBuddy.AutoGather.Enabled ? ColorId.GatherWindowAvailable.Value() : ColorId.GatherWindowText.Value();
        using var color = ImRaii.PushColor(ImGuiCol.Text, colorId);
        if (ImGui.Selectable($"Auto-Gather: {GatherBuddy.AutoGather.AutoStatus}###toggle-button"))
        {
            GatherBuddy.AutoGather.Enabled = !GatherBuddy.AutoGather.Enabled;
        }
        color.Pop();
        ImGuiUtil.HoverTooltip("Click to enable/disable auto-gather".Loc());
        using var table = ImRaii.Table("##table", GatherBuddy.Config.ShowGatherWindowTimers ? 2 : 1);
        if (!table)
            return;
        
        foreach (var (item, loc, time, quantity) in _data)
            DrawItem(item, loc, time, quantity);

        CheckAnchorPosition();
    }
}
