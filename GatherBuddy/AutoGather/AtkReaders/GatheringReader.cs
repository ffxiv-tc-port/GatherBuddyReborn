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
    // TC note: TC runs an older client patch than the AtkValue layouts these indices
    // were measured against. Several fields in this addon come back as a different
    // AtkValue.Type on TC than expected (UInt-vs-String at index 99, UInt-vs-Bool
    // elsewhere) - ReadUInt()/ReadBool() throw InvalidCastException by design on any
    // such mismatch, which used to abort the whole auto-gather tick before any item
    // selection happened. Every raw read here is wrapped so one mismatched field
    // degrades to 0/false instead of taking down the whole loop.
    private uint SafeReadUInt(int n)
    {
        try
        {
            return ReadUInt(n).GetValueOrDefault();
        }
        catch (InvalidCastException)
        {
            return 0u;
        }
    }

    private bool SafeReadBool(int n)
    {
        try
        {
            return ReadBool(n).GetValueOrDefault();
        }
        catch (InvalidCastException)
        {
            return ReadUInt(n).GetValueOrDefault() != 0;
        }
    }

    private uint GatherChancesRaw1
        => SafeReadUInt(1);

    private uint GatherChancesRaw2
        => SafeReadUInt(2);

    private uint GatherChances
        => (GatherChancesRaw1 != 0 && GatherChancesRaw1 != 0xFFFFFFFF ? BinaryPrimitives.ReverseEndianness(GatherChancesRaw1) : BinaryPrimitives.ReverseEndianness(GatherChancesRaw2));

    private uint ItemLevelRaw1
        => SafeReadUInt(3);

    private uint ItemLevelRaw2
        => SafeReadUInt(4);

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

    // index 99: rare/hidden flags array - String on TC instead of UInt.
    private uint ItemSlotFlags
        => SafeReadUInt(99);

    public bool QuickGatheringAllowed
        => SafeReadBool(106);

    public bool QuickGatheringEnabled
        => SafeReadBool(107);

    public bool QuickGatheringInProgress
        => SafeReadBool(108);

    private uint LastSelectedSlot
        => SafeReadUInt(109);

    public int IntegrityRemaining
        => (int)SafeReadUInt(110);

    public int IntegrityMax
        => (int)SafeReadUInt(111);

    public bool Touched
        => IntegrityRemaining != IntegrityMax;

    public bool HiddenRevealed
        => ItemSlots.Any(i => i.IsHidden);
}
