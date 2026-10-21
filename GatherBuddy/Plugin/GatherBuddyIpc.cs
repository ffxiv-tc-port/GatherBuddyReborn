using ECommons.EzIpcManager;
using ECommons.LanguageHelpers;
using GatherBuddy.AutoGather;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GatherBuddy.Plugin;

public sealed class GatherBuddyIpc : IDisposable
{
    /// <summary>
    /// 消費端用這個數字判對方支不支援某個端點：3 起有 AddToAutoGatherList／
    /// GetActiveAutoGatherItemCount，4 起有 SetAutoGatherListQuantity／GetAutoGatherListQuantity，
    /// 5 起有 GetGatherableTreasureMaps／GatherTreasureMap。端點名一旦公開就不改，只往上加。
    /// </summary>
    public const int IpcVersion = 5;

    // GatherTreasureMap 的回傳碼。0 必須是「不可用」：消費端把 default(int) 讀成「已開始」會是災難。
    public const int MapUnavailable    = 0;
    public const int MapStarted        = 1;
    public const int MapAlreadyRunning = 2;
    public const int MapNotGatherable  = 3;
    public const int MapAllowanceNotUp = 4;
    public const int MapAlreadyHeld    = 5;
    public const int MapLevelTooLow    = 6;
    public const int MapNotLoggedIn    = 7;
    public const int MapListDisabled   = 8;

    private readonly GatherBuddy _plugin;

    /// <summary>上一次因清單停用而提示過的地圖；同一張連續被拒只提示一次。只在主執行緒讀寫。</summary>
    private uint _lastListDisabledNotice;

    public GatherBuddyIpc(GatherBuddy plugin)
    {
        _plugin = plugin;
        // 端點一註冊就可能被別的外掛呼叫，所以快照要先填好再 Init。
        PublishStateSnapshot();
        EzIPC.Init(this, GatherBuddy.InternalName);
        Debug.Assert(AutoGatherWaiting != null);
        Debug.Assert(AutoGatherEnabledChanged != null);
    }

    // 🔴 這三個是「別的執行緒讀、遊戲主執行緒寫」的狀態快照。高頻輪詢的端點不可以走
    //    IpcFrameworkGate：它最久阻塞 5 秒，足以拖垮呼叫端的每幀繪製。
    private static volatile bool   _enabledSnapshot;
    private static volatile bool   _waitingSnapshot;
    private static volatile string _statusSnapshot = string.Empty;

    /// <summary>把自動採集狀態拍成快照供 IPC 端點就地讀取。<b>只從遊戲主執行緒呼叫。</b></summary>
    /// <remarks>每幀一次，外加 <c>Enabled</c> 的 setter 尾端，讓消費端設完立刻讀得到。</remarks>
    internal static void PublishStateSnapshot()
    {
        var autoGather = GatherBuddy.AutoGather;
        _enabledSnapshot = autoGather.Enabled;
        _waitingSnapshot = autoGather.Waiting;
        _statusSnapshot  = autoGather.AutoStatus;
    }

#pragma warning disable CA1822 // Mark members as static
    [EzIPC]
    public int Version()
        => IpcVersion;

    /// <returns>道具 id；<c>0</c> ＝ 查不到，或等不到主執行緒／正在卸載。</returns>
    /// <remarks>走閘門是因為模糊比對 <c>PatriciaTrie.FuzzyFind</c> 會就地改寫共用的暫存表，聊天指令查名字走同一份。</remarks>
    [EzIPC]
    public uint Identify(string text)
        => IpcFrameworkGate.Get(nameof(Identify), () => IdentifyOnFramework(text), 0u);

    [EzIPC]
    public bool IsAutoGatherEnabled()
        => _enabledSnapshot;

    [EzIPC]
    public string GetAutoGatherStatusText()
        => _statusSnapshot;

    /// <summary>開／關自動採集。逾時或卸載期＝這次沒改成，簽章沒有回值可以告知，只留 log。</summary>
    [EzIPC]
    public void SetAutoGatherEnabled(bool enabled)
        => IpcFrameworkGate.Run(nameof(SetAutoGatherEnabled), () => GatherBuddy.AutoGather.Enabled = enabled);

    [EzIPC]
    public bool IsAutoGatherWaiting()
        => _waitingSnapshot;

