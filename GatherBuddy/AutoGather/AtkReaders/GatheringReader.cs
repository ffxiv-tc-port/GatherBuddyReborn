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
    // TC/CN note: the "Gathering" addon's AtkValues array has 113 entries on
    // TC/CN-style clients versus 114 on global - one field fewer, starting right
    // after the per-item-slot block begins. Every index from the per-slot base
    // onward is shifted down by exactly 1 as a result (confirmed against
    // aliceric27/GatherBuddyReborn@dev-tc's "Fix AtkValue Offset" commit, which
    // hit the identical 113-vs-114 count on the CN client). This is a real field
    // offset difference, not just a type mismatch - reading the global-client
    // indices doesn't throw (SafeReadUInt/SafeReadBool below still guard against
    // that separately), it just silently reads the *wrong* neighboring field.
    private int Shift => addon->AtkValuesCount == 113 ? -1 : 0;

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
        => Loop<ItemSlotReader>(6 + Shift, 11, 8);

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

    // global index 99 / TC-CN index 98: rare/hidden flags array.
    private uint ItemSlotFlags
        => SafeReadUInt(99 + Shift);

    public bool QuickGatheringAllowed
        => SafeReadBool(106 + Shift);

    public bool QuickGatheringEnabled
        => SafeReadBool(107 + Shift);

    public bool QuickGatheringInProgress
        => SafeReadBool(108 + Shift);

    private uint LastSelectedSlot
        => SafeReadUInt(109 + Shift);

    public int IntegrityRemaining
        => (int)SafeReadUInt(110 + Shift);

    public int IntegrityMax
        => (int)SafeReadUInt(111 + Shift);

    public bool Touched
        => IntegrityRemaining != IntegrityMax;

    public bool HiddenRevealed
        => ItemSlots.Any(i => i.IsHidden);
}
