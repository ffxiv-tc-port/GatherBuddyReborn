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
    // TC note: several "Gathering"/"GatheringMasterpiece" fields come back as a different AtkValue.Type on
    // TC (UInt-vs-Bool, UInt-vs-String seen so far elsewhere in AtkReaders/). ReadUInt()/ReadBool() throw
    // InvalidCastException by design on a type mismatch, so every raw read in this class is wrapped: a
    // mismatch on any one field degrades to 0/false instead of throwing and aborting whatever auto-gather
    // logic depends on it.
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

    // 🔴 `GetNodeById` 是 [MemberFunction] 原生呼叫，節點 id 不存在時**合法回 null**（視窗還沒跑完 setup、
    //    切頁的那一瞬間；13/14/15 又是寫死的節點 id，台服版面不保證一致）。把 null 當 this 交給 `IsVisible()`
    //    就是攔不到的 AccessViolationException ⇒ 只能事前判空。
    //    這三顆是自動採集收藏品**每一輪決策**都會讀的輪詢型存取子 ⇒ 取不到就安靜回 false，不寫 log；
    //    三顆都 false 時 GetCollectabilityScores 走它既有的「都看不到 ⇒ 用 LowThreshold」分支。
    private bool NodeVisible(uint nodeId)
    {
        if (addon == null)
            return false;

        var node = addon->GetNodeById(nodeId);
        return node != null && node->IsVisible();
    }

    public bool HighVisible => NodeVisible(15);
    public bool MidVisible  => NodeVisible(14);
    public bool LowVisible  => NodeVisible(13);
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
