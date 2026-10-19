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

            // ⚠️ 只捕獲 GameObjectId,執行時重查物件表:這個任務在後續的幀才執行,而本 pin 的 ObjectTable 包裝是
            // 每格重用、存取時就地改寫 Address ⇒ 跨幀持有的 gameObject 會靜默換人或懸空;IGameObject.IsValid()
            // 只檢查「玩家有沒有登入」,不驗證位址。採集點在那之前消失(採完枯竭、換區、其他人採走)就是
            // 攔不到的 AccessViolation。查不到就放棄這次互動。
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
                // Match by ItemId rather than `x.Item == slot.Item`: Gatherable doesn't override equality, so the
                // instance backing `_activeItemList` and the one resolved fresh from GameData.Gatherables can differ
                // for the exact same item. A reference mismatch falls through to the struct default (Quantity = 0),
                // GetNextAction() sees itemsLeft <= 0 and abandons the node without using a single collectable action.
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
        /// Reverse index of GatheringNode.WorldPositions: an in-world gathering point object's DataId is a
        /// GatheringPoint row id, and every GatheringPoint row id belonging to a node is a key of that node's
        /// WorldPositions (see the GatheringNode constructor). MarkVisited() and GetNextOrDefault() rely on the
        /// same relation but only search the active item list, which is empty once the requested amounts have
        /// been gathered. Built once from static sheet data, which never changes at runtime.
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
        /// True when we are at a node with a limited uptime window and the user asked us to spend its
        /// remaining gathering attempts instead of leaving as soon as the requested amounts are met.
        /// Times.AlwaysUp() is false for exactly the unspoiled, legendary and ephemeral nodes (GetTimes()
        /// returns NodeType.Regular whenever the uptime covers all 24 hours). Attempts on such a node are a
        /// scarce resource - it will not come back until its next window - so throwing the leftovers away is
        /// a waste. Regular nodes respawn freely and are deliberately left alone.
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

            // TC note: a slot can hold a real item that GameData.Gatherables doesn't recognize, which makes it
            // null-Item, so it can never match the gather/fallback lists above - a wanted-but-unresolved item would
            // look like "nothing to gather here" and the AbandonNodes check right below would abandon the node.
            // Try it before honoring AbandonNodes. Enabled and a non-zero GatherChance are required: a slot the
            // game refuses every time (0% chance) was retried forever, spamming "Firing callback: Gathering".
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

            // Everything above refuses collectables, so on an unspoiled or legendary node - which usually holds
            // nothing but collectables - we would close the window with attempts still on the node. Take a
            // collectable too when we are supposed to use the node up. `available` is already stripped by
            // CheckItemOvercap (held treasure maps, crystals near the 9999 cap); maps are excluded again here so a
            // leftover attempt never burns the daily allowance. Enabled: a greyed-out slot would retry forever.
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
