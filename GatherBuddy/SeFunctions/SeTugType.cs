using System;
using Dalamud.Game;
using GatherBuddy.Enums;

namespace GatherBuddy.SeFunctions;

public sealed class SeTugType : SeAddressBase
{
    public SeTugType(ISigScanner sigScanner)
        : base(sigScanner,
            "48 8D 35 ?? ?? ?? ?? 4C 8B CE",
            missingEffect: "讀不到咬鉤力道：釣魚紀錄的力道記為未知，自動採集的自動釣魚停用；其餘功能照常。")
    { }

    public unsafe BiteType Bite
        => Address != IntPtr.Zero ? *(BiteType*)Address : BiteType.Unknown;
}
