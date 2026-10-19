using System;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Plugin;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{

    unsafe int SpiritbondMax
    {
        get
        {
            if (!GatherBuddy.Config.AutoGatherConfig.DoMaterialize) return 0;

            var inventory = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
            var result    = 0;
            for (var slot = 0; slot < inventory->Size; slot++)
            {
                var inventoryItem = inventory->GetInventorySlot(slot);
                if (inventoryItem == null || inventoryItem->ItemId <= 0)
                    continue;

                //GatherBuddy.Log.Debug("Slot " + slot + " has " + inventoryItem->Spiritbond + " Spiritbond");
                if (inventoryItem->SpiritbondOrCollectability == 10000)
                {
                    result++;
                }
            }

            return result;
        }
    }

    private Random _rng = new();
    unsafe void DoMateriaExtraction()
    {
        if (!QuestManager.IsQuestComplete(66174))
        {
            GatherBuddy.Config.AutoGatherConfig.DoMaterialize = false;
            Communicator.PrintError("[GatherBuddy Reborn] Materia Extraction enabled but relevant quest not complete yet. Feature disabled.".Loc());
            return;
        }
        if (MaterializeAddon == null)
        {
            TaskManager.Enqueue(StopNavigation);
            EnqueueActionWithDelay(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 14));
            TaskManager.Enqueue(() => MaterializeAddon != null);
            return;
        }

        TaskManager.Enqueue(YesAlready.Lock);
        // Materialize 每輪對同一扇窗重送 (2,0) 是合法的多次互動,用 15 幀逃生口;但關窗(-1)之後同位址任何按法都不准,
        // 擋住「精製沒成功、窗仍在關閉幀 → 下一輪再送 (2,0)」那條路。
        EnqueueActionWithDelay(() => { if (MaterializeAddon is var addon and not null && AddonPressGuard.TryBeginPress("Materialize", &addon->AtkUnitBase, AddonPressGuard.BuildPressKey(true, 2, 0), AddonPressGuard.RoutineRePressEscapeFrames)) Callback.Fire(&addon->AtkUnitBase, true, 2, 0); });
        TaskManager.Enqueue(() => MaterializeDialogAddon != null, 1000);
        EnqueueActionWithDelay(() => { if (MaterializeDialogAddon is var addon and not null && AddonPressGuard.TryBeginPress("MaterializeDialog", &addon->AtkUnitBase, "Materialize")) new MaterializeDialog(addon).Materialize(); });
        TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.Occupied39]);
        TaskManager.DelayNext(_rng.Next(500, 2000));

        if (SpiritbondMax == 1) 
        {
            EnqueueActionWithDelay(() => { if (MaterializeAddon is var addon and not null && AddonPressGuard.TryBeginPress("Materialize", &addon->AtkUnitBase, AddonPressGuard.ClosePressKey)) Callback.Fire(&addon->AtkUnitBase, true, -1); });
            TaskManager.Enqueue(YesAlready.Unlock);
        }
    }
}
