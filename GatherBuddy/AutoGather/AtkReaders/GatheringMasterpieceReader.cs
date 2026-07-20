using System;
using System.Collections.Generic;
using ECommons.UIHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Classes;

namespace GatherBuddy.AutoGather.AtkReaders;

// NOTE: Rewritten to use AtkReader (value-based) so it remains valid when the window is recreated by Revisit.

public unsafe class GatheringMasterpieceReader(AtkUnitBase* addon) : AtkReader(addon)
{
    // TC note: TC's client runs an older patch than the AtkValue layouts these indices
    // were measured against, and several "Gathering"/"GatheringMasterpiece" fields come
    // back as a different AtkValue.Type on TC (UInt-vs-Bool, UInt-vs-String seen so far
    // elsewhere in AtkReaders/). ReadUInt()/ReadBool() throw InvalidCastException by
    // design on a type mismatch. Every raw read in this class is wrapped so a mismatch
    // on any one field degrades to 0/false instead of throwing and aborting whatever
    // auto-gather logic depends on it.
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

    private uint ItemId => SafeReadUInt(2);

    // Same "item ID unrecognized in TC's older data sheets" indexer-throw issue as
    // ItemSlotReader.Item - use GetValueOrDefault instead of the throwing indexer.
    public Gatherable? Item => ItemId > 0 ? GatherBuddy.GameData.Gatherables.GetValueOrDefault(ItemId) : null;

    public bool HighVisible
        => addon->GetNodeById(15)->IsVisible();
    public bool MidVisible => addon->GetNodeById(14)->IsVisible();
    public bool LowVisible => addon->GetNodeById(13)->IsVisible();
    public int  CollectabilityCurrent => (int)SafeReadUInt(13);
    public int  CollectabilityMax     => (int)SafeReadUInt(14);

    public int IntegrityCurrent => (int)SafeReadUInt(62);
    public int IntegrityMax     => (int)SafeReadUInt(63);

    public int ScourGain      => (int)SafeReadUInt(48);
    public int BrazenGainMin  => (int)SafeReadUInt(49);
    public int BrazenGainMax  => (int)SafeReadUInt(50);
    public int MeticulousGain => (int)SafeReadUInt(51);

    public int LowThreshold  => (int)SafeReadUInt(65);
    public int MidThreshold  => (int)SafeReadUInt(66);
    public int HighThreshold => (int)SafeReadUInt(67);

    public bool IsValid => !IsNull;
}
