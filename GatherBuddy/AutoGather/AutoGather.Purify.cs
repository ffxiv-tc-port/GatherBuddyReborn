using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Plugin;
using Dalamud.Game.ClientState.Conditions;
using PurifyResult = ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.PurifyResult;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.EzSharedDataManager;
using ECommons.LanguageHelpers;
using GatherBuddy.AutoGather.Helpers;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private bool HasReducibleItems()
        {
            if (!GatherBuddy.Config.AutoGatherConfig.DoReduce || Svc.Condition[ConditionFlag.Mounted])
                return false;

            if (!QuestManager.IsQuestComplete(67633)) // No Longer a Collectable
            {
                GatherBuddy.Config.AutoGatherConfig.DoReduce = false;
                Communicator.PrintError(
                    "[GatherBuddyReborn] Aetherial reduction is enabled, but the relevant quest has not been completed yet. The feature has been disabled.".Loc());
                return false;
            }

            unsafe
            {
                var manager = InventoryManager.Instance();
                if (manager == null)
                    return false;

                foreach (var invType in InventoryTypes)
                {
                    var container = manager->GetInventoryContainer(invType);
                    if (container == null || !container->IsLoaded)
                        continue;

                    for (int i = 0; i < container->Size; i++)
                    {
                        var slot = container->GetInventorySlot(i);
                        if (slot != null
                         && slot->ItemId != 0
                         && GatherBuddy.GameData.Gatherables.TryGetValue(slot->ItemId, out var gatherable)
                         && gatherable.ItemData.AetherialReduce != 0)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        private unsafe void ReduceItems(bool reduceAll)
        {
            AutoStatus = "Aetherial reduction".Loc();
            var delay = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
            TaskManager.Enqueue(StopNavigation);
            TaskManager.Enqueue(YesAlready.Lock);
            if (PurifyItemSelectorAddon == null)
            {
                EnqueueActionWithDelay(() => { ActionManager.Instance()->UseAction(ActionType.GeneralAction, 21); });
                // Prevent the "Unable to execute command while occupied" message right after entering a house.
                TaskManager.DelayNext(500);
            }

            TaskManager.Enqueue(ReduceFirstItem,                                3000, true, "Reduce first item");
            TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.Occupied39], 5000, true, "Wait until first item reduction is complete");
            TaskManager.DelayNext(delay);
            // ⚠️ 逾時預算要大於守衛的逃生口:StartAutoReduction 走 AddonPressGuard 的預設 90 幀逃生口,
            // 而那個「幀」是 framework tick —— 60fps 下約 1.5 秒、30fps 下約 3 秒。原本的 1000ms 比它短,
            // 逃生口根本走不到:守衛一擋就是擋到 abortOnTimeout 觸發,清掉整條精選佇列(含後面的 YesAlready.Unlock)。
            // 改成和上面「Reduce first item」同樣的 3000ms,讓逃生口有機會先放行再談逾時。
            // (佇列被清不會死鎖 —— DoAutoGather 在 TaskManager 閒置時會無條件 YesAlready.Unlock —— 但那是自癒,不是設計。)
            TaskManager.Enqueue(StartAutoReduction,                             3000, true, "Start auto reduction");
            TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.Occupied39], 180000, true, "Wait until all items have been reduced");
            TaskManager.DelayNext(delay);
            TaskManager.Enqueue(() =>
            {
                EnqueueActionWithDelay(() =>
                {
                    if (PurifyResultAddon is var addon and not null
                     && AddonPressGuard.TryBeginPress("PurifyResult", addon, AddonPressGuard.ClosePressKey))
                        Callback.Fire(addon, true, -1);
                });
                if (reduceAll && HasReducibleItems())
                    ReduceItems(true);
                else
                    EnqueueActionWithDelay(() =>
                    {
                        if (PurifyItemSelectorAddon is var addon and not null
                         && AddonPressGuard.TryBeginPress("PurifyItemSelector", addon, AddonPressGuard.ClosePressKey))
                            Callback.Fire(addon, true, -1);
                    });
            });
            TaskManager.Enqueue(YesAlready.Unlock);
        }

        private unsafe bool? ReduceFirstItem()
        {
            var addon = PurifyItemSelectorAddon;
            if (addon == null)
                return false;

            // 同一扇 selector 每輪精選合法重送同一組參數(reduceAll 遞迴),用多次互動窗的 15 幀逃生口;關窗(-1)之後同位址不准。
            // 被擋下回 false =「這一輪沒按到,下一 tick 再來」,與 addon 還沒出現走同一條路。🔴 不回 null(那是 Abort)。
            if (!AddonPressGuard.TryBeginPress("PurifyItemSelector", addon, AddonPressGuard.BuildPressKey(true, 12, 0u), AddonPressGuard.RoutineRePressEscapeFrames))
                return false;

            Callback.Fire(addon, true, 12, 0u);
            return true;
        }

        private unsafe bool? StartAutoReduction()
        {
            var addon = PurifyResultAddon;
            if (addon == null)
                return false;

            if (!AddonPressGuard.TryBeginPress("PurifyResult", addon, "Automatic"))
                return false;

            new PurifyResult(addon).Automatic();
            return true;
        }
    }
}
