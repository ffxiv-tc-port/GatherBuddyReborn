using System;
using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.UIHelpers;
using GatherBuddy.Classes;

namespace GatherBuddy.AutoGather.AtkReaders;

public class ItemSlotReader(IntPtr addon, int beginOffset = 0) : AtkReader(addon, beginOffset)
{
    // TC note: several fields here come back as a different AtkValue.Type on TC than expected (Bool-vs-UInt
    // confirmed for Enabled/HasGivingLandBuff). ReadUInt()/ReadBool() throw InvalidCastException by design
    // on any such mismatch, which would abort the whole auto-gather tick. Every raw read here is wrapped so
    // one mismatched field degrades to 0/false instead of taking down the whole loop.
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

    public  bool        Enabled                => SafeReadBool(0);
    public  uint        ItemId                 => SafeReadUInt(1);

    // TC note: GameData.Gatherables is keyed off item IDs from the data sheets it was built from, so an item
    // ID read live from the addon can be absent from that dictionary. The indexer throws
    // KeyNotFoundException in that case, which - like the type mismatches above - aborts the whole
    // auto-gather tick before anything gets selected. Use GetValueOrDefault instead of the throwing indexer.
    public  Gatherable? Item                   => ItemId > 0 ? GatherBuddy.GameData.Gatherables.GetValueOrDefault(ItemId) : null;
    private uint        FlagsRaw               => SafeReadUInt(5);
    public  bool        HasBonus               => (FlagsRaw & 4) != 0;
    public  bool        RequiresPerception     => (FlagsRaw & 1) != 0;
    private SeString    RequiresPerceptionText => ReadSeString(6);
    private uint        BuffsValuesRaw         => SafeReadUInt(7);

    public sbyte Yield
        => (sbyte)(BuffsValuesRaw & 0xff);

    public sbyte BoonChance => (sbyte)((BuffsValuesRaw >> 8) & 0xff);

    public bool HasGivingLandBuff => SafeReadBool(9);

    private uint CollectableRaw
        => SafeReadUInt(10);

    public bool IsCollectable => CollectableRaw == 2;
}
