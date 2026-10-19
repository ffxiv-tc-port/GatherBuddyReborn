using System;
using System.Runtime.InteropServices;

namespace GatherBuddy.AutoGather.Movement;

/// <summary>
/// 載入時驗證飛行輸入函式仍把三個移動軸以 movss 寫到 result+0／+4／+8，也就是
/// <see cref="PlayerMoveControllerFlyInput"/> 的 Forward／Left／Up；CS 沒有這個結構，只能看遊戲碼。
/// 驗不過＝不掛飛行覆寫，不帶著未驗證的版面寫入。
/// </summary>
internal static unsafe class FlyInputLayoutCheck
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RuntimeFunction
    {
        public uint BeginAddress;
        public uint EndAddress;
        public uint UnwindData;
    }

    [DllImport("kernel32.dll")]
    private static extern RuntimeFunction* RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, nint historyTable);

    private const byte UnwFlagChainInfo = 0x4;

    public static bool Verify(nint entry, out string reason)
    {
        var length = FunctionLength(entry);
        if (length <= 0)
        {
            reason = "查不到函式範圍（.pdata）";
            return false;
        }

        if (!WritesAxes(new ReadOnlySpan<byte>((void*)entry, length), out reason))
            return false;

        reason = $"{reason}，函式長度 0x{length:X}";
        return true;
    }

    /// <summary>從進入點沿 .pdata 走過緊接著的延續區塊（UNW_FLAG_CHAININFO），回傳本體長度；查不到回 0。</summary>
    public static int FunctionLength(nint entry)
    {
        var start = (ulong)entry;
        var pc    = start;
        while (true)
        {
            var rf = RtlLookupFunctionEntry(pc, out var imageBase, 0);
            if (rf == null || imageBase + rf->BeginAddress != pc)
                break;

            if (pc != start && (rf->UnwindData & 1) == 0 && ((*(byte*)(imageBase + rf->UnwindData) >> 3) & UnwFlagChainInfo) == 0)
                break;

            pc = imageBase + rf->EndAddress;
        }

        return (int)Math.Min(pc - start, 0x10000);
    }

    /// <summary>只看傳進來的位元組；離線驗證也呼叫這支。</summary>
    public static bool WritesAxes(ReadOnlySpan<byte> code, out string reason)
    {
        // result 在 rdx；序言通常先搬到別的暫存器（REX.W 8B /r 或 89 /r，mod=11），找不到就當它一直留在 rdx。
        var baseReg  = 2;
        var prologue = Math.Min(code.Length, 0x40);
        for (var i = 0; i + 3 <= prologue; ++i)
        {
            int rex = code[i], op = code[i + 1], modrm = code[i + 2];
            if ((rex & 0xF8) != 0x48 || modrm >> 6 != 3)
                continue;

            var reg = ((modrm >> 3) & 7) | ((rex & 4) << 1);
            var rm  = (modrm & 7) | ((rex & 1) << 3);
            if (op == 0x8B && rm == 2)
            {
                baseReg = reg;
                break;
            }

            if (op == 0x89 && reg == 2)
            {
                baseReg = rm;
                break;
            }
        }

        // rsp／r12 要 SIB、rbp／r13 的 mod=00 是 rip 相對：不認這兩種形狀，當成驗不過。
        if ((baseReg & 7) is 4 or 5)
        {
            reason = $"result 搬到不支援的暫存器 r{baseReg}";
            return false;
        }

        var seen = 0;
        for (var i = 0; i + 4 <= code.Length; ++i)
        {
            if (code[i] != 0xF3)
                continue;

            var j   = i + 1;
            var rex = 0;
            if ((code[j] & 0xF0) == 0x40)
                rex = code[j++];
            if (j + 3 > code.Length || code[j] != 0x0F || code[j + 1] != 0x11)
                continue;

            var modrm = code[j + 2];
            if (((modrm & 7) | ((rex & 1) << 3)) != baseReg)
                continue;

            var disp = (modrm >> 6) switch
            {
                0                           => 0,
                1 when j + 4 <= code.Length => (sbyte)code[j + 3],
                _                           => -1,
            };
            if (disp is 0 or 4 or 8)
                seen |= 1 << (disp >> 2);
        }

        reason = $"result 暫存器 r{baseReg}，+0/+4/+8 的 movss 寫入遮罩 0b{Convert.ToString(seen, 2).PadLeft(3, '0')}";
        return seen == 7;
    }
}
