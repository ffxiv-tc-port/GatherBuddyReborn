using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using Newtonsoft.Json;
using OtterGui;
using Functions = GatherBuddy.Plugin.Functions;

namespace GatherBuddy.AutoGather.Lists;

public partial class AutoGatherListsManager : IDisposable
{
    public event Action? ActiveItemsChanged;

    private const string FileName         = "auto_gather_lists.json";
    private const string FileNameFallback = "gather_window.json";

    private readonly List<AutoGatherList>                   _lists         = [];
    private readonly List<(Gatherable Item, uint Quantity)> _activeItems   = [];
    private readonly List<(Gatherable Item, uint Quantity)> _fallbackItems = [];
    private readonly List<(Fish Fish, uint Quantity)>       _activeFish    = [];

    // 唯讀外殼只包一次：它是底下那個 List 的即時檢視，每次讀屬性都重包只是多配置一個物件。
    public ReadOnlyCollection<AutoGatherList> Lists { get; }

    public ReadOnlyCollection<(Gatherable Item, uint Quantity)> ActiveItems { get; }

    public ReadOnlyCollection<(Gatherable Item, uint Quantity)> FallbackItems { get; }

    public ReadOnlyCollection<(Fish Fish, uint Quantity)> ActiveFish { get; }

    public AutoGatherListsManager()
    {
        Lists         = _lists.AsReadOnly();
        ActiveItems   = _activeItems.AsReadOnly();
        FallbackItems = _fallbackItems.AsReadOnly();
        ActiveFish    = _activeFish.AsReadOnly();
    }

    public void Dispose()
    { }

    public void SetActiveItems()
    {
        _activeItems.Clear();
        _activeFish.Clear();
        _fallbackItems.Clear();
        var items = _lists
            .Where(l => l.Enabled)
            .SelectMany(l => l.Items.Select(i => (Item: i, Quantity: l.Quantities[i], l.Fallback, ItemEnabled: l.EnabledItems[i])))
            .Where(i => i.ItemEnabled)
            .GroupBy(i => (i.Item, i.Fallback))
            .Select(x => (x.Key.Item, Quantity: (uint)Math.Min(x.Sum(g => g.Quantity), uint.MaxValue), x.Key.Fallback));

        foreach (var (item, quantity, fallback) in items)
        {
            if (item is Fish fish)
            {
                _activeFish.Add((fish, quantity));
            }

            if (item is Gatherable gatherable)
            {
                if (fallback)
                {
                    _fallbackItems.Add((gatherable, quantity));
                }
                else
                {
                    _activeItems.Add((gatherable, quantity));
                }
            }
        }

        ActiveItemsChanged?.Invoke();
    }

    public void Save()
    {
        var file = Functions.ObtainSaveFile(FileName);
        if (file == null)
            return;

        try
        {
            var text = SerializeLists();
            Functions.WriteSaveFile(file, text, "Error serializing auto-gather lists data");
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Error serializing auto-gather lists data:\n{e}");
        }
    }

    private string SerializeLists()
        => JsonConvert.SerializeObject(_lists.Select(p => new AutoGatherList.Config(p)), Formatting.Indented);

    public static AutoGatherListsManager Load()
    {
        var ret    = new AutoGatherListsManager();
        var file   = Functions.ObtainSaveFile(FileName);
        var change = false;
        if (file is not { Exists: true })
        {
            file = Functions.ObtainSaveFile(FileNameFallback);
            if (file is not { Exists: true })
            {
                ret.Save();
                return ret;
            }

            change = true;
        }

        // 從舊檔名遷移時寫回的是新檔名、不會覆寫那個舊檔（它屬於採集視窗），所以讀壞或部分載入都不必備份。
        var fromFallback = change;
        try
        {
            var text = File.ReadAllText(file.FullName);
            var data = JsonConvert.DeserializeObject<AutoGatherList.Config[]>(text)!;
            ret._lists.Capacity = data.Length;
            foreach (var cfg in data)
            {
                change |= AutoGatherList.FromConfig(cfg, out var list);
                ret._lists.Add(list);
            }

            if (change)
            {
                if (!fromFallback)
                    Functions.BackupBeforeRewriteSaveFile(file, text, ret.SerializeLists, "載入時有清單項目被略過或修正",
                        "這次會用讀進來的結果立即覆寫原檔。");
                ret.Save();
            }
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"Error deserializing auto gather lists:\n{e}");
            Communicator.PrintError($"[GatherBuddy Reborn] Auto gather lists failed to load and have been reset.");
            if (!fromFallback)
                Functions.BackupUnreadableSaveFile(file, e, "這次已用讀進來的部分（可能是空的）立即覆寫原檔。");
            ret.Save();
        }

        ret.SetActiveItems();
        return ret;
    }
}
