using System;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Colors;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Plugin;
using Newtonsoft.Json;
using OtterGui;

namespace GatherBuddy.AutoGather;

// 本檔假設與契約：
// ①Tick／DrawBanner／連結回呼／Dispose 都在框架執行緒上（Dalamud 卸載時以 RunOnFrameworkThread 呼叫 Dispose）⇒ 不加鎖。
// ②記號只在 Dispose 寫一次、載入時讀完就刪；遊戲結束（IsFrameworkUnloading）不寫。
// ③同一角色、未滿 ReloadResumeRules.Window 才提示；點了才走既有的 Enabled = true，不另開路徑。
// ④任何失敗都只記錄並放棄提示，不擲出建構子、Update 或 Dispose。
internal sealed class ReloadResumeOffer
{
    private const string FileName = "auto_gather_resume.json";

    // 依外掛 InternalName 分開註冊；舊實例印出的連結在重載後也會派到這裡，所以回呼要自己判斷是否還有效。
    private const uint LinkCommandId = 0x47425201;

    private sealed class Marker
    {
        public DateTime StoppedAtUtc { get; set; }
        public ulong    ContentId    { get; set; }
        public string[] Lists        { get; set; } = [];
    }

    private enum State
    {
        None,
        WaitingForLogin,
        Shown,
    }

    private readonly AutoGather _owner;
    private readonly string     _path;

    private Marker?             _marker;
    private State               _state;
    private long                _deadlineTick;
    private ulong               _lastContentId;
    private DalamudLinkPayload? _link;
    private bool                _tickFailed;

