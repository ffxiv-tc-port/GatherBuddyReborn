using System;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game;
using Dalamud.Plugin.Services;

namespace GatherBuddy.SeFunctions;

public class CommandManager
{
    private readonly ProcessChatBox _processChatBox;
    private readonly IGameGui       _gameGui;

    // 🔴 原本這裡是 `_uiModulePtr = gameGui.GetUIModule();` —— 在建構時把原生指標凍結成
    //    唯讀欄位，之後每次 Execute 都用那一份。兩個問題：
    //    ① 這正是「跨幀保存原生指標」：`IGameGui.GetUIModule()` 逐字是
    //       `(nint)UIModule.Instance()`，而 `UIModule.Instance()` 是
    //       `Framework.Instance() == null ? null : framework->GetUIModule()` —— **合法回 0**。
    //       CommandManager 由 Executor 的欄位初始式建立（外掛載入那一刻），此時取到 0 的話
    //       這個外掛**整個 session** 都送不出遊戲指令，而且只會安靜地記一行錯誤。
    //    ② 存下來的指標之後沒有任何一層會重新解析，遊戲那邊換掉單例就是拿舊位址去用。
    //    正解是 §61⑥ 的「刪快取換一行」：改存 Dalamud 服務，每次要用的時候重新查。
    //    UIModule 的取得器是 VirtualFunction 不掃特徵碼，所以每次重查的額外風險只有
    //    `Framework.GetUIModule` 一點，而且它本來就每次都會被走到。
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

        // 每次重新查，不要用建構時凍結的那一份（見建構式上的說明）。
        var uiModulePtr = _gameGui.GetUIModule().Address;
        if (uiModulePtr == IntPtr.Zero)
        {
            // 動作型路徑（使用者/自動採集明確要送一條指令）：取不到就記一行並不做，
            // 安靜失敗會讓人以為指令已經送出去了。原本的字串少了 $，實際印出來的是
            // 字面的 "{message}"，對使用者回報的 log 完全沒有診斷價值。
            GatherBuddy.Log.Error($"Can not execute \"{message}\" because no uiModulePtr is available.");
            return false;
        }

        // Then prepare a string to send to the game itself.
        var (text, length) = PrepareString(message);
        var payload = PrepareContainer(text, length);

        _processChatBox.Invoke(uiModulePtr, payload, IntPtr.Zero, (byte)0);

        Marshal.FreeHGlobal(payload);
        Marshal.FreeHGlobal(text);
        return false;
    }

    private static (IntPtr, long) PrepareString(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var mem   = Marshal.AllocHGlobal(bytes.Length + 30);
        Marshal.Copy(bytes, 0, mem, bytes.Length);
        Marshal.WriteByte(mem + bytes.Length, 0);
        return (mem, bytes.Length + 1);
    }

    private static IntPtr PrepareContainer(IntPtr message, long length)
    {
        var mem = Marshal.AllocHGlobal(400);
        Marshal.WriteInt64(mem,        message.ToInt64());
        Marshal.WriteInt64(mem + 0x8,  64);
        Marshal.WriteInt64(mem + 0x10, length);
        Marshal.WriteInt64(mem + 0x18, 0);
        return mem;
    }
}
