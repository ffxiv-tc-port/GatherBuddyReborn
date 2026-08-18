using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Classes;
using System.Linq;
using System.Runtime.InteropServices;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.AutoGather.AtkReaders;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.Lists;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private unsafe void EnqueueNodeInteraction(IGameObject gameObject, Gatherable targetItem)
        {
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return;

            // ⚠️ .Address 寫在 lambda「內部」也沒用:Dalamud 的 GameObject.Address
            // 在建構時就凍結、永不重新解析(GameObject.cs:137-139),所以晚點讀等於
            // 讀到當初那個值。而 IGameObject.IsValid() 只檢查「玩家有沒有登入」、
            // 完全不驗證位址(GameObject.cs:170-177)。
            // 這個任務是在後續的幀才執行的,採集點在那之前消失(採完枯竭、換區、
            // 其他人採走)就會解參考已釋放的記憶體 → 攔不到的 AccessViolation。
            // 正解:只捕獲 GameObjectId,執行時重查物件表,查不到就放棄這次互動。
            var nodeId = gameObject.GameObjectId;

            TaskManager.Enqueue(() =>
            {
                var node = Svc.Objects.SearchById(nodeId);
                if (node == null)
                    return;

                targetSystem->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)node.Address);
            });
            TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.Gathering], 500);
        }

        private unsafe void EnqueueGatherItem(ItemSlot slot)
        {
            // TC note: slot.Item can be null here for the raw-fallback case in
            // GetItemSlotToGather (an item ID unresolved in GameData.Gatherables on
            // TC's older patch data) - slot.IsCollectable is read straight from the
            // addon's own flags, so it doesn't need Item to be resolved.
            if (slot.Item != null && slot.IsCollectable)
            {
                // Since it's possible that we are not gathering the top item in the list,
                // we need to remember what we are going to gather inside MasterpieceAddon
                //
                // Matching by ItemId rather than `x.Item == slot.Item`: if `Gatherable`
                // doesn't override equality, that compared object references, which can
                // differ between the instance backing `_activeItemList` and the instance
                // resolved fresh from GameData.Gatherables for this slot even for the
                // exact same item. A reference mismatch silently fell through to the
                // struct default (Quantity = 0), which made GetNextAction() see
                // itemsLeft <= 0 on the very first call and immediately abandon the node
                // (via AbandonNodes) without ever using a single collectable action -
                // matching "opens the Masterpiece window, does nothing, goes back."
                var quantity = _activeItemList.FirstOrDefault(x => x.Item?.ItemId == slot.Item.ItemId).Quantity;
                CurrentCollectableRotation =
                    new CollectableRotation(MatchConfigPreset(slot.Item), slot.Item, quantity, ShouldUseUpCurrentNode);
            }

            EnqueueActionWithDelay(slot.Gather);

            if (slot.Item?.IsTreasureMap ?? false)
            {
                TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction], 1000);
                TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.ExecutingGatheringAction]);
                TaskManager.Enqueue(DiscipleOfLand.RefreshNextTreasureMapAllowance);
            }
        }

        /// <summary>
        /// Reverse index of GatheringNode.WorldPositions: an in-world gathering point object's
        /// DataId is a GatheringPoint row id, and every GatheringPoint row id belonging to a
        /// node is a key of that node's WorldPositions (see the GatheringNode constructor).
        /// This is the same DataId-to-node relation AutoGather already relies on in
        /// MarkVisited() and GetNextOrDefault(), except those only search the active item
        /// list, which is exactly empty once the requested amounts have been gathered.
        /// Built once from static sheet data, which never changes at runtime.
        /// </summary>
        private static Dictionary<uint, GatheringNode>? _nodesByGatheringPointId;

        private static GatheringNode? FindNodeByObjectDataId(uint dataId)
        {
            if (_nodesByGatheringPointId == null)
            {
                var map = new Dictionary<uint, GatheringNode>();
                foreach (var node in GatherBuddy.GameData.GatheringNodes.Values)
                foreach (var pointId in node.WorldPositions.Keys)
                    map[pointId] = node;
                _nodesByGatheringPointId = map;
            }

            return _nodesByGatheringPointId.GetValueOrDefault(dataId);
        }

        /// <summary>
        /// The node we are actually standing at, resolved from the gathering point object we
        /// have targeted. Falls back to the node auto-gather navigated to if the target got
        /// lost (the game keeps the node targeted for the whole gathering session, so the
        /// fallback is only for the "enabled auto-gather while already inside a node" case).
        /// </summary>
        private GatheringNode? CurrentNode
        {
            get
            {
                var obj = Svc.Targets.Target ?? Svc.Targets.PreviousTarget;
                if (obj is { ObjectKind: ObjectKind.GatheringPoint })
                {
                    var node = FindNodeByObjectDataId(obj.BaseId);
                    if (node != null)
                        return node;
                }

                return _currentGatherTarget?.Node;
            }
        }

        /// <summary>
        /// True when we are at a node with a limited uptime window and the user asked us to
        /// spend its remaining gathering attempts instead of leaving as soon as the requested
        /// amounts are met. Times.AlwaysUp() is false for exactly the unspoiled, legendary and
        /// ephemeral nodes: GatheringNode.GetTimes() builds Times from GatheringPointTransient
        /// and returns NodeType.Regular whenever the resulting uptime covers all 24 hours, so
        /// "not always up" and "not a regular node" are the same condition by construction.
        /// Attempts on such a node are a scarce resource - the node is consumed by visiting it
        /// and will not come back until its next window - so throwing the leftovers away is a
        /// waste. Regular nodes respawn freely and are deliberately left alone.
        /// </summary>
        internal bool ShouldUseUpCurrentNode
            => GatherBuddy.Config.AutoGatherConfig.FinishTimedNodes
             && (!CurrentNode?.Times.AlwaysUp() ?? false);

        /// <summary>
        /// Checks if desired item could or should be gathered and may change it to something more suitable
        /// </summary>
        /// <returns>UseSkills: True if the selected item is in the gathering list; false if we gather a collectable or some unneeded junk
        /// Slot: ItemSlot of item to gather</returns>
        private (bool UseSkills, ItemSlot Slot) GetItemSlotToGather(IEnumerable<GatherTarget> gatherTarget)
        {
            if (GatheringWindowReader == null)
                throw new InvalidOperationException("GatheringWindowReader is null");
            var available = GatheringWindowReader.ItemSlots
                .Where(i => !i.IsEmpty)
                .Where(CheckItemOvercap)
                .ToList();

            if (GatherBuddy.Config.AutoGatherConfig.AlwaysGatherMaps && available.Any(i => i.Item.IsTreasureMap) && DiscipleOfLand.NextTreasureMapAllowance < GatherBuddy.Time.ServerTime.DateTime)
            {
                return (false, available.First(i => i.Item.IsTreasureMap));
            }

            var target = available.FirstOrDefault(a => gatherTarget.Any(i => i.Gatherable?.ItemId == a.Item.ItemId));

            //Gather crystals when using The Giving Land
            if (HasGivingLandBuff && (target == null || !target.Item.IsCrystal))
            {
                var crystal = GetAnyCrystalInNode();
                if (crystal != null)
                    return (true, crystal);
            }

            if (target != null && target.Item.GetInventoryCount() < gatherTarget.First(t => t.Gatherable?.ItemId == target.Item.ItemId).Quantity)
            {
                //The target item is found in the node, would not overcap and we need to gather more of it
                return (!target.IsCollectable, target);
            }

            //Items in the gathering list
            var gatherList = ItemsToGather
                //Join node slots, retaining list order
                .Join(available, i => i.Item, s => s.Item, (i, s) => (Slot: s, i.Quantity))
                //And we need more of them
                .Where(x => x.Slot.Item.GetInventoryCount() < x.Quantity)
                .Select(x => x.Slot);

            //Items in the fallback list
            var fallbackList = _plugin.AutoGatherListsManager.FallbackItems
                //Join node slots, retaining list order
                .Join(available, i => i.Item, s => s.Item, (i, s) => (Slot: s, i.Quantity))
                //And we need more of them
                .Where(x => x.Slot.Item.GetInventoryCount() < x.Quantity)
                .Select(x => x.Slot);

            var fallbackSkills = GatherBuddy.Config.AutoGatherConfig.UseSkillsForFallbackItems;

            //If there is any other item that we want in the node, gather it
            var slot = gatherList.FirstOrDefault();
            if (slot != null)
            {
                return (!slot.IsCollectable, slot);
            }

            //If there is any fallback item, gather it
            slot = fallbackList.FirstOrDefault();
            if (slot != null)
            {
                return (fallbackSkills && !slot.IsCollectable, slot);
            }

            // TC note: a slot can hold a real item that GameData.Gatherables doesn't
            // recognize (older TC patch data), which makes it null-Item. That null Item
            // means it can never match the user's gather/fallback lists above (the Join
            // there matches by resolved Gatherable object identity), even when the item
            // genuinely is one the user wants - so without this, a wanted-but-unresolved
            // item looked indistinguishable from "nothing to gather here" and the
            // AbandonNodes check right below would immediately abandon the node without
            // ever attempting to gather anything. Try it before honoring AbandonNodes.
            // Must also require Enabled and a non-zero GatherChance - without this, a
            // slot with a 0% gather chance (game refuses the attempt every time) got
            // retried forever, spamming "Firing callback: Gathering" every tick with
            // the game rejecting it and never actually gathering anything.
            var unresolvedSlot = GatheringWindowReader!.ItemSlots
                .FirstOrDefault(s => !s.IsEmpty && s.Item == null && !s.IsCollectable && s.Enabled && s.GatherChance > 0);
            if (unresolvedSlot != null)
            {
                return (false, unresolvedSlot);
            }

            //Check if we should and can abandon the node.
            //Timed nodes are exempt when the user asked us to use them up: their remaining
            //gathering attempts are gone for good once we walk away, so we keep going and
            //let the node close itself when its integrity runs out.
            if (GatherBuddy.Config.AutoGatherConfig.AbandonNodes && !ShouldUseUpCurrentNode)
                throw new NoGatherableItemsInNodeException();

            if (target != null)
            {
                //Gather unneeded target item as a fallback
                return (false, target);
            }

            //Gather any crystals
            slot = GetAnyCrystalInNode();
            if (slot != null)
            {
                return (false, slot);
            }
            //If there are no crystals, gather anything which is not treasure map nor collectable
            slot = available.FirstOrDefault(s => (!s.Item?.IsTreasureMap ?? false) && !s.IsCollectable);
            if (slot != null)
            {
                return (false, slot);
            }

            //Everything above refuses collectables, so on an unspoiled or legendary node -
            //which usually holds nothing but collectables - the fallbacks find nothing and we
            //would close the window with attempts still on the node. Take a collectable too
            //when we are supposed to use the node up. Overcap protection is unchanged: this
            //picks from `available`, which CheckItemOvercap has already stripped of treasure
            //maps we already hold and of crystals within a node's yield of the 9999 cap, and
            //maps are excluded again here so a leftover attempt never burns the daily
            //allowance. Enabled is required for the same reason as in the unresolved-slot
            //case above: clicking a greyed-out slot is silently refused by the game and would
            //retry forever instead of consuming integrity.
            if (ShouldUseUpCurrentNode)
            {
                slot = available.FirstOrDefault(s => (!s.Item?.IsTreasureMap ?? false) && s.Enabled);
                if (slot != null)
                {
                    return (false, slot);
                }
            }

            //Abort if there are no items we can gather
            throw new NoGatherableItemsInNodeException();
        }

        private bool CheckItemOvercap(ItemSlot s)
        {
            if (s.Item == null)
                return false;
            //If it's a treasure map, we can have only one in the inventory
            if (s.Item.IsTreasureMap && GetInventoryItemCount(s.Item.ItemId) != 0)
                return false;
            //If it's a crystal, we can't have more than 9999
            if (s.Item.IsCrystal && GetInventoryItemCount(s.Item.ItemId) > 9999 - s.Yield)
                return false;
            return true;
        }
        
        private ItemSlot? GetAnyCrystalInNode()
        {
            if (GatheringWindowReader == null)
                throw new InvalidOperationException("GatheringWindowReader is null");
            return GatheringWindowReader.ItemSlots
                .Where(s => s.Item != null)
                .Where(s => s.Item!.IsCrystal)
                .Where(CheckItemOvercap)
                //Prioritize crystals in the gathering list
                .GroupJoin(_activeItemList.Where(i => i.Gatherable?.IsCrystal ?? false), s => s.Item, i => i.Item, (s, x) => (Slot: s, Order: x.Any()?1:0))
                .OrderBy(x => x.Order)
                //Prioritize crystals with a lower amount in the inventory
                .ThenBy(x => x.Slot.Item!.GetInventoryCount())
                .Select(x => x.Slot)
                .FirstOrDefault();
        }
    }
}
