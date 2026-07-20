using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using ECommons.Automation;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Enums;

namespace GatherBuddy.AutoGather.AtkReaders;

public unsafe class GatheringReader(AtkUnitBase* addon) : AtkReader(addon)
{
    private uint GatherChancesRaw1
        => ReadUInt(1).GetValueOrDefault();

    private uint GatherChancesRaw2
        => ReadUInt(2).GetValueOrDefault();

    private uint GatherChances
        => (GatherChancesRaw1 != 0 && GatherChancesRaw1 != 0xFFFFFFFF ? BinaryPrimitives.ReverseEndianness(GatherChancesRaw1) : BinaryPrimitives.ReverseEndianness(GatherChancesRaw2));

    private uint ItemLevelRaw1
        => ReadUInt(3).GetValueOrDefault();

    private uint ItemLevelRaw2
        => ReadUInt(4).GetValueOrDefault();

    private uint ItemLevel
        => (ItemLevelRaw1 != 0 && ItemLevelRaw1 != 0xFFFFFFFF ? BinaryPrimitives.ReverseEndianness(ItemLevelRaw1) : BinaryPrimitives.ReverseEndianness(ItemLevelRaw2));

    private List<ItemSlotReader> ItemSlotReaders
        => Loop<ItemSlotReader>(6, 11, 8);

    public List<ItemSlot> ItemSlots
    {
        get
        {
            var result = new List<ItemSlot>();
            for (var i = 0; i < 8; ++i)
            {
                var slot = ItemSlotReaders[i];
                result.Add(new ItemSlot(i, slot, ItemSlotFlags, GatherChances, ItemLevel));
            }

            return result;
        }
    }

    // TC note: index 99 in the "Gathering" addon's AtkValues array is a UInt
    // (rare/hidden flags) on the global client, but on TC's older client build
    // this slot holds a String value instead - the addon's field layout has
    // shifted. ReadUInt() throws InvalidCastException on a type mismatch by
    // design, and this getter is read every auto-gather tick (via
    // HiddenRevealed -> ItemSlots), so the exception was aborting the entire
    // auto-gather action loop before any item selection happened. Fall back to
    // "no flags" instead of crashing when the type doesn't match.
    private uint ItemSlotFlags
    {
        get
        {
            try
            {
                return ReadUInt(99).GetValueOrDefault();
            }
            catch (InvalidCastException)
            {
                return 0u;
            }
        }
    }

    // Same TC Bool-vs-UInt AtkValue mismatch as ItemSlotFlags/ItemSlotReader.Enabled -
    // guard every ReadBool() here the same way rather than waiting for another crash report.
    private static bool SafeReadBool(Func<bool?> readBool, Func<uint?> readUInt)
    {
        try
        {
            return readBool().GetValueOrDefault();
        }
        catch (InvalidCastException)
        {
            return readUInt().GetValueOrDefault() != 0;
        }
    }

    public bool QuickGatheringAllowed
        => SafeReadBool(() => ReadBool(106), () => ReadUInt(106));

    public bool QuickGatheringEnabled
        => SafeReadBool(() => ReadBool(107), () => ReadUInt(107));

    public bool QuickGatheringInProgress
        => SafeReadBool(() => ReadBool(108), () => ReadUInt(108));

    private uint LastSelectedSlot
        => ReadUInt(109).GetValueOrDefault();

    public int IntegrityRemaining
        => (int)ReadUInt(110).GetValueOrDefault();

    public int IntegrityMax
        => (int)ReadUInt(111).GetValueOrDefault();

    public bool Touched
        => IntegrityRemaining != IntegrityMax;

    public bool HiddenRevealed
        => ItemSlots.Any(i => i.IsHidden);
}
