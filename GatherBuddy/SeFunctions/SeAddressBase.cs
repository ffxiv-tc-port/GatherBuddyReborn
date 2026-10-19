using System;
using Dalamud.Game;

namespace GatherBuddy.SeFunctions;

public class SeAddressBase
{
    public readonly IntPtr Address;

    public bool IsAvailable
        => Address != IntPtr.Zero;

    /// <param name="missingEffect">特徵碼對不上時少了什麼功能（寫進那一行 Warning）。</param>
    public SeAddressBase(ISigScanner sigScanner, string signature, int offset = 0, string missingEffect = "")
    {
        // 特徵碼對不上時 Address 留 0、不擲例外；衍生類別解參考前一律先判 IsAvailable。
        if (!sigScanner.TryGetStaticAddressFromSig(signature, out var address) || address == IntPtr.Zero)
        {
            GatherBuddy.Log.Warning($"遊戲改版後 {GetType().Name} 的特徵碼對不上（{signature}）。{missingEffect}");
            return;
        }

        Address = address + offset;
        var baseOffset = (ulong)Address.ToInt64() - (ulong)sigScanner.Module.BaseAddress.ToInt64();
        GatherBuddy.Log.Debug($"{GetType().Name} address 0x{Address.ToInt64():X16}, baseOffset 0x{baseOffset:X16}.");
    }
}