    /// <summary>
    /// 把道具加進自動採集清單。<b>只加清單，不會開始自動採集。</b>
    /// 已在清單上就把數量加上去；<paramref name="quantity"/> 小於 1 當 1。
    /// </summary>
    /// <param name="itemId">道具 id，不是採集物 id（用 id 不用名字：台服名字比對不可靠）。</param>
    /// <returns><see langword="false"/> ＝ 沒有可用的採集節點資料／等不到主執行緒／正在卸載。</returns>
    [EzIPC]
    public bool AddToAutoGatherList(uint itemId, int quantity)
        => IpcFrameworkGate.Get(nameof(AddToAutoGatherList), () => AddToListOnFramework(itemId, quantity), false);

    /// <summary>目前所有已啟用清單合起來有幾種道具要採。</summary>
    /// <returns>件數；<c>-1</c> ＝ 問不到（等不到主執行緒或正在卸載），<b>不是</b> 0。</returns>
    [EzIPC]
    public int GetActiveAutoGatherItemCount()
        => IpcFrameworkGate.Get(nameof(GetActiveAutoGatherItemCount), CountActiveItemsOnFramework, -1);

    /// <summary>
    /// 把「來自其他插件」清單上這個道具的目標數量<b>設為</b> <paramref name="quantity"/>（不是加上去），重複呼叫不會疊加；
    /// <paramref name="quantity"/> 小於 1 ＝ 從清單移除。<b>只改清單，不會開始自動採集。</b>
    /// </summary>
    /// <remarks>
    /// 清單不存在就照 <see cref="AddToAutoGatherList"/> 的方式建立（移除時不建）。
    /// 數量照清單本身的規則正規化（上限 999999、藏寶圖固定 1），不動該項的啟用狀態。
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> ＝ 清單上已是這個目標（或已不在清單上）；
    /// <see langword="false"/> ＝ 沒有可用的採集節點資料／等不到主執行緒／正在卸載。
    /// </returns>
    [EzIPC]
    public bool SetAutoGatherListQuantity(uint itemId, int quantity)
        => IpcFrameworkGate.Get(nameof(SetAutoGatherListQuantity), () => SetQuantityOnFramework(itemId, quantity), false);

    /// <summary>「來自其他插件」清單上這個道具目前的目標數量。走主執行緒閘門，不要拿來從別的執行緒每幀輪詢。</summary>
    /// <returns>數量；<c>0</c> ＝ 不在清單上（或清單不存在）；<c>-1</c> ＝ 問不到（等不到主執行緒或正在卸載），<b>不是</b> 0。</returns>
    [EzIPC]
    public int GetAutoGatherListQuantity(uint itemId)
        => IpcFrameworkGate.Get(nameof(GetAutoGatherListQuantity), () => GetQuantityOnFramework(itemId), -1);

    /// <summary>有帶世界座標的採集點、自動採集找得到的寶物地圖道具 id，等級高的在前。</summary>
    /// <returns>每次都是新的一份；問不到（等不到主執行緒或正在卸載）回空清單。</returns>
    [EzIPC]
    public List<uint> GetGatherableTreasureMaps()
        => IpcFrameworkGate.Get(nameof(GetGatherableTreasureMaps), ListTreasureMapsOnFramework, new List<uint>());

    /// <summary>
    /// 使用者按下「採一張寶物地圖」：把這張地圖放進「來自其他插件」清單（目標 1）並開啟自動採集，
    /// 之後照自動採集自己的流程走。只能接在使用者按下的按鈕上：不輪詢、不重試。
    /// </summary>
    /// <returns>
    /// 見 <c>Map*</c> 常數；只有 <see cref="MapStarted"/>／<see cref="MapAlreadyRunning"/>／<see cref="MapListDisabled"/> 有動到清單，
    /// 只有前兩者會開自動採集。<see cref="MapListDisabled"/>＝清單或該項目被使用者停用，刻意不替他勾回。
    /// </returns>
    [EzIPC]
    public int GatherTreasureMap(uint itemId)
        => IpcFrameworkGate.Get(nameof(GatherTreasureMap), () => GatherTreasureMapOnFramework(itemId), MapUnavailable);

    [EzIPCEvent]
    public readonly Action AutoGatherWaiting;

