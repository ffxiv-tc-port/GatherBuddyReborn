using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using GatherBuddy.AutoGather;
using GatherBuddy.Config;
using ImGuiNET;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OtterGui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ECommons.ImGuiMethods;
using GatherBuddy.Classes;
using static GatherBuddy.AutoGather.AutoGather;

namespace GatherBuddy.Gui
{
    public partial class Interface
    {
        private static readonly (string name, uint id)[] CrystalTypes =
            [("元素碎晶", 2), ("元素水晶", 8), ("元素晶簇", 14)];

        private readonly ConfigPresetsSelector                _configPresetsSelector = new();
        private          (bool EditingName, bool ChangingMin) _configPresetsUIState;

        public IReadOnlyCollection<ConfigPreset> GatherActionsPresets
            => _configPresetsSelector.Presets;

        public class ConfigPresetsSelector : ItemSelector<ConfigPreset>
        {
            private const string FileName = "actions.json";

            public ConfigPresetsSelector()
                : base([], Flags.All ^ Flags.Drop)
            {
                Load();
            }

            public IReadOnlyCollection<ConfigPreset> Presets
                => Items.AsReadOnly();

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
                if (idx == Items.Count - 1)
                    return false;

                Items.RemoveAt(idx);
                Save();
                return true;
            }

            protected override bool OnAdd(string name)
            {
                Items.Insert(Items.Count - 1, new()
                {
                    Name = name,
                });
                Save();
                return true;
            }

            protected override bool OnClipboardImport(string name, string data)
            {
                var preset = ConfigPreset.FromBase64String(data);
                if (preset == null)
                {
                    Notify.Error("無法從剪貼簿載入設定預設。請確認內容是否有效。");
                    return false;
                }

                preset.Name = name;

                Items.Insert(Items.Count - 1, preset);
                Save();
                Notify.Success($"已成功從剪貼簿匯入設定預設 {preset.Name}。");
                return true;
            }

            protected override bool OnDuplicate(string name, int idx)
            {
                var preset = Items[idx] with
                {
                    Enabled = false,
                    Name = name
                };
                Items.Insert(Math.Min(idx + 1, Items.Count - 1), preset);
                Save();
                return true;
            }

            protected override bool OnMove(int idx1, int idx2)
            {
                idx2 = Math.Min(idx2, Items.Count - 2);
                if (idx1 >= Items.Count - 1)
                    return false;
                if (idx1 < 0 || idx2 < 0)
                    return false;

                Plugin.Functions.Move(Items, idx1, idx2);
                Save();
                return true;
            }

            public void Save()
            {
                var file = Plugin.Functions.ObtainSaveFile(FileName);
                if (file == null)
                    return;

                try
                {
                    var text = JsonConvert.SerializeObject(Items, Formatting.Indented);
                    File.WriteAllText(file.FullName, text);
                }
                catch (Exception e)
                {
                    GatherBuddy.Log.Error($"Error serializing config presets data:\n{e}");
                }
            }

