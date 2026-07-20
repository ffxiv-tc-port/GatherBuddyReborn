using System;
using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.UIHelpers;
using GatherBuddy.Classes;

namespace GatherBuddy.AutoGather.AtkReaders;

public class ItemSlotReader(IntPtr addon, int beginOffset = 0) : AtkReader(addon, beginOffset)
{
    public  bool        Enabled                => ReadBool(0).GetValueOrDefault();
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

    public bool HasGivingLandBuff => ReadBool(9).GetValueOrDefault();

    private uint CollectableRaw
        => ReadUInt(10).GetValueOrDefault();

    public bool IsCollectable => CollectableRaw == 2;
}