    [EzIPCEvent]
    public readonly Action<bool> AutoGatherEnabledChanged;

#pragma warning restore CA1822 // Mark members as static

    /// <summary>IPC 加進來的道具固定收在這一份清單，刻意不動使用者自己建的清單。</summary>
    private static string IpcListName
        => "From Other Plugins".Loc();

    private static string IpcListDescription
        => "Automatically generated from another plugin.".Loc();

    private uint IdentifyOnFramework(string text)
        => _plugin.Executor.Identificator.IdentifyGatherable(text)?.ItemId
         ?? _plugin.Executor.Identificator.IdentifyFish(text)?.ItemId ?? 0;

    private int CountActiveItemsOnFramework()
        => _plugin.AutoGatherListsManager.ActiveItems.Count
         + _plugin.AutoGatherListsManager.ActiveFish.Count
         + _plugin.AutoGatherListsManager.FallbackItems.Count;

    private bool AddToListOnFramework(uint itemId, int quantity)
    {
        if (itemId == 0 || !TryFindGatherable(itemId, out var item))
            return false;

        var manager = _plugin.AutoGatherListsManager;
        var list    = GetOrCreateIpcList(manager);
        var amount  = (uint)Math.Clamp(quantity, 1, MaxQuantity);

        if (list.Quantities.TryGetValue(item, out var old))
        {
            manager.ChangeQuantity(list, item, (uint)Math.Min(old + (long)amount, MaxQuantity));
            return true;
        }

        manager.AddItem(list, item);
        if (!list.Quantities.ContainsKey(item))
            return false;

        if (amount > 1)
            manager.ChangeQuantity(list, item, amount);
        return true;
    }

    private bool SetQuantityOnFramework(uint itemId, int quantity)
    {
        if (itemId == 0)
            return false;

        var manager = _plugin.AutoGatherListsManager;
        var list    = FindIpcList(manager);
        var idx     = list == null ? -1 : IndexOfItem(list, itemId);

        if (quantity < 1)
        {
            if (idx >= 0)
                manager.RemoveItem(list!, idx);
            return true;
        }

        var amount = (uint)Math.Min(quantity, MaxQuantity);
        if (idx >= 0)
        {
            manager.ChangeQuantity(list!, list!.Items[idx], amount);
            return true;
        }

        if (!TryFindGatherable(itemId, out var item))
            return false;

        list ??= GetOrCreateIpcList(manager);
        manager.AddItem(list, item);
        if (!list.Quantities.ContainsKey(item))
            return false;

        if (amount > 1)
            manager.ChangeQuantity(list, item, amount);
        return true;
    }

    private int GetQuantityOnFramework(uint itemId)
    {
        var list = FindIpcList(_plugin.AutoGatherListsManager);
        var idx  = list == null ? -1 : IndexOfItem(list, itemId);
        return idx < 0 ? 0 : (int)list!.Quantities[list.Items[idx]];
    }

    private static List<uint> ListTreasureMapsOnFramework()
        => GatherBuddy.GameData.Gatherables.Values
            .Where(IsReachableTreasureMap)
            .OrderByDescending(g => g.Level)
            .ThenByDescending(g => g.ItemId)
            .Select(g => g.ItemId)
            .ToList();

    private static bool IsReachableTreasureMap(Gatherable gatherable)
        => gatherable.IsTreasureMap && gatherable.NodeList.SelectMany(n => n.WorldPositions.Values).SelectMany(p => p).Any();

    private int GatherTreasureMapOnFramework(uint itemId)
    {
        if (!Dalamud.ClientState.IsLoggedIn || Dalamud.Objects.LocalPlayer == null)
            return MapNotLoggedIn;

        if (!GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var map) || !IsReachableTreasureMap(map))
            return MapNotGatherable;

        // 下面三項都是自動採集自己會靜默跳過的條件（ActiveItemList.UpdateItemsToGather）：先講清楚，不要開了卻什麼都不採。
        if (map.GetInventoryCount() > 0)
            return MapAlreadyHeld;

        // MinValue＝遊戲還沒給時間，不擋：自動採集一開就會自己去要。
        var allowance = DiscipleOfLand.NextTreasureMapAllowance;
        if (allowance != DateTime.MinValue && allowance >= AutoGather.AutoGather.AdjustedServerTime.DateTime)
            return MapAllowanceNotUp;