            private void Load()
            {
                List<ConfigPreset>? items = null;

                var file = Plugin.Functions.ObtainSaveFile(FileName);
                if (file != null && file.Exists)
                {
                    var text = File.ReadAllText(file.FullName);
                    items = JsonConvert.DeserializeObject<List<ConfigPreset>>(text);
                }

                if (items != null && items.Count > 0)
                {
                    foreach (var item in items)
                    {
                        Items.Add(item);
                    }
                }
                else
                {
                    //Convert old settings to the new Default preset
                    Items.Add(GatherBuddy.Config.AutoGatherConfig.ConvertToPreset());
                    Items[0].ChooseBestActionsAutomatically = true;
                    Save();
                    GatherBuddy.Config.AutoGatherConfig.ConfigConversionFixed        = true;
                    GatherBuddy.Config.AutoGatherConfig.RotationSolverConversionDone = true;
                    GatherBuddy.Config.Save();
                }

                Items[Items.Count - 1] = Items[Items.Count - 1].MakeDefault();

                if (!GatherBuddy.Config.AutoGatherConfig.RotationSolverConversionDone)
                {
                    Items[Items.Count - 1].ChooseBestActionsAutomatically            = true;
                    GatherBuddy.Config.AutoGatherConfig.RotationSolverConversionDone = true;
                    Save();
                    GatherBuddy.Config.Save();
                }

                if (!GatherBuddy.Config.AutoGatherConfig.ConfigConversionFixed)
                {
                    var def = Items[Items.Count - 1];
                    fixAction(def.GatherableActions.Bountiful);
                    fixAction(def.GatherableActions.Yield1);
                    fixAction(def.GatherableActions.Yield2);
                    fixAction(def.GatherableActions.SolidAge);
                    fixAction(def.GatherableActions.TwelvesBounty);
                    fixAction(def.GatherableActions.GivingLand);
                    fixAction(def.GatherableActions.Gift1);
                    fixAction(def.GatherableActions.Gift2);
                    fixAction(def.GatherableActions.Tidings);
                    fixAction(def.GatherableActions.Bountiful);
                    fixAction(def.CollectableActions.Scrutiny);
                    fixAction(def.CollectableActions.Scour);
                    fixAction(def.CollectableActions.Brazen);
                    fixAction(def.CollectableActions.Meticulous);
                    fixAction(def.CollectableActions.SolidAge);
                    fixAction(def.Consumables.Cordial);
                    Save();
                    GatherBuddy.Config.AutoGatherConfig.ConfigConversionFixed = true;
                    GatherBuddy.Config.Save();
                }

                void fixAction(ConfigPreset.ActionConfig action)
                {
                    if (action.MaxGP == 0)
                        action.MaxGP = ConfigPreset.MaxGP;
                }
            }

            public ConfigPreset Match(Gatherable? item)
            {
                return item == null
                    ? Items[Items.Count - 1]
                    : Items.SkipLast(1).Where(i => i.Match(item)).FirstOrDefault(Items[Items.Count - 1]);
            }

            public ConfigPreset Match(Fish? item)
            {
                return item == null
                    ? Items[Items.Count - 1]
                    : Items.SkipLast(1).Where(i => i.Match(item)).FirstOrDefault(Items[Items.Count - 1]);
            }
        }

        public ConfigPreset MatchConfigPreset(Gatherable? item)
            => _configPresetsSelector.Match(item);

        public ConfigPreset MatchConfigPreset(Fish? item)
            => _configPresetsSelector.Match(item);

        public void DrawConfigPresetsTab()
        {
            using var tab = ImRaii.TabItem("設定預設");

            ImGuiUtil.HoverTooltip("設定自動採集要使用的動作。");

            if (!tab)
                return;

            var selector = _configPresetsSelector;
            selector.Draw(SelectorWidth);
            ImGui.SameLine();
            ItemDetailsWindow.Draw("預設詳情", DrawConfigPresetHeader,
                () => { DrawConfigPreset(selector.EnsureCurrent()!, selector.CurrentIdx == selector.Presets.Count - 1); });
        }

