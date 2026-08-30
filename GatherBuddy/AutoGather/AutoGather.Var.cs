using System;
using Dalamud.Game.ClientState.Conditions;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using GatherBuddy.Time;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Numerics;
using ECommons;
using ECommons.MathHelpers;
using GatherBuddy.AutoGather.AtkReaders;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        public bool IsPathing
            => VNavmesh.Path.IsRunning();

        public bool IsPathGenerating
            => VNavmesh.Nav.PathfindInProgress();

        public bool NavReady
            => VNavmesh.Nav.IsReady();

        private bool IsBlacklisted(Vector3 g)
        {
            var blacklisted = GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId.ContainsKey(Dalamud.ClientState.TerritoryType)
             && GatherBuddy.Config.AutoGatherConfig.BlacklistedNodesByTerritoryId[Dalamud.ClientState.TerritoryType].Contains(g);
            return blacklisted;
        }

        public bool IsGathering
            => Dalamud.Conditions[ConditionFlag.Gathering] || Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction];

        public bool IsFishing
            => Dalamud.Conditions[ConditionFlag.Fishing];

        public  bool?      LastNavigationResult { get; set; }         = null;
        public  Vector3    CurrentDestination   { get; private set; } = default;
        public  Angle      CurrentRotation      { get; private set; } = default;
        private ILocation? CurrentFarNodeLocation;
        public bool LureSuccess { get; private set; } = false;

        public unsafe GatheringReader? GatheringWindowReader
            => GenericHelpers.TryGetAddonByName("Gathering", out AtkUnitBase* addon)
                ? new GatheringReader(addon)
                : null;

        public unsafe GatheringMasterpieceReader? MasterpieceReader
            => GenericHelpers.TryGetAddonByName("GatheringMasterpiece", out AtkUnitBase* add)
                ? new GatheringMasterpieceReader(add)
                : null;

        public static IReadOnlyList<InventoryType> InventoryTypes { get; } =
        [
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        ];

        public GatheringType JobAsGatheringType
        {
            get
            {
                var job = Player.Job;
                switch (job)
                {
                    case Job.MIN: return GatheringType.Miner;
                    case Job.BTN: return GatheringType.Botanist;
                    case Job.FSH: return GatheringType.Fisher;
                    default:      return GatheringType.Unknown;
                }
            }
        }

        public bool ShouldUseFlag
            => !GatherBuddy.Config.AutoGatherConfig.DisableFlagPathing;

        public bool ShouldFly(Vector3 destination)
        {
            if (Dalamud.Conditions[ConditionFlag.InFlight] || Dalamud.Conditions[ConditionFlag.Diving])
                return true;

            if (GatherBuddy.Config.AutoGatherConfig.ForceWalking || Dalamud.Objects.LocalPlayer == null)
            {
                return false;
            }

            return Vector3.Distance(Dalamud.Objects.LocalPlayer.Position, destination)
             >= GatherBuddy.Config.AutoGatherConfig.MountUpDistance;
        }

        /// <summary>
        /// 給除錯分頁的手動導航按鈕用的 ShouldFly。
        /// vnavmesh 收到 fly=true 但玩家沒騎坐騎時會直接停用移動並返回,表現是站著不動且
        /// 完全沒有訊息;vnavmesh 也不會替呼叫端上坐騎。這裡在呼叫前先把飛行降級成地面移動。
        /// 刻意不代替使用者上坐騎 —— 手動按鈕不引入自動化。
        /// </summary>
        public bool ShouldFlyManual(Vector3 destination)
            => DowngradeFlyIfNotMounted(ShouldFly(destination));

        /// <summary>
        /// 未騎乘坐騎時把飛行需求降為地面移動,並留下 Information 級記錄。
        /// 只給手動觸發的按鈕使用(離散事件,不需節流);自動採集主流程另有 EnqueueMountUp。
        /// </summary>
        public static bool DowngradeFlyIfNotMounted(bool shouldFly)
        {
            if (!shouldFly)
                return false;

            if (Dalamud.Conditions[ConditionFlag.Mounted]
             || Dalamud.Conditions[ConditionFlag.InFlight]
             || Dalamud.Conditions[ConditionFlag.Diving])
                return true;

            GatherBuddy.Log.Information(
                "手動導航:目前沒有騎乘坐騎,已改用地面路徑移動。(vnavmesh 收到飛行指令但未騎乘時會直接停住不動,且不會自動上坐騎)");
            return false;
        }

        public unsafe Vector2? TimedNodePosition
        {
            get
            {
                // 🔴 AgentMap.Instance() 由 [Agent(AgentId.Map)] 產生:內部鏈
                //    AgentModule -> UIModule -> Framework,任一層回 null 整條就回 null
                //    (登入前、切場景、登出後都是常態),底層 [StaticAddress]/[MemberFunction]
                //    特徵碼失配時改為擲 InvalidOperationException——兩種失效模式並存,
                //    只擋一種等於假防護。裸解參考 null 原生指標是 AccessViolationException,
                //    在 .NET Core 屬 corrupted-state exception,try/catch 攔不到 ⇒ 只能事前判空。
                //    ⚠️ 下面原有的 markers == null 擋不到這件事:MiniMapGatheringMarkers 是
                //    FixedSizeArray6 產生的 Span 屬性,它是從 map 的位址算出來的,map 為 null
                //    時 span 照樣「建得出來」,真正的爆點在 foreach 讀取那一刻。
                //    這裡跑在自動採集的每輪判斷(高頻),所以 fail-closed 靜默回 null,不寫 log。
                FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap* map;
                try
                {
                    map = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
                }
                catch
                {
                    return null;
                }

                if (map == null)
                    return null;

                var markers = map->MiniMapGatheringMarkers;
                if (markers == null)
                    return null;

                Vector2? result = null;
                foreach (var miniMapGatheringMarker in markers)
                {
                    if (miniMapGatheringMarker.MapMarker.X != 0 && miniMapGatheringMarker.MapMarker.Y != 0)
                    {
                        // ReSharper disable twice PossibleLossOfFraction
                        result = new Vector2(miniMapGatheringMarker.MapMarker.X / 16, miniMapGatheringMarker.MapMarker.Y / 16);
                        break;
                    }
                    // GatherBuddy.Log.Information(miniMapGatheringMarker.MapMarker.IconId +  " => X: " + miniMapGatheringMarker.MapMarker.X / 16 + " Y: " + miniMapGatheringMarker.MapMarker.Y / 16);
                }

                return result;
            }
        }

        public  string      AutoStatus { get; private set; } = "Idle";
        public  int         LastCollectability = 0;
        public  int         LastIntegrity      = 0;
        private bool LuckUsed;
        private bool        WentHome;

        internal IEnumerable<GatherTarget> ItemsToGather
            => _activeItemList;

        internal ReadOnlyDictionary<GatheringNode, TimeInterval> DebugVisitedTimedLocations
            => _activeItemList.DebugVisitedTimedLocations;

        public readonly HashSet<Vector3> FarNodesSeenSoFar = [];
        public readonly LinkedList<uint> VisitedNodes      = [];

        private IEnumerator<Actions.BaseAction?>? ActionSequence;

        private static unsafe T* GetAddon<T>(string name) where T : unmanaged
        {
            var addon = (AtkUnitBase*)Dalamud.GameGui.GetAddonByName(name).Address;
            if (addon != null && addon->IsFullyLoaded() && addon->IsReady)
                return (T*)addon;
            else
                return null;
        }

        public static unsafe AddonGathering* GatheringAddon
            => GetAddon<AddonGathering>("Gathering");

        public static unsafe AddonGatheringMasterpiece* MasterpieceAddon
            => GetAddon<AddonGatheringMasterpiece>("GatheringMasterpiece");

        public static unsafe AddonMaterializeDialog* MaterializeAddon
            => GetAddon<AddonMaterializeDialog>("Materialize");

        public static unsafe AddonMaterializeDialog* MaterializeDialogAddon
            => GetAddon<AddonMaterializeDialog>("MaterializeDialog");

        public static unsafe AddonSelectYesno* SelectYesnoAddon
            => GetAddon<AddonSelectYesno>("SelectYesno");

        public static unsafe AtkUnitBase* PurifyItemSelectorAddon
            => GetAddon<AtkUnitBase>("PurifyItemSelector");

        public static unsafe AtkUnitBase* PurifyResultAddon
            => GetAddon<AtkUnitBase>("PurifyResult");

        public static unsafe AddonRepair* RepairAddon
            => GetAddon<AddonRepair>("Repair");

        public IEnumerable<IGatherable> ItemsToGatherInZone
            => _activeItemList.Where(i => i.Node?.Territory.Id == Dalamud.ClientState.TerritoryType).Select(i => i.Item);

        private bool LocationMatchesJob(ILocation loc)
            => loc.GatheringType.ToGroup() == JobAsGatheringType;

        public bool CanAct
        {
            get
            {
                if (Dalamud.Objects.LocalPlayer == null)
                    return false;
                if (Dalamud.Conditions[ConditionFlag.BetweenAreas]
                 || Dalamud.Conditions[ConditionFlag.BetweenAreas51]
                 || Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent]
                 || Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell]
                 || Dalamud.Conditions[ConditionFlag.BeingMoved]
                 || Dalamud.Conditions[ConditionFlag.Casting]
                 || Dalamud.Conditions[ConditionFlag.Casting87]
                 || Dalamud.Conditions[ConditionFlag.Jumping]
                 || Dalamud.Conditions[ConditionFlag.Jumping61]
                 || Dalamud.Conditions[ConditionFlag.LoggingOut]
                 || Dalamud.Conditions[ConditionFlag.Occupied]
                 || Dalamud.Conditions[ConditionFlag.Occupied39]
                 || Dalamud.Conditions[ConditionFlag.Unconscious]
                 || Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction]
                 || Dalamud.Conditions[ConditionFlag.MountOrOrnamentTransition] // Mounting up
                    //Node is open? Fades off shortly after closing the node, can't use items (but can mount) while it's set
                 || Dalamud.Conditions[85] && !Dalamud.Conditions[ConditionFlag.Gathering]
                 || Dalamud.Objects.LocalPlayer.IsDead
                 || Player.IsAnimationLocked)
                    return false;

                return true;
            }
        }

        private static unsafe bool HasGivingLandBuff
            => Dalamud.Objects.LocalPlayer?.StatusList.Any(s => s.StatusId == 1802) ?? false;

        public static unsafe bool IsGivingLandOffCooldown
            => ActionManager.Instance()->IsActionOffCooldown(ActionType.Action, Actions.GivingLand.ActionId);

        //Should be near the upper bound to reduce the probability of overcapping.
        private const int GivingLandYield = 30;

        private static unsafe uint FreeInventorySlots
            => InventoryManager.Instance()->GetEmptySlotsInBag();

        public static TimeStamp AdjustedServerTime
            => GatherBuddy.Time.ServerTime.AddSeconds(GatherBuddy.Config.AutoGatherConfig.TimedNodePrecog);

        private ConfigPreset MatchConfigPreset(Gatherable? item)
            => _plugin.Interface.MatchConfigPreset(item);

        private ConfigPreset MatchConfigPreset(Fish? item)
            => _plugin.Interface.MatchConfigPreset(item);
    }
}
