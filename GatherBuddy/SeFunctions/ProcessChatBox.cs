using System;
using Dalamud.Game;

namespace GatherBuddy.SeFunctions;

public delegate IntPtr ProcessChatBoxDelegate(IntPtr uiModule, IntPtr message, IntPtr unk1, byte unk2);

public sealed class ProcessChatBox : SeFunctionBase<ProcessChatBoxDelegate>
{
    public ProcessChatBox(ISigScanner sigScanner)
        : base(sigScanner, "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 48 8B F2 48 8B F9 45 84 C9",
            missingEffect: "送不出遊戲內建的文字指令（例如採集前切換套裝的 /gearset）；插件自己的指令與其餘功能照常。")
    { }
}
