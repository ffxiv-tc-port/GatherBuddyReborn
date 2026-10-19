using System;
using Dalamud.Game;

namespace GatherBuddy.SeFunctions;

public delegate void UpdateCatchDelegate(IntPtr module, uint fishId, bool large, ushort size, byte amount, byte level, byte unk7, byte unk8, byte unk9, byte unk10,
    byte unk11, byte unk12);

public sealed class UpdateFishCatch : SeFunctionBase<UpdateCatchDelegate>
{
    public UpdateFishCatch(ISigScanner sigScanner)
        // 上游 FFXIV-CombatReborn 的新簽名(舊簽名在台服已失效)。
        : base(sigScanner, "48 89 6C 24 ?? 56 41 56 41 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 01",
            missingEffect: "收不到釣獲通知：釣魚計時器與釣魚紀錄停用；其餘功能照常。")
    {}
}
