using System;
using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.UIHelpers;
using GatherBuddy.Classes;

namespace GatherBuddy.AutoGather.AtkReaders;

public class ItemSlotReader(IntPtr addon, int beginOffset = 0) : AtkReader(addon, beginOffset)
{
    // TC note: this field is a native Bool on the global client but a UInt (0/1) on TC's
    // older client build, for at least the first slot (absolute index 6). ReadBool()
    // throws InvalidCastException on the type mismatch; fall back to reading it as a
    // UInt (nonzero = enabled) instead of crashing the whole auto-gather tick.
    public  bool        Enabled
    {
        get
        {
            try
            {
                return ReadBool(0).GetValueOrDefault();
            }
            catch (InvalidCastException)
            {
                return ReadUInt(0).GetValueOrDefault() != 0;
            }
        }
    }
    public  uint        ItemId                 => ReadUInt(1).GetValueOrDefault();
    // TC note: GameData.Gatherables is keyed off item IDs from the current global
    // patch's data sheets. TC runs an older patch, so an item ID read live from the
    // addon (e.g. a newer-patch gatherable) can be absent from that dictionary. The
    // indexer throws KeyNotFoundException in that case, which - like the ItemSlotFlags
    // issue above - aborts the whole auto-gather tick before anything gets selected.
    public  Gatherable? Item                   => ItemId > 0 ? GatherBuddy.GameData.Gatherables.GetValueOrDefault(ItemId) : null;
    private uint        FlagsRaw               => ReadUInt(5).GetValueOrDefault();
    public  bool        HasBonus               => (FlagsRaw & 4) != 0;
    public  bool        RequiresPerception     => (FlagsRaw & 1) != 0;
    private SeString    RequiresPerceptionText => ReadSeString(6);
    private uint        BuffsValuesRaw         => ReadUInt(7).GetValueOrDefault();

    public sbyte Yield
        => (sbyte)(BuffsValuesRaw & 0xff);

    public sbyte BoonChance => (sbyte)((BuffsValuesRaw >> 8) & 0xff);

    // Same TC Bool-vs-UInt mismatch as Enabled above - defend the same way.
    public bool HasGivingLandBuff
    {
        get
        {
            try
            {
                return ReadBool(9).GetValueOrDefault();
            }
            catch (InvalidCastException)
            {
                return ReadUInt(9).GetValueOrDefault() != 0;
            }
        }
    }

    private uint CollectableRaw
        => ReadUInt(10).GetValueOrDefault();

    public bool IsCollectable => CollectableRaw == 2;
}