    public ReloadResumeOffer(AutoGather owner, string configDirectory)
    {
        _owner = owner;
        _path  = Path.Combine(configDirectory, FileName);
        try
        {
            if (!File.Exists(_path))
                return;

            var text = File.ReadAllText(_path);
            TryDeleteFile();
            var marker = JsonConvert.DeserializeObject<Marker>(text);
            if (marker == null || marker.ContentId == 0)
                return;

            if (!ReloadResumeRules.TryGetRemaining(marker.StoppedAtUtc, DateTime.UtcNow, out var remaining))
            {
                GatherBuddy.Log.Information(
                    $"[自動採集] 上次卸載時自動採集在跑，但已超過 {ReloadResumeRules.Window.TotalSeconds:0} 秒（停止於 {marker.StoppedAtUtc.ToLocalTime():HH:mm:ss}），不提示繼續。");
                return;
            }

            _marker       = marker;
            _state        = State.WaitingForLogin;
            _deadlineTick = Environment.TickCount64 + (long)remaining.TotalMilliseconds;
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"[自動採集] 讀取重新載入記號失敗，這次不提示繼續：{e.GetType().Name}: {e.Message}");
            TryDeleteFile();
            _marker = null;
            _state  = State.None;
        }
    }

    public bool IsShown
        => _state == State.Shown;

    public void Tick()
    {
        if (_tickFailed)
            return;

        try
        {
            if (_owner.Enabled)
            {
                _lastContentId = Svc.PlayerState.ContentId;
                if (_state != State.None)
                    Clear("已經開始自動採集");
                return;
            }

            if (_state == State.None)
                return;

            if (Environment.TickCount64 > _deadlineTick)
            {
                Clear($"超過 {ReloadResumeRules.Window.TotalSeconds:0} 秒沒有繼續");
                return;
            }

            if (_state != State.WaitingForLogin)
                return;

            var contentId = Svc.PlayerState.ContentId;
            if (contentId == 0 || !Dalamud.ClientState.IsLoggedIn)
                return;

            if (contentId != _marker!.ContentId)
            {
                Clear("登入的是別的角色");
                return;
            }

            if (GatherBuddy.Config.AutoGatherConfig.AutoResumeAfterReload)
            {
                Clear(null);
                GatherBuddy.Log.Information($"[自動採集] 重新載入前在跑，已依設定自動繼續（清單：{ListNames()}）");
                _owner.Enabled = true;
                Communicator.Print("[GatherBuddy Reborn] " + "Auto-gather resumed automatically after the plugin was reloaded.".Loc());
                return;
            }

            PrintOffer();
            _state = State.Shown;
            GatherBuddy.Log.Information($"[自動採集] 重新載入前在跑，已提示繼續（清單：{ListNames()}）");
        }
        catch (Exception e)
        {
            _tickFailed = true;
            _state      = State.None;
            _marker     = null;
            GatherBuddy.Log.Warning($"[自動採集] 重新載入後的「繼續」提示出錯，這次不再提示：{e.GetType().Name}: {e.Message}");
        }
    }

    public void DrawBanner()
    {
        if (_state != State.Shown)
            return;

        ImGui.TextColored(ImGuiColors.DalamudYellow, "Auto-gather stopped because the plugin was reloaded.".Loc());
        ImGui.SameLine();
        if (ImGui.Button("Resume auto-gather".Loc() + "##reloadResume"))
            Accept("主視窗按鈕");
        ImGuiUtil.HoverTooltip("Starts auto-gather the same way as ticking \"Enabled\". This prompt disappears after 2 minutes or once auto-gather is on.".Loc());
        ImGui.SameLine();
        if (ImGui.Button("Don't resume".Loc() + "##reloadResumeDismiss"))
            Clear("使用者關掉提示");
    }

    public void Dispose(bool autoGatherRunning, AutoGatherListsManager listsManager)
    {
        try
        {
            if (_link != null)
                Dalamud.Chat.RemoveChatLinkHandler(_link.CommandId);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"[自動採集] 解除「繼續」連結失敗：{e.GetType().Name}: {e.Message}");
        }

        try
        {
            var offerAlive = _state != State.None && _marker != null && Environment.TickCount64 <= _deadlineTick;
            var decision = ReloadResumeRules.OnDispose(Dalamud.Framework.IsFrameworkUnloading, autoGatherRunning, _lastContentId,
                offerAlive);
            var marker = decision switch
            {
                ReloadResumeRules.WriteDecision.NewMarker => new Marker
                {
                    StoppedAtUtc = DateTime.UtcNow,
                    ContentId    = _lastContentId,
                    Lists        = listsManager.Lists.Where(l => l.Enabled).Select(l => l.Name).ToArray(),
                },
                ReloadResumeRules.WriteDecision.CarryOver => _marker,
                _                                         => null,
            };
            if (marker == null)
                return;

            ConfigFileSafety.EnqueueWrite(_path, JsonConvert.SerializeObject(marker), "[自動採集] 寫入重新載入記號失敗，下次載入不會提示繼續",
                s => GatherBuddy.Log.Warning(s));
            GatherBuddy.Log.Information($"[自動採集] 卸載時自動採集在跑，已留下記號（{decision}），重新載入後 2 分鐘內會提示繼續。");
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"[自動採集] 留下重新載入記號失敗，下次載入不會提示繼續：{e.GetType().Name}: {e.Message}");
        }
    }

    private void PrintOffer()
    {
        _link ??= Dalamud.Chat.AddChatLinkHandler(LinkCommandId, OnLinkClicked);
        var builder = new SeStringBuilder()
            .AddText("[GatherBuddy Reborn] ")
            .AddText("Auto-gather stopped because the plugin was reloaded.".Loc())
            .AddUiForeground(500)
            .AddText("  [ ")
            .Add(_link)
            .AddText("Click here to resume".Loc())
            .Add(RawPayload.LinkTerminator)
            .AddText(" ]")
            .AddUiForegroundOff();
        Communicator.Print(builder.BuiltString);
    }

    private void OnLinkClicked(uint commandId, SeString _)
    {
        try
        {
            Accept("聊天連結");
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"[自動採集] 處理「繼續」連結失敗：{e.GetType().Name}: {e.Message}");
        }
    }

    private void Accept(string source)
    {
        if (_state != State.Shown || _owner.Enabled)
        {
            if (!_owner.Enabled)
                Communicator.Print("[GatherBuddy Reborn] " + "This resume prompt has expired. Tick \"Enabled\" in the main window if you still want to gather.".Loc());
            return;
        }

        var lists = ListNames();
        Clear(null);
        GatherBuddy.Log.Information($"[自動採集] 從{source}繼續自動採集（清單：{lists}）");
        _owner.Enabled = true;
    }

    private void Clear(string? reason)
    {
        if (reason != null && _state != State.None)
            GatherBuddy.Log.Information($"[自動採集] 不再提示繼續：{reason}");
        _state  = State.None;
        _marker = null;
    }

    private string ListNames()
        => _marker == null || _marker.Lists.Length == 0 ? "?" : string.Join("、", _marker.Lists);

    private void TryDeleteFile()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"[自動採集] 刪除重新載入記號失敗（過期後自然失效）：{e.GetType().Name}: {e.Message}");
        }
    }
}
