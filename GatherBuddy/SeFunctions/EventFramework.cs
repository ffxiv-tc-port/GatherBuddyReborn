using CsEventFramework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework;
using CsFishingEventHandler = FFXIVClientStructs.FFXIV.Client.Game.Event.FishingEventHandler;

namespace GatherBuddy.SeFunctions;

public enum FishingState : byte
{
    None       = 0,
    PoleOut    = 1,
    PullPoleIn = 2,
    Quit       = 3,
    PoleReady  = 4,
    Bite       = 5,
    Reeling    = 6,
    Waiting    = 8,
    Waiting2   = 9,
    Waiting3   = 12,
}

public sealed unsafe class EventFramework
{
    public nint Address
        => (nint)CsEventFramework.Instance();

    // 版面跟著 CS 的 FishingEventHandler 走，不手抄偏移：遊戲改版時隨 CS 一起換。
    internal CsFishingEventHandler* FishingManager
    {
        get
        {
            var eventFramework = CsEventFramework.Instance();
            if (eventFramework == null)
                return null;

            return (CsFishingEventHandler*)eventFramework->EventHandlerModule.FishingEventHandler;
        }
    }

    public uint? CurrentSwimBait
    {
        get
        {
            var ptr = FishingManager;
            if (ptr == null)
                return null;

            return ptr->CurrentSelectedSwimBait switch
            {
                0x00 when ptr->SwimBaitId1 != 0 => ptr->SwimBaitId1,
                0x01 when ptr->SwimBaitId2 != 0 => ptr->SwimBaitId2,
                0x02 when ptr->SwimBaitId3 != 0 => ptr->SwimBaitId3,
                _                               => null,
            };
        }
    }

    public uint? SwimBait(int idx)
    {
        var ptr = FishingManager;
        if (ptr == null)
            return null;

        return idx switch
        {
            0x00 when ptr->SwimBaitId1 != 0 => ptr->SwimBaitId1,
            0x01 when ptr->SwimBaitId2 != 0 => ptr->SwimBaitId2,
            0x02 when ptr->SwimBaitId3 != 0 => ptr->SwimBaitId3,
            _                               => null,
        };
    }


    public int NumSwimBait
    {
        get
        {
            var ptr = FishingManager;
            if (ptr == null)
                return 0;

            return (ptr->SwimBaitId1 != 0 ? 1 : 0)
              + (ptr->SwimBaitId2 != 0 ? 1 : 0)
              + (ptr->SwimBaitId3 != 0 ? 1 : 0);
        }
    }

    public FishingState FishingState
    {
        get
        {
            // CS 的 State 是 4 位元組列舉；刻意只讀最低位元組，與原本的單位元組讀法相同。
            var ptr = FishingManager;
            return ptr != null ? (FishingState)(*(byte*)&ptr->State) : FishingState.None;
        }
    }
}
