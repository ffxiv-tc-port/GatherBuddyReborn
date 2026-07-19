using System;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using GatherBuddy.Alarms;
using GatherBuddy.Config;
using GatherBuddy.GatherHelper;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using ImGuiNET;
using OtterGui;
using ImRaii = OtterGui.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private class GatherWindowDragDropData
    {
        public GatherWindowPreset Preset;
        public IGatherable        Item;
        public int                ItemIdx;

        public GatherWindowDragDropData(GatherWindowPreset preset, IGatherable item, int idx)
        {
            Preset  = preset;
            Item    = item;
            ItemIdx = idx;
        }
    }

    private class GatherWindowCache
    {
        public class GatherWindowSelector : ItemSelector<GatherWindowPreset>
        {
            public GatherWindowSelector()
                : base(_plugin.GatherWindowManager.Presets, Flags.All)
            { }

            protected override bool Filtered(int idx)
                => Filter.Length != 0 && !Items[idx].Name.Contains(Filter, StringComparison.InvariantCultureIgnoreCase);

            protected override bool OnDraw(int idx)
            {
                using var id    = ImRaii.PushId(idx);
                using var color = ImRaii.PushColor(ImGuiCol.Text, ColorId.DisabledText.Value(), !Items[idx].Enabled);
                return ImGui.Selectable(CheckUnnamed(Items[idx].Name), idx == CurrentIdx);
            }

            protected override bool OnDelete(int idx)
            {
                _plugin.GatherWindowManager.DeletePreset(idx);
                return true;
            }

            protected override bool OnAdd(string name)
            {
                _plugin.GatherWindowManager.AddPreset(new GatherWindowPreset()
                {
                    Name = name,
                });
                return true;
            }

            protected override bool OnClipboardImport(string name, string data)
            {
                if (!GatherWindowPreset.Config.FromBase64(data, out var cfg))
                    return false;

                GatherWindowPreset.FromConfig(cfg, out var preset);
                preset.Name = name;
                _plugin.GatherWindowManager.AddPreset(preset);
                return true;
            }

            protected override bool OnDuplicate(string name, int idx)
            {
                if (Items.Count <= idx || idx < 0)
                    return false;

                var preset = _plugin.GatherWindowManager.Presets[idx].Clone();
                preset.Name = name;
                _plugin.GatherWindowManager.AddPreset(preset);
                return true;
            }

            protected override void OnDrop(object? data, int idx)
            {
                if (Items.Count <= idx || idx < 0)
                    return;
                if (data is not GatherWindowDragDropData obj)
                    return;

                var preset = _plugin.GatherWindowManager.Presets[idx];
                _plugin.GatherWindowManager.RemoveItem(obj.Preset, obj.ItemIdx);
                _plugin.GatherWindowManager.AddItem(preset, obj.Item);
            }


            protected override bool OnMove(int idx1, int idx2)
            {
                _plugin.GatherWindowManager.MovePreset(idx1, idx2);
                return true;
            }
        }

        public readonly GatherWindowSelector Selector = new();

        public int  NewGatherableIdx;
        public bool EditName;
        public bool EditDesc;
    }

    private readonly GatherWindowCache _gatherWindowCache;

    private void DrawGatherWindowPresetHeaderLine()
    {
        if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Copy.ToIconString(), IconButtonSize, "將目前的採集視窗預設複製到剪貼簿。",
                _gatherWindowCache.Selector.Current == null, true))
        {
            var preset = _gatherWindowCache.Selector.Current!;
            try
            {
                var s = new GatherWindowPreset.Config(preset).ToBase64();
                ImGui.SetClipboardText(s);
                Communicator.PrintClipboardMessage("採集視窗預設 ", preset.Name);
            }
            catch (Exception e)
            {
                Communicator.PrintClipboardMessage("採集視窗預設 ", preset.Name, e);
            }
        }

        if (ImGuiUtil.DrawDisabledButton("建立提醒", Vector2.Zero, "根據此採集視窗預設建立新的提醒群組。", _gatherWindowCache.Selector.Current == null))
        {
            var preset = new AlarmGroup(_gatherWindowCache.Selector.Current!);
            _plugin.AlarmManager.AddGroup(preset);
        }

        ImGuiComponents.HelpMarker(
            "若不依上線時間排序採集視窗，項目會依「已啟用的預設順序」再依「預設內項目順序」不重複加入。\n"
          + "你可以在清單中拖曳預設來調整順序。\n"
          + "你可以在特定預設內拖曳項目來調整順序。\n"
          + "你可以將項目拖曳到選擇器中另一個預設上，把它加入該預設並從目前預設移除。\n"
          + "在採集視窗本身，按住 Ctrl 並右鍵點擊項目，可將其從所屬預設中刪除。若這麼做移除了預設中的最後一個項目，該預設也會一併被移除。");
    }

    private void DrawGatherWindowPreset(GatherWindowPreset preset)
    {
        if (ImGuiUtil.DrawEditButtonText(0, _gatherWindowCache.EditName ? preset.Name : CheckUnnamed(preset.Name), out var newName,
                ref _gatherWindowCache.EditName, IconButtonSize, SetInputWidth, 64))
            _plugin.GatherWindowManager.ChangeName(preset, newName);
        if (ImGuiUtil.DrawEditButtonText(1, _gatherWindowCache.EditDesc ? preset.Description : CheckUndescribed(preset.Description),
                out var newDesc, ref _gatherWindowCache.EditDesc, IconButtonSize, 2 * SetInputWidth, 128))
            _plugin.GatherWindowManager.ChangeDescription(preset, newDesc);

        var tmp = preset.Enabled;
        if (ImGui.Checkbox("啟用##preset", ref tmp) && tmp != preset.Enabled)
            _plugin.GatherWindowManager.TogglePreset(preset);

        ImGui.NewLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() - ImGui.GetStyle().ItemInnerSpacing.X);
        using var box = ImRaii.ListBox("##gatherWindowList", new Vector2(-1.5f * ImGui.GetStyle().ItemSpacing.X, -1));
        if (!box)
            return;

        for (var i = 0; i < preset.Items.Count; ++i)
        {
            using var id    = ImRaii.PushId(i);
            using var group = ImRaii.Group();
            var       item  = preset.Items[i];
            if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Trash.ToIconString(), IconButtonSize, "從預設中刪除此項目...", false,
                    true))
                _plugin.GatherWindowManager.RemoveItem(preset, i--);

            ImGui.SameLine();
            if (_gatherGroupCache.GatherableSelector.Draw(item.Name[GatherBuddy.Language], out var newIdx))
                _plugin.GatherWindowManager.ChangeItem(preset, GatherGroupCache.AllGatherables[newIdx], i);
            group.Dispose();

            _gatherWindowCache.Selector.CreateDropSource(new GatherWindowDragDropData(preset, item, i), item.Name[GatherBuddy.Language]);

            var localIdx = i;
            _gatherWindowCache.Selector.CreateDropTarget<GatherWindowDragDropData>(d
                => _plugin.GatherWindowManager.MoveItem(d.Preset, d.ItemIdx, localIdx));
        }

        if (ImGuiUtil.DrawDisabledButton(FontAwesomeIcon.Plus.ToIconString(), IconButtonSize,
                "將此項目加到預設的最後面（若尚未包含在內）...",
                preset.Items.Contains(GatherGroupCache.AllGatherables[_gatherWindowCache.NewGatherableIdx]), true))
            _plugin.GatherWindowManager.AddItem(preset, GatherGroupCache.AllGatherables[_gatherWindowCache.NewGatherableIdx]);

        ImGui.SameLine();
        if (_gatherGroupCache.GatherableSelector.Draw(_gatherWindowCache.NewGatherableIdx, out var idx))
            _gatherWindowCache.NewGatherableIdx = idx;
    }

    private void DrawGatherWindowTab()
    {
        using var id  = ImRaii.PushId("GatherWindow");
        using var tab = ImRaii.TabItem("採集視窗");

        ImGuiUtil.HoverTooltip(
            "設定視窗太大裝不下嗎？\n"
          + "準備一個只顯示你真正在意的項目的小視窗吧！");

        if (!tab)
            return;

        _gatherWindowCache.Selector.Draw(SelectorWidth);
        ImGui.SameLine();

        ItemDetailsWindow.Draw("預設詳情", DrawGatherWindowPresetHeaderLine, () =>
        {
            if (_gatherWindowCache.Selector.Current != null)
                DrawGatherWindowPreset(_gatherWindowCache.Selector.Current);
        });
    }
}
