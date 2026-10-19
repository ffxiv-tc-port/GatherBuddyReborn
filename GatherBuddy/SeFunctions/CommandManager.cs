using System;
using System.Text;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace GatherBuddy.SeFunctions;

public unsafe class CommandManager
{
    private readonly ProcessChatBox _processChatBox;
    private readonly IGameGui       _gameGui;

    // 🔴 存 Dalamud 服務、每次要用的時候重新查,不在建構時把 UIModule 指標凍結成欄位(絕不跨幀保存原生指標):
    //    `IGameGui.GetUIModule()` 逐字是 `(nint)UIModule.Instance()`，而 `UIModule.Instance()` 是
    //    `Framework.Instance() == null ? null : framework->GetUIModule()` —— **合法回 0**。
    //    CommandManager 由 Executor 的欄位初始式建立（外掛載入那一刻），那時凍結到 0 的話
    //    這個外掛**整個 session** 都送不出遊戲指令，而且只會安靜地記一行錯誤。
    public CommandManager(IGameGui gameGui, ProcessChatBox processChatBox)
    {
        _processChatBox = processChatBox;
        _gameGui        = gameGui;
    }

    public CommandManager(IGameGui gameGui, ISigScanner sigScanner)
        : this(gameGui, new ProcessChatBox(sigScanner))
    { }

    public bool Execute(string message)
    {
        // First try to process the command through Dalamud.
        if (Dalamud.Commands.ProcessCommand(message))
        {
            GatherBuddy.Log.Verbose($"Executed Dalamud command \"{ message}\".");
            return true;
        }

        // 每次重新查（理由見建構式上的說明）。
        var uiModulePtr = _gameGui.GetUIModule().Address;
        if (uiModulePtr == IntPtr.Zero)
        {
            // 動作型路徑（使用者/自動採集明確要送一條指令）：取不到就記一行並不做，
            // 安靜失敗會讓人以為指令已經送出去了。
            GatherBuddy.Log.Error($"Can not execute \"{message}\" because no uiModulePtr is available.");
            return false;
        }

        // Then hand the game a fully initialised Utf8String (0x68); its content ends at the first NUL,
        // exactly as the game reads a C string.
        var text = PrepareString(message);
        try
        {
            _processChatBox.Invoke(uiModulePtr, (IntPtr)text, IntPtr.Zero, (byte)0);
        }
        finally
        {
            text->Dtor();
            IMemorySpace.Free(text);
        }

        return false;
    }

    private static Utf8String* PrepareString(string message)
    {
        var bytes      = Encoding.UTF8.GetBytes(message);
        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);
        fixed (byte* ptr = terminated)
        {
            return Utf8String.FromSequence(ptr);
        }
    }
}
