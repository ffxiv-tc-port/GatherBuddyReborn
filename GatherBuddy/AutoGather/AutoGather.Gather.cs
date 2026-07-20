using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Classes;
using System.Linq;
using System.Runtime.InteropServices;
using ECommons.Automation.UIInput;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.AutoGather.AtkReaders;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.Lists;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private unsafe void EnqueueNodeInteraction(IGameObject gameObject, Gatherable targetItem)
        {
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return;

            TaskManager.Enqueue(() => targetSystem->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address));
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
                CurrentCollectableRotation = new CollectableRotation(MatchConfigPreset(slot.Item), slot.Item, quantity);
            }

            EnqueueActionWithDelay(slot.Gather);

            if (slot.Item?.IsTreasureMap ?? false)
            {
                TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.Gathering42], 1000);
                TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Gathering42]);
                TaskManager.Enqueue(DiscipleOfLand.RefreshNextTreasureMapAllowance);
            }
        }

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

            //Check if we should and can abandon the node
            if (GatherBuddy.Config.AutoGatherConfig.AbandonNodes)
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
