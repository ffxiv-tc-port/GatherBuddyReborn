using ECommons.DalamudServices;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using Dalamud.Game.ClientState.Conditions;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.Automation;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.LanguageHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.AutoGather.Helpers;

namespace GatherBuddy.AutoGather;

public unsafe partial class AutoGather
{
    private Item? EquipmentNeedingRepair()
    {
        const int defaultThreshold = 5;
        var threshold = GatherBuddy.Config.AutoGatherConfig.DoRepair ? GatherBuddy.Config.AutoGatherConfig.RepairThreshold : defaultThreshold;

        var equippedItems = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        for (var i = 0; i < equippedItems->Size; i++)
        {
            var equippedItem = equippedItems->GetInventorySlot(i);
            if (equippedItem != null && equippedItem->ItemId > 0)
            {
                if (equippedItem->Condition / 300 <= threshold)
                {
                    return Svc.Data.Excel.GetSheet<Item>().GetRow(equippedItem->ItemId);
                }
            }
        }

        return null;
    }

    private bool HasRepairJob(Item itemToRepair)
    {
        if (itemToRepair.ClassJobRepair.RowId > 0)
        {
            var repairJobLevel =
                PlayerState.Instance()->ClassJobLevels[
                    Svc.Data.GetExcelSheet<ClassJob>()?.GetRow(itemToRepair.ClassJobRepair.RowId).ExpArrayIndex ?? 0];
            if (Math.Max(1, itemToRepair.LevelEquip - 10) <= repairJobLevel)
                return true;
        }

        return false;
    }

    private bool HasDarkMatter(Item itemToRepair)
    {
        var darkMatters = Svc.Data.Excel.GetSheet<ItemRepairResource>();
        foreach (var darkMatter in darkMatters)
        {
            if (darkMatter.Item.RowId < itemToRepair.ItemRepair.Value.Item.RowId)
                continue;

            if (GetInventoryItemCount(darkMatter.Item.RowId) > 0)
                return true;
        }

        return false;
    }

    private bool RepairIfNeeded()
    {
        if (Svc.Condition[ConditionFlag.Mounted] || Player.Job is not Job.BTN and not Job.MIN)
            return false;

        var itemToRepair = EquipmentNeedingRepair();

        if (itemToRepair == null)
            return false;

        if (!GatherBuddy.Config.AutoGatherConfig.DoRepair)
        {
            Communicator.PrintError("Your gear is almost broken. Repair it before enabling Auto-Gather.".Loc());
            AbortAutoGather("Repairs needed.");
            return true;
        }

        if (!HasRepairJob((Item)itemToRepair))
        {
            AbortAutoGather("Repairs needed, but no repair job found.");
            return true;
        }
        if (!HasDarkMatter((Item)itemToRepair))
        {
            AbortAutoGather("Repairs needed, but no dark matter found.");
            return true;
        }

        AutoStatus = "Repairing...".Loc();
        StopNavigation();
        YesAlready.Lock();

        var delay = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
        if (RepairAddon == null)
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 6);

        TaskManager.Enqueue(() => RepairAddon != null, 1000, true, "Wait until repair menu is ready.");
        TaskManager.DelayNext(delay);
        // 每一發都過 AddonPressGuard:同一扇窗(位址)同一按法在它走完生命週期前只送一次;關窗(-1)之後同位址任何按法都不准。
        // 🔴 這三顆一律寫成 Func<bool?> 多載(有回傳值)而不是 Action:ECommons 的 LegacyTaskManager 把 Enqueue(Action, …) 包成
        // () => { task(); return true; },被守衛擋下的 false 會被吞掉 —— 最後那顆關窗被吞就永遠不會再送,修理視窗就永遠開著。
        // 語意:真的按下去了才回 true;視窗還沒好或被守衛擋下回 false(下一 tick 再試);🔴 絕不回 null(那是 Abort(),會清掉整條佇列)。
        // ⚠️ 逾時 3000ms 要大於守衛的 90 tick 逃生口(60fps 約 1.5 秒、30fps 約 3 秒),否則一被擋就是擋到自己逾時。
        TaskManager.Enqueue(() =>
        {
            var addon = RepairAddon;
            if (addon == null)
                return false;

            if (!AddonPressGuard.TryBeginPress("Repair", &addon->AtkUnitBase, "RepairAll"))
                return false;

            new AddonMaster.Repair(addon).RepairAll();
            return true;
        }, 3000, "Repairing all.");
        TaskManager.Enqueue(() => SelectYesnoAddon != null, 1000, true, "Wait until YesnoAddon is ready.");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() =>
        {
            var addon = SelectYesnoAddon;
            if (addon == null)
                return false;

            if (!AddonPressGuard.TryBeginPress("SelectYesno", (AtkUnitBase*)addon, "Yes"))
                return false;

            new AddonMaster.SelectYesno(addon).Yes();
            return true;
        }, 3000, "Confirm repairs.");
        TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.Occupied39], 5000, "Wait for repairs.");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() =>
        {
            var addon = RepairAddon;
            if (addon == null)
                return true; // 窗已經不在了:「關掉修理視窗」這件事已經達成,不要拖到逾時(這顆的 abortOnTimeout 是 true,佇列會被清掉)。

            if (!AddonPressGuard.TryBeginPress("Repair", &addon->AtkUnitBase, AddonPressGuard.ClosePressKey))
                return false;

            Callback.Fire(&addon->AtkUnitBase, true, -1);
            return true;
        }, 3000, true, "Close repair menu.");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(YesAlready.Unlock);

        return true;
    }
}