        private void DrawConfigPresetHeader()
        {
            if (ImGui.Button("匯出"))
            {
                var current = _configPresetsSelector.Current;
                if (current == null)
                {
                    Notify.Error("未選擇設定預設。");
                    return;
                }

                var text = current.ToBase64String();
                ImGui.SetClipboardText(text);
                Notify.Success($"已成功複製 {current.Name} 到剪貼簿。");
            }

            if (ImGui.Button("檢查"))
            {
                ImGui.OpenPopup("Config Presets Checker");
            }

            ImGuiUtil.HoverTooltip("檢查採集清單中各物品所使用的設定預設");

            var open = true;
            using (var popup = ImRaii.PopupModal("Config Presets Checker", ref open,
                       ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoTitleBar))
            {
                if (popup)
                {
                    using (var table = ImRaii.Table("Items", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                    {
                        ImGui.TableSetupColumn("採集清單");
                        ImGui.TableSetupColumn("物品");
                        ImGui.TableSetupColumn("設定預設");
                        ImGui.TableHeadersRow();

                        var crystals = CrystalTypes
                            .Select(x => ("", x.name, GatherBuddy.GameData.Gatherables[x.id]));
                        var items = _plugin.AutoGatherListsManager.Lists
                            .Where(x => x.Enabled && !x.Fallback)
                            .SelectMany(x => x.Items.Select(i => (x.Name, i.Name[GatherBuddy.Language], i as Gatherable)));
                        var fish = _plugin.AutoGatherListsManager.Lists.Where(x => x.Enabled && !x.Fallback)
                            .SelectMany(x => x.Items.Select(i => (x.Name, i.Name[GatherBuddy.Language], i as Fish)));

                        foreach (var (list, name, item) in items)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text(list);
                            ImGui.TableNextColumn();
                            ImGui.Text(name);
                            ImGui.TableNextColumn();
                            ImGui.Text(MatchConfigPreset(item).Name);
                        }
                        foreach (var (list, name, item) in fish)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text(list);
                            ImGui.TableNextColumn();
                            ImGui.Text(name);
                            ImGui.TableNextColumn();
                            ImGui.Text(MatchConfigPreset(item).Name);
                        }
                        foreach (var (list, name, item) in crystals)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text(list);
                            ImGui.TableNextColumn();
                            ImGui.Text(name);
                            ImGui.TableNextColumn();
                            ImGui.Text(MatchConfigPreset(item).Name);
                        }
                    }

                    var size   = ImGui.CalcTextSize("關閉").X + ImGui.GetStyle().FramePadding.X * 2.0f;
                    var offset = (ImGui.GetContentRegionAvail().X - size) * 0.5f;
                    if (offset > 0.0f)
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                    if (ImGui.Button("關閉"))
                        ImGui.CloseCurrentPopup();
                }
            }

