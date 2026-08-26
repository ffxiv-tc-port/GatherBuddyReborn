using ECommons;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Classes;

namespace GatherBuddy.AutoGather.AtkReaders;

public class ItemSlot(int index, ItemSlotReader reader, uint itemSlotFlags, uint gatherChances, uint itemLevels)
{
    public unsafe void Gather()
    {
        if (GenericHelpers.TryGetAddonByName("Gathering", out AtkUnitBase* addon))
        {
            Callback.Fire(addon, true, index, 0);
        }
    }
    public bool Enabled => reader.Enabled;
    public bool HasBonus => reader.HasBonus;
    public bool RequiresPerception => reader.RequiresPerception;
    public bool HasGivingLandBuff => reader.HasGivingLandBuff;
    public bool IsCollectable => reader.IsCollectable;
    public sbyte Yield => reader.Yield;
    public sbyte BoonChance => reader.BoonChance;
    // TC note: a slot can hold an item whose ID isn't in GameData.Gatherables (see
    // ItemSlotReader.Item) because TC runs an older patch than the data sheets that
    // dictionary is built from. That item is real and occupies the slot - IsEmpty must
    // check the raw slot ID, not whether we could resolve it to a Gatherable, otherwise
    // nodes containing an unrecognized item look entirely empty and auto-gather closes
    // the window without gathering anything from them.
    public bool        IsEmpty => reader.ItemId == 0;
    public Gatherable? Item    => reader.Item;

    public int GatherChance
        => (sbyte)((gatherChances >> (index * 8)) & 0xFF);

    public int ItemLevel
        => (sbyte)((itemLevels >> (index * 8)) & 0xFF);

    private const uint RareFlagMask = 1u << 16;

    public bool IsRare
    {
        get
        {
            uint mask = RareFlagMask << index;
            return (itemSlotFlags & mask) != 0;
        }
    }

    public bool IsHidden => (itemSlotFlags & (1u << (index))) != 0;
}
