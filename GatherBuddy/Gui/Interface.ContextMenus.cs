using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.LanguageHelpers;
using GatherBuddy.Alarms;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.GatherHelper;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.Structs;
using Dalamud.Bindings.ImGui;
using ImRaii = OtterGui.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private static string AutomaticallyGenerated
        => "Automatically generated from context menu.".Loc();

    private void DrawAddAlarm(IGatherable item)
    {
        // Only timed items.
        if (item.InternalLocationId <= 0)
            return;

        var current = _alarmCache.Selector.EnsureCurrent();
        if (ImGui.Selectable("Add to Alarm Preset".Loc()))
        {
            if (current == null)
            {
                _plugin.AlarmManager.AddGroup(new AlarmGroup()
                {
                    Description = AutomaticallyGenerated,
                    Enabled     = true,
                    Alarms      = new List<Alarm> { new(item) { Enabled = true } },
                });
                current = _alarmCache.Selector.EnsureCurrent();
            }
            else
            {
                _plugin.AlarmManager.AddAlarm(current, new Alarm(item));
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Add ?? to ??".Loc(item.Name[GatherBuddy.Language], current == null ? "a new alarm preset.".Loc() : CheckUnnamed(current.Name)));
    }

    private void DrawAddToGatherGroup(IGatherable item)
    {
        var       current = _gatherGroupCache.Selector.EnsureCurrent();
        using var color   = ImRaii.PushColor(ImGuiCol.Text, ColorId.DisabledText.Value(), current == null);
        if (ImGui.Selectable("Add to Gather Group".Loc()) && current != null)
            if (_plugin.GatherGroupManager.ChangeGroupNode(current, current.Nodes.Count, item, null, null, null, false))
                _plugin.GatherGroupManager.Save();

        color.Pop();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(current == null
                ? "Requires a Gather Group to be setup and selected.".Loc()
                : "Add ?? to ??".Loc(item.Name[GatherBuddy.Language], current.Name));
    }

    private void DrawAddGatherWindow(IGatherable item)
    {
        var current = _gatherWindowCache.Selector.EnsureCurrent();

        if (ImGui.Selectable("Add to Gather Window Preset".Loc()))
        {
            if (current == null)
                _plugin.GatherWindowManager.AddPreset(new GatherWindowPreset
                {
                    Enabled     = true,
                    Items       = new List<IGatherable> { item },
                    Description = AutomaticallyGenerated,
                });
            else
                _plugin.GatherWindowManager.AddItem(current, item);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Add ?? to ??".Loc(item.Name[GatherBuddy.Language], current == null ? "a new gather window preset.".Loc() : CheckUnnamed(current.Name)));
    }

    private static string TeamCraftAddressEnd(string type, uint id)
    {
        var lang = GatherBuddy.Language switch
        {
            ClientLanguage.English  => "en",
            ClientLanguage.German   => "de",
            ClientLanguage.French   => "fr",
            ClientLanguage.Japanese => "ja",
            // TC(台服)客戶端在 Dalamud 13.0.0.16 之後回報 ClientLanguage 7(TraditionalChinese),
            // 舊版回報 4(ChineseSimplified)。用數值比較才能同時相容 CI 釘的 13.0.0.6(列舉沒有 7 這個名字)與執行期新版。
            // TeamCraft 的繁中代碼是 tw,不是 zh(zh 是國服簡中)。查證來源(2026-09-03,staging 分支):
            //   apps/client/src/app/core/data/language.ts     Language = 'fr'|'en'|'de'|'ja'|'ko'|'zh'|'ru'|'tw'
            //   apps/client/src/app/modules/settings/settings.service.ts
            //       availableLocales = ['en','de','fr','ja','pt','br','es','ko','zh','tw','ru'];availableRegions 含 Region.Taiwan
            //   apps/data-extraction/src/abstract-extractor.ts  另有 assets/data/tw/*.json 的 tw 名稱資料管線
            // ⚠️ 代碼若不在 availableLocales 內,db.component.ts 會靜默退回 en(不報錯);
            //    tw 沒有的名稱由 i18n-tools.service.ts 的 getName() 退回 .en,所以最壞情況與原本的 en 相同。
            (ClientLanguage)4 or (ClientLanguage)5 or (ClientLanguage)7 => "tw",
            _                       => "en",
        };

        return $"db/{lang}/{type}/{id}";
    }

    private static string TeamCraftAddressEnd(FishingSpot s)
        => s.Spearfishing
            ? TeamCraftAddressEnd("spearfishing-spot", s.SpearfishingSpotData!.Value.GatheringPointBase.RowId)
            : TeamCraftAddressEnd("fishing-spot",      s.Id);

    private static string GarlandToolsItemAddress(uint itemId)
        => $"https://www.garlandtools.org/db/#item/{itemId}";

    private static void DrawOpenInGarlandTools(uint itemId)
    {
        if (itemId == 0)
            return;

        if (!ImGui.Selectable("Open in GarlandTools".Loc()))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(GarlandToolsItemAddress(itemId)) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Could not open GarlandTools:\n{e.Message}");
        }
    }

    private static void DrawOpenInTeamCraft(uint itemId)
    {
        if (itemId == 0)
            return;

        if (ImGui.Selectable("Open in TeamCraft (Browser)".Loc()))
            OpenInTeamCraftWeb(TeamCraftAddressEnd("item", itemId));

        if (ImGui.Selectable("Open in TeamCraft (App)".Loc()))
            OpenInTeamCraftLocal(TeamCraftAddressEnd("item", itemId));
    }

    private static void OpenInTeamCraftWeb(string addressEnd)
    {
        Process.Start(new ProcessStartInfo($"https://ffxivteamcraft.com/{addressEnd}")
        {
            UseShellExecute = true,
        });
    }

    private static void OpenInTeamCraftLocal(string addressEnd)
    {
        Task.Run(() =>
        {
            try
            {
                using var request  = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:14500/{addressEnd}");
                using var response = GatherBuddy.HttpClient.Send(request);
            }
            catch
            {
                try
                {
                    if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ffxiv-teamcraft")))
                        Process.Start(new ProcessStartInfo($"teamcraft:///{addressEnd}")
                        {
                            UseShellExecute = true,
                        });
                }
                catch
                {
                    GatherBuddy.Log.Error("Could not open local teamcraft.");
                }
            }
        });
    }

    private static void DrawOpenInTeamCraft(FishingSpot fs)
    {
        if (fs.Id == 0)
            return;

        if (ImGui.Selectable("Open in TeamCraft (Browser)".Loc()))
            OpenInTeamCraftWeb(TeamCraftAddressEnd(fs));

        if (ImGui.Selectable("Open in TeamCraft (App)".Loc()))
            OpenInTeamCraftLocal(TeamCraftAddressEnd(fs));
    }

    public void CreateContextMenu(IGatherable item)
    {
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup(item.Name[GatherBuddy.Language]);

        using var popup = ImRaii.Popup(item.Name[GatherBuddy.Language]);
        if (!popup)
            return;

        DrawAddAlarm(item);
        DrawAddToGatherGroup(item);
        DrawAddGatherWindow(item);
        DrawAddToAutoGather(item);
        if (ImGui.Selectable("Create Link".Loc()))
            Communicator.Print(SeString.CreateItemLink(item.ItemId));
        DrawOpenInGarlandTools(item.ItemId);
        DrawOpenInTeamCraft(item.ItemId);
    }

    private static string PresetName
        => "From Gatherables List".Loc();

    private void DrawAddToAutoGather(IGatherable item)
    {
        var current = _autoGatherListsCache.Selector.EnsureCurrent();

        if (ImGui.Selectable("Add to Auto-Gather List".Loc()))
        {
            if (current == null)
                CreateAndAddPreset(item);
            else
                _plugin.AutoGatherListsManager.AddItem(current, item);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Add ?? to ??".Loc(item.Name[GatherBuddy.Language], current == null ? "a new gather window preset.".Loc() : CheckUnnamed(current.Name)));
    }

    private static AutoGatherList CreateAndAddPreset(IGatherable item)
    {
        var preset = new AutoGatherList
        {
            Enabled     = true,
            Description = AutomaticallyGenerated,
            Name        = PresetName
        };
        preset.Add(item);
        _plugin.AutoGatherListsManager.AddList(preset);

        return preset;
    }

    public static void CreateGatherWindowContextMenu(IGatherable item, bool clicked)
    {
        if (clicked)
            ImGui.OpenPopup(item.Name[GatherBuddy.Language]);

        using var popup = ImRaii.Popup(item.Name[GatherBuddy.Language]);
        if (!popup)
            return;

        if (ImGui.Selectable("Create Link".Loc()))
            Communicator.Print(SeString.CreateItemLink(item.ItemId));
        DrawOpenInGarlandTools(item.ItemId);
        DrawOpenInTeamCraft(item.ItemId);
    }

    public static void CreateContextMenu(Bait bait)
    {
        if (bait.Id == 0)
            return;

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup(bait.Name);

        using var popup = ImRaii.Popup(bait.Name);
        if (!popup)
            return;

        if (ImGui.Selectable("Create Link".Loc()))
            Communicator.Print(SeString.CreateItemLink(bait.Id));
        DrawOpenInGarlandTools(bait.Id);
        DrawOpenInTeamCraft(bait.Id);
    }

    public static void CreateContextMenu(FishingSpot? spot)
    {
        if (spot == null)
            return;

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup(spot.Name);

        using var popup = ImRaii.Popup(spot.Name);
        if (!popup)
            return;

        DrawOpenInTeamCraft(spot);
    }
}