            ImGuiComponents.HelpMarker(
                "預設會依照由上到下的順序，與目前目標物品進行比對。\n"
              + "只會使用第一個符合的預設，其餘的會被忽略。\n"
              + "預設清單的最後一項永遠是「預設」，當沒有其他預設符合時會使用它。");
        }

        private void DrawConfigPreset(ConfigPreset preset, bool isDefault)
        {
            var     selector = _configPresetsSelector;
            ref var state    = ref _configPresetsUIState;

            if (!isDefault)
            {
                if (ImGuiUtil.DrawEditButtonText(0, CheckUnnamed(preset.Name), out var name, ref state.EditingName, IconButtonSize,
                        SetInputWidth, 64)
                 && name != CheckUnnamed(preset.Name))
                {
                    preset.Name = name;
                    selector.Save();
                }

                var enabled = preset.Enabled;
                if (ImGui.Checkbox("啟用", ref enabled) && enabled != preset.Enabled)
                {
                    preset.Enabled = enabled;
                    selector.Save();
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                var useGlv = preset.ItemLevel.UseGlv;
                using var box = ImRaii.ListBox("##ConfigPresetListbox",
                    new Vector2(-1.5f * ImGui.GetStyle().ItemSpacing.X, ImGui.GetFrameHeightWithSpacing() * 3 + ItemSpacing.Y));
                Span<int> ilvl = [preset.ItemLevel.Min, preset.ItemLevel.Max];
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.DragInt2("最低與最高等級", ref ilvl[0], 0.2f, 1, useGlv ? ConfigPreset.MaxGvl : ConfigPreset.MaxLevel))
                {
                    state.ChangingMin    = preset.ItemLevel.Min != ilvl[0];
                    preset.ItemLevel.Min = ilvl[0];
                    preset.ItemLevel.Max = ilvl[1];
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (preset.ItemLevel.Min > preset.ItemLevel.Max)
                    {
                        if (state.ChangingMin)
                            preset.ItemLevel.Max = preset.ItemLevel.Min;
                        else
                            preset.ItemLevel.Min = preset.ItemLevel.Max;
                    }

                    selector.Save();
                }

                ImGui.SameLine();
                if (ImGui.RadioButton("等級", !useGlv))
                    useGlv = false;
                ImGuiUtil.HoverTooltip("採集紀錄與採集視窗中顯示的等級。");
                ImGui.SameLine();
                if (ImGui.RadioButton("採集等級", useGlv))
                    useGlv = true;
                ImGuiUtil.HoverTooltip("採集等級（隱藏數值）。可用於區分不同等級的傳說採集點。");
                if (useGlv != preset.ItemLevel.UseGlv)
                {
                    int min, max;
                    if (useGlv)
                    {
                        min = GatherBuddy.GameData.Gatherables.Values
                            .Where(i => i.Level >= preset.ItemLevel.Min)
                            .Select(i => (int)i.GatheringData.GatheringItemLevel.RowId)
                            .DefaultIfEmpty(ConfigPreset.MaxGvl)
                            .Min();
                        max = GatherBuddy.GameData.Gatherables.Values
                            .Where(i => i.Level <= preset.ItemLevel.Max)
                            .Select(i => (int)i.GatheringData.GatheringItemLevel.RowId)
                            .DefaultIfEmpty(1)
                            .Max();
                    }
                    else
                    {
                        min = GatherBuddy.GameData.Gatherables.Values
                            .Where(i => i.GatheringData.GatheringItemLevel.RowId >= preset.ItemLevel.Min)
                            .Select(i => i.Level)
                            .DefaultIfEmpty(ConfigPreset.MaxLevel)
                            .Min();
                        max = GatherBuddy.GameData.Gatherables.Values
                            .Where(i => i.GatheringData.GatheringItemLevel.RowId <= preset.ItemLevel.Max)
                            .Select(i => i.Level)
                            .DefaultIfEmpty(1)
                            .Max();
                    }

                    preset.ItemLevel.UseGlv = useGlv;
                    preset.ItemLevel.Min    = min;
                    preset.ItemLevel.Max    = max;
                    selector.Save();
                }

                ImGui.Text("採集點類型：");
                ImGui.SameLine();
                if (ImGuiUtil.Checkbox("一般", "", preset.NodeType.Regular, x => preset.NodeType.Regular = x))
                    selector.Save();
                ImGui.SameLine(0, ImGui.CalcTextSize("水晶").X - ImGui.CalcTextSize("一般").X + ItemSpacing.X);
                if (ImGuiUtil.Checkbox("未鑑別", "", preset.NodeType.Unspoiled, x => preset.NodeType.Unspoiled = x))
                    selector.Save();
                ImGui.SameLine(0, ImGui.CalcTextSize("收藏品").X - ImGui.CalcTextSize("未鑑別").X + ItemSpacing.X);
                if (ImGuiUtil.Checkbox("傳說", "", preset.NodeType.Legendary, x => preset.NodeType.Legendary = x))
                    selector.Save();
                ImGui.SameLine();
                if (ImGuiUtil.Checkbox("限時", "", preset.NodeType.Ephemeral, x => preset.NodeType.Ephemeral = x))
                    selector.Save();

                ImGui.Text("物品類型：");
                ImGui.SameLine(0, ImGui.CalcTextSize("採集點類型：").X - ImGui.CalcTextSize("物品類型：").X + ItemSpacing.X);
                if (ImGuiUtil.Checkbox("水晶", "", preset.ItemType.Crystals, x => preset.ItemType.Crystals = x))
                    selector.Save();
                ImGui.SameLine();
                if (ImGuiUtil.Checkbox("收藏品", "", preset.ItemType.Collectables, x => preset.ItemType.Collectables = x))
                    selector.Save();
                ImGui.SameLine();
                if (ImGuiUtil.Checkbox("其他", "", preset.ItemType.Other, x => preset.ItemType.Other = x))
                    selector.Save();
                ImGui.SameLine();
                if (ImGuiUtil.Checkbox("魚類", "", preset.ItemType.Fish, x => preset.ItemType.Fish = x))
                    selector.Save();
            }

            using var child = ImRaii.Child("ConfigPresetSettings", new Vector2(-1.5f * ItemSpacing.X, -ItemSpacing.Y));

            using var width = ImRaii.ItemWidth(SetInputWidth);

            using (var node = ImRaii.TreeNode("一般設定", ImGuiTreeNodeFlags.Framed))
            {
                if (node)
                {
                    if (preset.ItemType.Crystals || preset.ItemType.Other)
                    {
                        var tmp = preset.GatherableMinGP;
                        if (ImGui.DragInt("採集一般物品或水晶所需的最低 GP", ref tmp, 1f, 0, ConfigPreset.MaxGP))
                            preset.GatherableMinGP = tmp;
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            selector.Save();
                    }

                    if (preset.ItemType.Collectables)
                    {
                        var tmp = preset.CollectableMinGP;
                        if (ImGui.DragInt("採集收藏品所需的最低 GP", ref tmp, 1f, 0, ConfigPreset.MaxGP))
                            preset.CollectableMinGP = tmp;
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            selector.Save();

                        tmp = preset.CollectableActionsMinGP;
                        if (ImGui.DragInt("對收藏品使用動作所需的最低 GP", ref tmp, 1f, 0, ConfigPreset.MaxGP))
                            preset.CollectableActionsMinGP = tmp;
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            selector.Save();

                        ImGui.SameLine();
                        if (ImGuiUtil.Checkbox($"永遠使用 {ConcatNames(Actions.SolidAge)}",
                                $"當達到目標收藏品評分時，無論起始 GP 為何都使用 {ConcatNames(Actions.SolidAge)}",
                                preset.CollectableAlwaysUseSolidAge,
                                x => preset.CollectableAlwaysUseSolidAge = x))
                            selector.Save();

                        if (ImGuiUtil.Checkbox("手動設定收藏品評分",
                                "停用時，收藏品評分會自動從遊戲介面偵測。\n"
                              + "啟用時，可以在下方手動指定目標與最低評分。",
                                preset.CollectableManualScores,
                                x => preset.CollectableManualScores = x))
                            selector.Save();

                        if (preset.CollectableManualScores)
                        {
                            tmp = preset.CollectableTagetScore;
                            if (ImGui.DragInt("採集前要達到的目標收藏品評分", ref tmp, 1f, 0,
                                    ConfigPreset.MaxCollectability))
                                preset.CollectableTagetScore = tmp;
                            if (ImGui.IsItemDeactivatedAfterEdit())
                                selector.Save();

                            tmp = preset.CollectableMinScore;
                            if (ImGui.DragInt(
                                    $"在最後一次耐久時採集所需的最低收藏品評分（設為 {ConfigPreset.MaxCollectability} 可停用）",
                                    ref tmp, 1f, 0, ConfigPreset.MaxCollectability))
                                preset.CollectableMinScore = tmp;
                            if (ImGui.IsItemDeactivatedAfterEdit())
                                selector.Save();
                        }
                    }

                    if (ImGuiUtil.Checkbox("自動決定使用哪些動作",
                            "此設定依物品或採集點類型而有不同效果。\n"
                          + "收藏品：使用常見的收藏品循環，啟用所有動作。\n"
                          + "未鑑別與傳說採集點：選擇能將產量最大化的動作。\n"
                          + "一般採集點：選擇能將每 GP 產量最大化的動作。\n",
                            preset.ChooseBestActionsAutomatically,
                            x => preset.ChooseBestActionsAutomatically = x))
                        selector.Save();

                    if (preset.ChooseBestActionsAutomatically && preset.NodeType.Regular)
                    {
                        if (ImGuiUtil.Checkbox("保留 GP，直到出現擁有最佳加成的採集點",
                                "此設定僅適用於一般採集點。啟用時，會保留 GP 以等待能帶來最佳每 GP 產量加成的採集點。\n"
                              + "請確認擁有 +2 耐久、+3 產量、+100% 額外獎勵機率隱藏加成的採集點確實存在，且你能滿足其條件。\n"
                              + $"若 {ConcatNames(Actions.Bountiful)} 給予 +3 加成則會忽略此設定，因為沒有更好的選擇了。\n"
                              + "若你擁有再訪特性（91 級以上），不建議啟用此設定。",
                                preset.SpendGPOnBestNodesOnly,
                                x => preset.SpendGPOnBestNodesOnly = x))
                            selector.Save();
                    }
                }
            }

            using var width2 = ImRaii.ItemWidth(SetInputWidth - ImGui.GetStyle().IndentSpacing);
            if ((preset.ItemType.Crystals || preset.ItemType.Other) && !preset.ChooseBestActionsAutomatically)
            {
                using var node = ImRaii.TreeNode("採集動作", ImGuiTreeNodeFlags.Framed);
                if (node)
                {
                    DrawActionConfig(ConcatNames(Actions.Bountiful), preset.GatherableActions.Bountiful, selector.Save);
                    DrawActionConfig(ConcatNames(Actions.Yield1),    preset.GatherableActions.Yield1,    selector.Save);
                    DrawActionConfig(ConcatNames(Actions.Yield2),    preset.GatherableActions.Yield2,    selector.Save);
                    DrawActionConfig(ConcatNames(Actions.SolidAge),  preset.GatherableActions.SolidAge,  selector.Save);
                    DrawActionConfig(ConcatNames(Actions.Gift1),     preset.GatherableActions.Gift1,     selector.Save);
                    DrawActionConfig(ConcatNames(Actions.Gift2),     preset.GatherableActions.Gift2,     selector.Save);
                    DrawActionConfig(ConcatNames(Actions.Tidings),   preset.GatherableActions.Tidings,   selector.Save);
                    if (preset.ItemType.Crystals)
                    {
                        DrawActionConfig(Actions.TwelvesBounty.Names.Botanist, preset.GatherableActions.TwelvesBounty, selector.Save);
                        DrawActionConfig(Actions.GivingLand.Names.Botanist,    preset.GatherableActions.GivingLand,    selector.Save);
                    }
                }
            }

            if (preset.ItemType.Collectables && !preset.ChooseBestActionsAutomatically)
            {
                using var node = ImRaii.TreeNode("收藏品動作", ImGuiTreeNodeFlags.Framed);
                if (node)
                {
                    DrawActionConfig(Actions.Scour.Names.Botanist,      preset.CollectableActions.Scour,      selector.Save);
                    DrawActionConfig(Actions.Brazen.Names.Botanist,     preset.CollectableActions.Brazen,     selector.Save);
                    DrawActionConfig(Actions.Meticulous.Names.Botanist, preset.CollectableActions.Meticulous, selector.Save);
                    DrawActionConfig(Actions.Scrutiny.Names.Botanist,   preset.CollectableActions.Scrutiny,   selector.Save);
                    DrawActionConfig(ConcatNames(Actions.SolidAge),     preset.CollectableActions.SolidAge,   selector.Save);
                }
            }

            {
                using var node = ImRaii.TreeNode("消耗品", ImGuiTreeNodeFlags.Framed);
                if (node)
                {
                    DrawActionConfig("酒",           preset.Consumables.Cordial,        selector.Save, PossibleCordials);
                    DrawActionConfig("食物",          preset.Consumables.Food,           selector.Save, PossibleFoods,           true);
                    DrawActionConfig("藥水",          preset.Consumables.Potion,         selector.Save, PossiblePotions,         true);
                    DrawActionConfig("指南",          preset.Consumables.Manual,         selector.Save, PossibleManuals,         true);
                    DrawActionConfig("小隊指南",       preset.Consumables.SquadronManual, selector.Save, PossibleSquadronManuals, true);
                    DrawActionConfig("小隊通行證",     preset.Consumables.SquadronPass,   selector.Save, PossibleSquadronPasses,  true);
                }
            }

            static string ConcatNames(Actions.BaseAction action)
                => $"{action.Names.Miner} / {action.Names.Botanist}";
        }

        private void DrawActionConfig(string name, ConfigPreset.ActionConfig action, System.Action save, IEnumerable<Item>? items = null,
            bool hideGP = false)
        {
            using var node = ImRaii.TreeNode(name);
            if (!node)
                return;

            ref var state = ref _configPresetsUIState;

            if (ImGuiUtil.Checkbox("啟用", "", action.Enabled, x => action.Enabled = x))
                save();
            if (!action.Enabled)
                return;

            if (action is ConfigPreset.ActionConfigIntegrity action2)
            {
                if (ImGuiUtil.Checkbox("僅在第一階段使用", "僅在尚未從該採集點採集任何物品時使用",
                        action2.FirstStepOnly, x => action2.FirstStepOnly = x))
                    save();
            }

            if (!hideGP)
            {
                Span<int> gp = [action.MinGP, action.MaxGP];
                if (ImGui.DragInt2("最低與最高 GP", ref gp[0], 1, 0, ConfigPreset.MaxGP))
                {
                    state.ChangingMin = action.MinGP != gp[0];
                    action.MinGP      = gp[0];
                    action.MaxGP      = gp[1];
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (action.MinGP > action.MaxGP)
                    {
                        if (state.ChangingMin)
                            action.MaxGP = action.MinGP;
                        else
                            action.MinGP = action.MaxGP;
                    }

                    save();
                }
            }

            if (action is ConfigPreset.ActionConfigBoon action3)
            {
                Span<int> chance = [action3.MinBoonChance, action3.MaxBoonChance];
                if (ImGui.DragInt2("最低與最高額外獎勵機率", ref chance[0], 0.2f, 0, 100))
                {
                    state.ChangingMin     = action3.MinBoonChance != chance[0];
                    action3.MinBoonChance = chance[0];
                    action3.MaxBoonChance = chance[1];
                }

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (action3.MinBoonChance > action3.MaxBoonChance)
                    {
                        if (state.ChangingMin)
                            action3.MaxBoonChance = action3.MinBoonChance;
                        else
                            action3.MinBoonChance = action3.MaxBoonChance;
                    }

                    save();
                }
            }

            if (action is ConfigPreset.ActionConfigIntegrity action4)
            {
                var tmp = action4.MinIntegrity;
                if (ImGui.DragInt("最低初始採集點耐久", ref tmp, 0.1f, 1, ConfigPreset.MaxIntegrity))
                    action4.MinIntegrity = tmp;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    save();
            }

            if (action is ConfigPreset.ActionConfigYieldBonus action5)
            {
                var tmp = action5.MinYieldBonus;
                if (ImGui.DragInt("最低產量加成", ref tmp, 0.1f, 1, 3))
                    action5.MinYieldBonus = tmp;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    save();
            }

            if (action is ConfigPreset.ActionConfigYieldTotal action6)
            {
                var tmp = action6.MinYieldTotal;
                if (ImGui.DragInt("最低總產量", ref tmp, 0.1f, 1, 30))
                    action6.MinYieldTotal = tmp;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    save();
            }

            if (action is ConfigPreset.ActionConfigConsumable action7 && items != null)
            {
                var list = items
                    .SelectMany(item => new[]
                    {
                        (item, rowid: item.RowId),
                        (item, rowid: item.RowId + 100000)
                    })
                    .Where(x => x.item.CanBeHq || x.rowid < 100000)
                    .Select(x => (name: x.item.Name.ExtractText(), x.rowid, count: GetInventoryItemCount(x.rowid)))
                    .OrderBy(x => x.count == 0)
                    .ThenBy(x => x.name)
                    .Select(x => x with { name = $"{(x.rowid > 100000 ? " " : "")}{x.name} ({x.count})" })
                    .ToList();

                var       selected = (action7.ItemId > 0 ? list.FirstOrDefault(x => x.rowid == action7.ItemId).name : null) ?? string.Empty;
                using var combo    = ImRaii.Combo($"選擇{name.ToLower()}", selected);
                if (combo)
                {
                    if (ImGui.Selectable(string.Empty, action7.ItemId <= 0))
                    {
                        action7.ItemId = 0;
                        save();
                    }

                    bool? separatorState = null;
                    foreach (var (itemname, rowid, count) in list)
                    {
                        if (count != 0)
                            separatorState = true;
                        else if (separatorState ?? false)
                        {
                            ImGui.Separator();
                            separatorState = false;
                        }

                        if (ImGui.Selectable(itemname, action7.ItemId == rowid))
                        {
                            action7.ItemId = rowid;
                            save();
                        }
                    }
                }
            }
        }
    }
}
