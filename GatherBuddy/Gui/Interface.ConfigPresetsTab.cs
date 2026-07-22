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
using ECommons.LanguageHelpers;
using GatherBuddy.Classes;
using static GatherBuddy.AutoGather.AutoGather;

namespace GatherBuddy.Gui
{
    public partial class Interface
    {
        private static readonly (string name, uint id)[] CrystalTypes =
            [("Elemental Shards".Loc(), 2), ("Elemental Crystals".Loc(), 8), ("Elemental Clusters".Loc(), 14)];

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
                    Notify.Error("Failed to load config preset from clipboard. Are you sure it's valid?".Loc());
                    return false;
                }

                preset.Name = name;

                Items.Insert(Items.Count - 1, preset);
                Save();
                Notify.Success("Imported config preset ?? from clipboard successfully.".Loc(preset.Name));
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
            using var tab = ImRaii.TabItem("Config Presets".Loc());

            ImGuiUtil.HoverTooltip("Configure what actions to use with Auto-Gather.".Loc());

            if (!tab)
                return;

            var selector = _configPresetsSelector;
            selector.Draw(SelectorWidth);
            ImGui.SameLine();
            ItemDetailsWindow.Draw("Preset Details".Loc(), DrawConfigPresetHeader,
                () => { DrawConfigPreset(selector.EnsureCurrent()!, selector.CurrentIdx == selector.Presets.Count - 1); });
        }

        private void DrawConfigPresetHeader()
        {
            if (ImGui.Button("Export".Loc()))
            {
                var current = _configPresetsSelector.Current;
                if (current == null)
                {
                    Notify.Error("No config preset selected.".Loc());
                    return;
                }

                var text = current.ToBase64String();
                ImGui.SetClipboardText(text);
                Notify.Success("Successfully copied ?? to clipboard.".Loc(current.Name));
            }

            if (ImGui.Button("Check".Loc()))
            {
                ImGui.OpenPopup("Config Presets Checker");
            }

            ImGuiUtil.HoverTooltip("Check what presets are used for items from the auto-gather list".Loc());

            var open = true;
            using (var popup = ImRaii.PopupModal("Config Presets Checker", ref open,
                       ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoTitleBar))
            {
                if (popup)
                {
                    using (var table = ImRaii.Table("Items", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                    {
                        ImGui.TableSetupColumn("Gather List".Loc());
                        ImGui.TableSetupColumn("Item".Loc());
                        ImGui.TableSetupColumn("Config Preset".Loc());
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

                    var size   = ImGui.CalcTextSize("Close".Loc()).X + ImGui.GetStyle().FramePadding.X * 2.0f;
                    var offset = (ImGui.GetContentRegionAvail().X - size) * 0.5f;
                    if (offset > 0.0f)
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                    if (ImGui.Button("Close".Loc()))
                        ImGui.CloseCurrentPopup();
                }
            }

            ImGuiComponents.HelpMarker(
                ("Presets are checked against the current target item in order from top to bottom.\n"
              + "Only the first matched preset is used, the rest are ignored.\n"
              + "The Default preset is always last and is used if no other preset matches the item.").Loc());
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
                if (ImGui.Checkbox("Enabled".Loc(), ref enabled) && enabled != preset.Enabled)
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
                if (ImGui.DragInt2("Minimum and maximum item".Loc(), ref ilvl[0], 0.2f, 1, useGlv ? ConfigPreset.MaxGvl : ConfigPreset.MaxLevel))
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
                if (ImGui.RadioButton("level".Loc(), !useGlv))
                    useGlv = false;
                ImGuiUtil.HoverTooltip("Level as shown in the gathering log and the gathering window.".Loc());
                ImGui.SameLine();
                if (ImGui.RadioButton("glv".Loc(), useGlv))
                    useGlv = true;
                ImGuiUtil.HoverTooltip("Gathering level (hidden stat). Use it to distinguish between different tiers of legendary nodes.".Loc());
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

                ImGui.Text("Node types:".Loc());
                ImGui.SameLine();
                if (ImGuiUtil.Checkbox("Regular".Loc(), "", preset.NodeType.Regular, x => preset.NodeType.Regular = x))
                    selector.Save();
                ImGui.SameLine(0, ImGui.CalcTextSize("Crystals".Loc()).X - ImGui.CalcTextSize("Regular".Loc()).X + ItemSpacing.X);
                if (ImGuiUtil.Checkbox("Unspoiled".Loc(), "", preset.NodeType.Unspoiled, x => preset.NodeType.Unspoiled = x))
                    selector.Save();
                ImGui.SameLine(0, ImGui.CalcTextSize("Collectables".Loc()).X - ImGui.CalcTextSize("Unspoiled".Loc()).X + ItemSpacing.X);
                if (ImGuiUtil.Checkbox("Legendary".Loc(), "", preset.NodeType.Legendary, x => preset.NodeType.Legendary = x))
                    selector.Save();
                ImGui.SameLine();
                if (ImGuiUtil.Checkbox("Ephemeral".Loc(), "", preset.NodeType.Ephemeral, x => preset.NodeType.Ephemeral = x))
                    selector.Save();

                ImGui.Text("Item types:".Loc());
                ImGui.SameLine(0, ImGui.CalcTextSize("Node types:".Loc()).X - ImGui.CalcTextSize("Item types:".Loc()).X + ItemSpacing.X);
                if (ImGuiUtil.Checkbox("Crystals".Loc(), "", preset.ItemType.Crystals, x => preset.ItemType.Crystals = x))
                    selector.Save();
                ImGui.SameLine();
                if (ImGuiUtil.Checkbox("Collectables".Loc(), "", preset.ItemType.Collectables, x => preset.ItemType.Collectables = x))
                    selector.Save();
                ImGui.SameLine();
                if (ImGuiUtil.Checkbox("Other".Loc(), "", preset.ItemType.Other, x => preset.ItemType.Other = x))
                    selector.Save();
                ImGui.SameLine();
                if (ImGuiUtil.Checkbox("Fish".Loc(), "", preset.ItemType.Fish, x => preset.ItemType.Fish = x))
                    selector.Save();
            }

            using var child = ImRaii.Child("ConfigPresetSettings", new Vector2(-1.5f * ItemSpacing.X, -ItemSpacing.Y));

            using var width = ImRaii.ItemWidth(SetInputWidth);

            using (var node = ImRaii.TreeNode("General Settings".Loc(), ImGuiTreeNodeFlags.Framed))
            {
                if (node)
                {
                    if (preset.ItemType.Crystals || preset.ItemType.Other)
                    {
                        var tmp = preset.GatherableMinGP;
                        if (ImGui.DragInt("Minimum GP for gathering regular items or crystals".Loc(), ref tmp, 1f, 0, ConfigPreset.MaxGP))
                            preset.GatherableMinGP = tmp;
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            selector.Save();
                    }

                    if (preset.ItemType.Collectables)
                    {
                        var tmp = preset.CollectableMinGP;
                        if (ImGui.DragInt("Minimum GP for collecting collectables".Loc(), ref tmp, 1f, 0, ConfigPreset.MaxGP))
                            preset.CollectableMinGP = tmp;
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            selector.Save();

                        tmp = preset.CollectableActionsMinGP;
                        if (ImGui.DragInt("Minimum GP for using actions on collectables".Loc(), ref tmp, 1f, 0, ConfigPreset.MaxGP))
                            preset.CollectableActionsMinGP = tmp;
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            selector.Save();

                        ImGui.SameLine();
                        if (ImGuiUtil.Checkbox("Always use ??".Loc(ConcatNames(Actions.SolidAge)),
                                "Use ?? regardless of starting GP if the target collectability score is reached".Loc(ConcatNames(Actions.SolidAge)),
                                preset.CollectableAlwaysUseSolidAge,
                                x => preset.CollectableAlwaysUseSolidAge = x))
                            selector.Save();

                        if (ImGuiUtil.Checkbox("Manually set collectability scores".Loc(),
                                ("When disabled, collectability scores will be automatically detected from the game UI.\n"
                              + "When enabled, you can manually specify the target and minimum scores below.").Loc(),
                                preset.CollectableManualScores,
                                x => preset.CollectableManualScores = x))
                            selector.Save();

                        if (preset.CollectableManualScores)
                        {
                            tmp = preset.CollectableTagetScore;
                            if (ImGui.DragInt("Target collectability score to reach before collecting".Loc(), ref tmp, 1f, 0,
                                    ConfigPreset.MaxCollectability))
                                preset.CollectableTagetScore = tmp;
                            if (ImGui.IsItemDeactivatedAfterEdit())
                                selector.Save();

                            tmp = preset.CollectableMinScore;
                            if (ImGui.DragInt(
                                    "Minimum collectability score to collect at the last integrity point (set to ?? to disable)".Loc(ConfigPreset.MaxCollectability),
                                    ref tmp, 1f, 0, ConfigPreset.MaxCollectability))
                                preset.CollectableMinScore = tmp;
                            if (ImGui.IsItemDeactivatedAfterEdit())
                                selector.Save();
                        }
                    }

                    if (ImGuiUtil.Checkbox("Automatically decide what actions to use".Loc(),
                            ("This setting works differently depending on item or node type.\n"
                          + "For collectables: the usual collectable rotation is used with all actions enabled.\n"
                          + "For unspoiled and legendary nodes: actions are chosen to maximise the yield.\n"
                          + "For regular nodes: actions are chosen to maximise the yield per GP spent.\n").Loc(),
                            preset.ChooseBestActionsAutomatically,
                            x => preset.ChooseBestActionsAutomatically = x))
                        selector.Save();

                    if (preset.ChooseBestActionsAutomatically && preset.NodeType.Regular)
                    {
                        if (ImGuiUtil.Checkbox("Hold off spending GP until a node with the best bonuses".Loc(),
                                ("This setting is for regular nodes only. When enabled, GP would be kept for nodes with bonuses\n"
                              + "that would give the best possible yield per GP spent. Make sure that nodes with +2 integrity,\n"
                              + "+3 yield, and +100% boon chance hidden bonuses do exist, and you can meet their requirements.\n"
                              + "It is ignored if ?? gives +3 bonus, because nothing can beat that.\n"
                              + "Not recommended if you have the Revisit trait (level 91+).").Loc(ConcatNames(Actions.Bountiful)),
                                preset.SpendGPOnBestNodesOnly,
                                x => preset.SpendGPOnBestNodesOnly = x))
                            selector.Save();
                    }
                }
            }

            using var width2 = ImRaii.ItemWidth(SetInputWidth - ImGui.GetStyle().IndentSpacing);
            if ((preset.ItemType.Crystals || preset.ItemType.Other) && !preset.ChooseBestActionsAutomatically)
            {
                using var node = ImRaii.TreeNode("Gathering Actions".Loc(), ImGuiTreeNodeFlags.Framed);
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
                using var node = ImRaii.TreeNode("Collectable Actions".Loc(), ImGuiTreeNodeFlags.Framed);
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
                using var node = ImRaii.TreeNode("Consumables".Loc(), ImGuiTreeNodeFlags.Framed);
                if (node)
                {
                    DrawActionConfig("Cordial".Loc(),         preset.Consumables.Cordial,        selector.Save, PossibleCordials);
                    DrawActionConfig("Food".Loc(),            preset.Consumables.Food,           selector.Save, PossibleFoods,           true);
                    DrawActionConfig("Potion".Loc(),          preset.Consumables.Potion,         selector.Save, PossiblePotions,         true);
                    DrawActionConfig("Manual".Loc(),          preset.Consumables.Manual,         selector.Save, PossibleManuals,         true);
                    DrawActionConfig("Squadron Manual".Loc(), preset.Consumables.SquadronManual, selector.Save, PossibleSquadronManuals, true);
                    DrawActionConfig("Squadron Pass".Loc(),   preset.Consumables.SquadronPass,   selector.Save, PossibleSquadronPasses,  true);
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

            if (ImGuiUtil.Checkbox("Enabled".Loc(), "", action.Enabled, x => action.Enabled = x))
                save();
            if (!action.Enabled)
                return;

            if (action is ConfigPreset.ActionConfigIntegrity action2)
            {
                if (ImGuiUtil.Checkbox("Use only on first step".Loc(), "Use only if no items have been gathered from the node yet".Loc(),
                        action2.FirstStepOnly, x => action2.FirstStepOnly = x))
                    save();
            }

            if (!hideGP)
            {
                Span<int> gp = [action.MinGP, action.MaxGP];
                if (ImGui.DragInt2("Minimum and maximum GP".Loc(), ref gp[0], 1, 0, ConfigPreset.MaxGP))
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
                if (ImGui.DragInt2("Minimum and maximum boon chance".Loc(), ref chance[0], 0.2f, 0, 100))
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
                if (ImGui.DragInt("Minimum initial node integrity".Loc(), ref tmp, 0.1f, 1, ConfigPreset.MaxIntegrity))
                    action4.MinIntegrity = tmp;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    save();
            }

            if (action is ConfigPreset.ActionConfigYieldBonus action5)
            {
                var tmp = action5.MinYieldBonus;
                if (ImGui.DragInt("Minimum yield bonus".Loc(), ref tmp, 0.1f, 1, 3))
                    action5.MinYieldBonus = tmp;
                if (ImGui.IsItemDeactivatedAfterEdit())
                    save();
            }

            if (action is ConfigPreset.ActionConfigYieldTotal action6)
            {
                var tmp = action6.MinYieldTotal;
                if (ImGui.DragInt("Minimum total yield".Loc(), ref tmp, 0.1f, 1, 30))
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
                using var combo    = ImRaii.Combo("Select ??".Loc(name.ToLower()), selected);
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