        if (!HasNodeWithinLevel(map))
            return MapLevelTooLow;

        if (!SetQuantityOnFramework(itemId, 1))
            return MapNotGatherable;

        // 清單停用或這一項沒勾時地圖進不了採集目標：開了自動採集也不會去採，不回「已開始」。
        if (!_plugin.AutoGatherListsManager.ActiveItems.Any(a => a.Item.ItemId == itemId))
        {
            if (_lastListDisabledNotice != itemId)
            {
                _lastListDisabledNotice = itemId;
                Communicator.PrintError($"其他插件請 GatherBuddy 採「{map.Name}」，但「{IpcListName}」清單或清單裡的這張地圖已停用，沒有開始自動採集；"
                  + "要採的話請到自動採集分頁把它勾回。");
            }

            GatherBuddy.Log.Information($"[GatherBuddy IPC] 別的插件交來寶物地圖 {itemId}（{map.Name}），但「{IpcListName}」清單或該項目已停用：不開自動採集，回 {MapListDisabled}。");
            return MapListDisabled;
        }

        _lastListDisabledNotice = 0;
        var wasRunning = GatherBuddy.AutoGather.Enabled;
        GatherBuddy.AutoGather.Enabled = true;
        GatherBuddy.Log.Information($"[GatherBuddy IPC] 別的插件替使用者交來一張寶物地圖 {itemId}（{map.Name}）：已放進「來自其他插件」清單，"
          + (wasRunning ? "自動採集本來就開著。" : "已開啟自動採集。"));
        return wasRunning ? MapAlreadyRunning : MapStarted;
    }

    /// <summary>與自動採集挑採集點時的等級篩選相同（等級捨入到 5 的倍數）。</summary>
    private static bool HasNodeWithinLevel(Gatherable map)
    {
        var minerLevel    = (DiscipleOfLand.MinerLevel + 5) / 5 * 5;
        var botanistLevel = (DiscipleOfLand.BotanistLevel + 5) / 5 * 5;
        return map.NodeList.Any(n => n.GatheringType.ToGroup() switch
        {
            GatheringType.Miner    => n.Level <= minerLevel,
            GatheringType.Botanist => n.Level <= botanistLevel,
            _                      => false,
        });
    }

    /// <summary>清單上道具 id 相符的第一項；-1 ＝ 沒有。刻意用 id 比對、不經過節點資料的篩選，已在清單上的一定找得到。</summary>
    private static int IndexOfItem(AutoGatherList list, uint itemId)
    {
        var items = list.Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].ItemId == itemId)
                return i;
        }

        return -1;
    }

    /// <summary>與 AutoGatherList.NormalizeQuantity 的上限一致。</summary>
    private const int MaxQuantity = 999999;

    private static AutoGatherList? FindIpcList(AutoGatherListsManager manager)
    {
        foreach (var existing in manager.Lists)
        {
            if (!existing.Fallback && existing.Name == IpcListName)
                return existing;
        }

        return null;
    }

    private static AutoGatherList GetOrCreateIpcList(AutoGatherListsManager manager)
    {
        if (FindIpcList(manager) is { } found)
            return found;

        // Enabled = true 與右鍵選單的 CreateAndAddPreset 一致：清單啟用不等於開始採集,
        // 自動採集本身仍要使用者自己開。
        var list = new AutoGatherList
        {
            Name        = IpcListName,
            Description = IpcListDescription,
            Enabled     = true,
        };
        manager.AddList(list);
        return list;
    }

    /// <summary>
    /// 採集品要真的有帶世界座標的節點才收，判準與自動採集分頁的候選清單相同；
    /// 沒有節點的道具加進去只會讓自動採集永遠找不到它。
    /// </summary>
    private static bool TryFindGatherable(uint itemId, out IGatherable item)
    {
        if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable)
         && gatherable.NodeList.SelectMany(n => n.WorldPositions.Values).SelectMany(p => p).Any())
        {
            item = gatherable;
            return true;
        }

        if (GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish) && !fish.IsTreasureMap)
        {
            item = fish;
            return true;
        }

        item = null!;
        return false;
    }

    public void Dispose()
    {
        // EzIPC will handle disposal automatically through ECommonsMain.Dispose()
    }
}
