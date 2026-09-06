using ECommons.EzIpcManager;
using ECommons.Reflection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ECommons.DalamudServices;
using ECommons.EzSharedDataManager;

#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
namespace GatherBuddy.Plugin
{
    internal static class IPCSubscriber
    {
        /// <summary>
        /// 某個外掛在不在(有沒有裝、載入完了沒有)。<b>每一次呼叫都把 Dalamud 的
        /// <c>InstalledPlugins</c> 整表反射掃一遍</b>(逐個外掛 <c>GetProperty("InternalName").GetValue</c>)。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>每幀路徑不要用這一支,用 <see cref="IsReadyThrottled"/>。</b><br/>
        /// ⚠️ <b>為什麼不乾脆改成 <c>ignoreCache: false</c></b>:ECommons 的那個快取
        /// <b>只存得下正面結果</b> —— <c>DalamudReflector.TryGetDalamudPlugin</c> 只在
        /// 「找到了」那條路徑寫 <c>pluginCache[internalName]</c>,「沒裝」從來不進快取,
        /// 於是對缺席的外掛<b>一點都沒省到</b>(照樣每次全表掃描)。
        /// 而且開快取會順便把 <c>MonitorPlugins</c> 掛上 <c>Framework.Update</c>,
        /// 變成<b>不管自動採集開沒開</b>都每幀走訪一次 <c>InstalledPlugins</c> 比對快照。
        /// ⇒ 換快取治不了這個問題,節流才治得了。
        /// </remarks>
        public static bool IsReady(string pluginName)
            => DalamudReflector.TryGetDalamudPlugin(pluginName, out _, false, true);

        /// <summary>同一個外掛名在這段時間內沿用上一次的答案。</summary>
        private const long PresenceProbeIntervalMs = 5_000;

        /// <summary>保護 <see cref="PresenceResults"/>。<b>鎖內不做反射、不打 IPC、不寫 log。</b></summary>
        private static readonly object PresenceGate = new();

        /// <summary>外掛名 → (這個答案到期的時刻, 上一次的答案)。</summary>
        private static readonly Dictionary<string, (long ExpiresAt, bool Present)> PresenceResults
            = new(StringComparer.Ordinal);

        /// <summary>
        /// <see cref="IsReady"/> 的節流版:每個外掛名最多每 <see cref="PresenceProbeIntervalMs"/>
        /// 毫秒真的去掃一次,其餘時間沿用上一次的答案。<b>每幀路徑一律用這一支。</b>
        /// </summary>
        /// <remarks>
        /// 🔴 <b>絕不用 <c>ECommons.Throttlers.EzThrottler</c></b>:那是整個外掛共用的靜態
        /// <c>Dictionary</c> 而且零同步,從繪製執行緒與 framework 執行緒同時進來會把字典本身弄壞,
        /// 連帶弄壞這個外掛裡所有模組的節流。這裡自帶一個字典＋自己的鎖。<br/>
        /// 🔴 <b>延遲有上限</b>:每一筆都會到期並重新查 ⇒「對端載入之後永遠沒發現」不可能發生,
        /// 最多晚 <see cref="PresenceProbeIntervalMs"/> 毫秒。<br/>
        /// 🔑 真正的查詢<b>一定在鎖外</b>(它會做反射,而且可能寫 log)。代價是偶爾兩條執行緒同時
        /// 查同一個名字 —— 那是冪等的(答案一樣),而且比今天「每幀都查」少了好幾個數量級。
        /// </remarks>
        public static bool IsReadyThrottled(string pluginName)
        {
            var  now = Environment.TickCount64;
            bool hadPrevious;
            bool previous;
            lock (PresenceGate)
            {
                hadPrevious = PresenceResults.TryGetValue(pluginName, out var cached);
                previous    = hadPrevious && cached.Present;
                if (hadPrevious && now < cached.ExpiresAt)
                    return cached.Present;
            }

            var present = IsReady(pluginName);

            lock (PresenceGate)
                PresenceResults[pluginName] = (now + PresenceProbeIntervalMs, present);

            // 🔴 log 一定在鎖外。只在「答案跟上一次不一樣」時寫一行 —— 那只有外掛真的被載入或
            //    卸載時才會發生,不會洗版;而使用者回報「GatherBuddy 沒發現某某外掛」時,
            //    這一行是唯一的線索。一律 Information:使用者的記錄等級只濾掉 Verbose。
            if (hadPrevious && previous != present)
                GatherBuddy.Log.Information(present
                    ? $"[外掛偵測] 偵測到 {pluginName} 已載入,相關整合恢復。"
                    : $"[外掛偵測] {pluginName} 已經不在了(卸載或重載中),相關整合暫停。");

            return present;
        }
    }

    internal static class VNavmesh
    {
        internal static bool Enabled
            => IPCSubscriber.IsReadyThrottled("vnavmesh");

        internal static class Nav
        {
            static Nav()
            {
                EzIPC.Init(typeof(Nav), "vnavmesh");
                Debug.Assert(IsReady != null);
                Debug.Assert(BuildProgress != null);
                Debug.Assert(Reload != null);
                Debug.Assert(Rebuild != null);
                Debug.Assert(Pathfind != null);
                Debug.Assert(PathfindCancelable != null);
                Debug.Assert(PathfindCancelAll != null);
                Debug.Assert(PathfindInProgress != null);
                Debug.Assert(PathfindNumQueued != null);
                Debug.Assert(IsAutoLoad != null);
                Debug.Assert(SetAutoLoad != null);
            }

            [EzIPC("vnavmesh.Nav.IsReady", applyPrefix: false)]
            internal static readonly Func<bool> IsReady;

            [EzIPC("vnavmesh.Nav.BuildProgress", applyPrefix: false)]
            internal static readonly Func<float> BuildProgress;

            [EzIPC("vnavmesh.Nav.Reload", applyPrefix: false)]
            internal static readonly Func<bool> Reload;

            [EzIPC("vnavmesh.Nav.Rebuild", applyPrefix: false)]
            internal static readonly Func<bool> Rebuild;

            [EzIPC("vnavmesh.Nav.Pathfind", applyPrefix: false)]
            internal static readonly Func<Vector3, Vector3, bool, Task<List<Vector3>>> Pathfind;

            [EzIPC("vnavmesh.Nav.PathfindCancelable", applyPrefix: false)]
            internal static readonly Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> PathfindCancelable;

            [EzIPC("vnavmesh.Nav.PathfindCancelAll", applyPrefix: false)]
            internal static readonly Action PathfindCancelAll;

            [EzIPC("vnavmesh.Nav.PathfindInProgress", applyPrefix: false)]
            internal static readonly Func<bool> PathfindInProgress;

            [EzIPC("vnavmesh.Nav.PathfindNumQueued", applyPrefix: false)]
            internal static readonly Func<int> PathfindNumQueued;

            [EzIPC("vnavmesh.Nav.IsAutoLoad", applyPrefix: false)]
            internal static readonly Func<bool> IsAutoLoad;

            [EzIPC("vnavmesh.Nav.SetAutoLoad", applyPrefix: false)]
            internal static readonly Action<bool> SetAutoLoad;
        }

        internal static class Query
        {
            internal static class Mesh
            {
                static Mesh()
                {
                    EzIPC.Init(typeof(Mesh), "vnavmesh");
                    Debug.Assert(NearestPoint != null);
                    Debug.Assert(PointOnFloor != null);
                }

                // 🔴 回傳型別必須是 Vector3?:提供端是 navmeshManager.Query?.FindNearestPointOnMesh(...),
                //    網格沒載入或該點不在網格上時回 null。
                //    宣告成非可空的 Vector3 時,Dalamud 的 CallGateChannel 在「有值」那條路徑會靠 JSON
                //    來回轉換**靜默轉成功**,只有「回 null」那次會在 (TRet)result 那步擲
                //    NullReferenceException —— 失敗形式是罕見的例外,不是型別不合,所以一直沒被發現。
                [EzIPC("vnavmesh.Query.Mesh.NearestPoint", applyPrefix: false)]
                internal static readonly Func<Vector3, float, float, Vector3?> NearestPoint;

                // 🔴 同 NearestPoint:提供端是 navmeshManager.Query?.FindPointOnFloor(...) ⇒ Vector3?。
                [EzIPC("vnavmesh.Query.Mesh.PointOnFloor", applyPrefix: false)]
                internal static readonly Func<Vector3, bool, float, Vector3?> PointOnFloor;
            }
        }

        internal static class Path
        {
            static Path()
            {
                EzIPC.Init(typeof(Path), "vnavmesh");
                Debug.Assert(MoveTo != null);
                Debug.Assert(Stop != null);
                Debug.Assert(IsRunning != null);
                Debug.Assert(NumWaypoints != null);
                Debug.Assert(GetMovementAllowed != null);
                Debug.Assert(SetMovementAllowed != null);
                Debug.Assert(GetAlignCamera != null);
                Debug.Assert(SetAlignCamera != null);
                Debug.Assert(GetTolerance != null);
                Debug.Assert(SetTolerance != null);
            }

            [EzIPC("vnavmesh.Path.MoveTo", applyPrefix: false)]
            internal static readonly Action<List<Vector3>, bool> MoveTo;

            [EzIPC("vnavmesh.Path.Stop", applyPrefix: false)]
            internal static readonly Action Stop;

            [EzIPC("vnavmesh.Path.IsRunning", applyPrefix: false)]
            internal static readonly Func<bool> IsRunning;

            [EzIPC("vnavmesh.Path.NumWaypoints", applyPrefix: false)]
            internal static readonly Func<int> NumWaypoints;

            [EzIPC("vnavmesh.Path.GetMovementAllowed", applyPrefix: false)]
            internal static readonly Func<bool> GetMovementAllowed;

            [EzIPC("vnavmesh.Path.SetMovementAllowed", applyPrefix: false)]
            internal static readonly Action<bool> SetMovementAllowed;

            [EzIPC("vnavmesh.Path.GetAlignCamera", applyPrefix: false)]
            internal static readonly Func<bool> GetAlignCamera;

            [EzIPC("vnavmesh.Path.SetAlignCamera", applyPrefix: false)]
            internal static readonly Action<bool> SetAlignCamera;

            [EzIPC("vnavmesh.Path.GetTolerance", applyPrefix: false)]
            internal static readonly Func<float> GetTolerance;

            [EzIPC("vnavmesh.Path.SetTolerance", applyPrefix: false)]
            internal static readonly Action<float> SetTolerance;
        }

        internal static class SimpleMove
        {
            static SimpleMove()
            {
                EzIPC.Init(typeof(SimpleMove), "vnavmesh");
                Debug.Assert(PathfindAndMoveTo != null);
                Debug.Assert(PathfindInProgress != null);
            }

            [EzIPC("vnavmesh.SimpleMove.PathfindAndMoveTo", applyPrefix: false)]
            internal static readonly Func<Vector3, bool, bool> PathfindAndMoveTo;

            [EzIPC("vnavmesh.SimpleMove.PathfindInProgress", applyPrefix: false)]
            internal static readonly Func<bool> PathfindInProgress;
        }

        internal static class Window
        {
            static Window()
            {
                EzIPC.Init(typeof(Window), "vnavmesh");
                Debug.Assert(IsOpen != null);
                Debug.Assert(SetOpen != null);
            }

            [EzIPC("vnavmesh.Window.IsOpen", applyPrefix: false)]
            internal static readonly Func<bool> IsOpen;

            [EzIPC("vnavmesh.Window.SetOpen", applyPrefix: false)]
            internal static readonly Action<bool> SetOpen;
        }

        internal static class DTR
        {
            static DTR()
            {
                EzIPC.Init(typeof(DTR), "vnavmesh");
                Debug.Assert(IsShown != null);
                Debug.Assert(SetShown != null);
            }

            [EzIPC("vnavmesh.DTR.IsShown", applyPrefix: false)]
            internal static readonly Func<bool> IsShown;

            [EzIPC("vnavmesh.DTR.SetShown", applyPrefix: false)]
            internal static readonly Action<bool> SetShown;
        }
    }

    internal static class Lifestream
    {
        static Lifestream()
        {
            EzIPC.Init(typeof(Lifestream), "Lifestream");
            Debug.Assert(ExecuteCommand != null);
            Debug.Assert(IsBusy != null);
            Debug.Assert(Abort != null);
            Debug.Assert(AethernetTeleport != null);
        }

        internal static bool Enabled
            => IPCSubscriber.IsReadyThrottled("Lifestream");

        [EzIPC("Lifestream.ExecuteCommand", applyPrefix: false)]
        internal static readonly Action<string> ExecuteCommand;

        [EzIPC("Lifestream.IsBusy", applyPrefix: false)]
        internal static readonly Func<bool> IsBusy;

        [EzIPC("Lifestream.Abort", applyPrefix: false)]
        internal static readonly Action Abort;

        [EzIPC("Lifestream.AethernetTeleport", applyPrefix: false)]
        internal static readonly Func<string, bool> AethernetTeleport;
    }

    internal static class YesAlready
    {
        private static bool _locked = false;
        internal static void Lock()
        {
            if (!_locked && EzSharedData.TryGet<HashSet<string>>("YesAlready.StopRequests", out var stopRequests))
            {
                stopRequests.Add(Svc.PluginInterface.InternalName);
                _locked = true;
            }
        }
        internal static void Unlock()
        {
            if (_locked && EzSharedData.TryGet<HashSet<string>>("YesAlready.StopRequests", out var stopRequests))
            {
                stopRequests.Remove(Svc.PluginInterface.InternalName);
                _locked = false;
            }
        }
    }

    internal static class AllaganTools
    {
        static AllaganTools()
        {
            EzIPC.Init(typeof(AllaganTools), "AllaganTools");
        }

        internal static bool Enabled => IPCSubscriber.IsReadyThrottled("InventoryTools");

        [EzIPC("AllaganTools.ItemCountOwned", applyPrefix: false)]
        internal static readonly Func<uint, bool, uint[], uint> ItemCountOwned;
    }

    internal static class AutoHook
    {
        static AutoHook()
        {
            EzIPC.Init(typeof(AutoHook), "AutoHook");
        }

        internal static bool Enabled => IPCSubscriber.IsReady("AutoHook");

        // 🔑 GetPluginState 是 AutoHookSuppression 拍快照用的 —— 沒有 getter 就只能單向關掉、永不還原。
        //    提供端 AutoHook/IPC/AutoHookIPC.cs 的 GetPluginState() 與 SetPluginState() 讀寫同一個
        //    欄位(Configuration.PluginEnabled)。舊版 AutoHook 沒有這個端點,呼叫會擲 IpcNotReadyError,
        //    所以 AutoHookSuppression 那邊一定要 try/catch(這個類別沒有帶 SafeWrapper)。
        [EzIPC("AutoHook.GetPluginState", applyPrefix: false)]
        internal static readonly Func<bool> GetPluginState;

        [EzIPC("AutoHook.SetPluginState", applyPrefix: false)]
        internal static readonly Action<bool> SetPluginState;

        [EzIPC("AutoHook.CreateAndSelectAnonymousPreset", applyPrefix: false)]
        internal static readonly Action<string> CreateAndSelectAnonymousPreset;

        // ── 具名暫停租約(憑證形狀,與 AutoRetainer 那一組逐字相同的簽章)────────────
        // 🔴 舊的 AutoHook.SetPluginState 是對**使用者的** Configuration.PluginEnabled 單向寫入,
        //    三個借用端(Questionable／GBR／ICE)各自拍快照各自還原 ⇒ **執行期是最後寫入者獲勝**,
        //    而且沒有逾時:持有者當掉在 false 上時使用者看到的是「AutoHook 突然不會自動上鉤了」,
        //    log 一個字都沒有,唯一自癒是重載外掛。
        //    ⚠️ **磁碟那半邊已經不是問題了**:提供端的 IpcConfigOverrides 讓借來的值不進設定檔
        //    (2026-09-07 對使用者實際安裝的 7.20.0.38 驗過:IpcConfigOverrides／PluginEnabledKey 都在,
        //    而租約端點一個都沒有)。殘留只剩執行期那半邊 —— 但它照樣沒有主人、照樣不會自己還原。
        //    租約端點押的是 EffectivePluginEnabled(疊加值),使用者自己的欄位一個位元都不會被動到,
        //    而且有 5 分鐘硬性逾時 —— 沒人記得還也會自己還原。
        // 🔴🔴 AutoHook 的租約是 **refcount** 語意:**Acquire 本身就開始壓制**,不必再呼叫
        //    SetLeasedPluginState。這與 vnavmesh 的 MovementLeases(拿到租約是惰性的)刻意不同,
        //    照 vnavmesh 那套三步驟寫過來會多押一次、反過來則是完全靜默地什麼都沒發生。
        // 🔴 提供端簽章(AutoHook/IPC/AutoHookIPC.cs):
        //      Guid AcquireSuppressionFor(string owner, int milliseconds)
        //      bool RenewSuppression(Guid lease) / bool ReleaseSuppression(Guid lease)
        //    全部是**不可為 null 的值型別**,失敗回 Guid.Empty / false,永不回 null ——
        //    所以這裡宣告成非可空的 Guid/bool 是對的(宣告成 T 而提供端會回 null 時,
        //    CallGateChannel 會在 (TRet)result 那步擲一個看起來與 IPC 完全無關的 NullReferenceException)。
        // ⚠️ 這個 class **沒有帶 SafeWrapper**:舊版 AutoHook 沒有這幾個端點時呼叫會擲
        //    IpcNotReadyError 而**不會**被吞掉 —— AutoHookSuppression 就是靠這個例外分辨要不要
        //    退回舊的 SetPluginState 對稱借還。**不要**在這裡加 SafeWrapper,那會把例外吞成
        //    default(Guid)＝Guid.Empty,與「端點在、但提供端拒絕」變得分不出來。
        [EzIPC("AutoHook.AcquireSuppressionFor", applyPrefix: false)]
        internal static readonly Func<string, int, Guid> AcquireSuppressionFor;

        [EzIPC("AutoHook.RenewSuppression", applyPrefix: false)]
        internal static readonly Func<Guid, bool> RenewSuppression;

        [EzIPC("AutoHook.ReleaseSuppression", applyPrefix: false)]
        internal static readonly Func<Guid, bool> ReleaseSuppression;
    }

    internal static class AutoRetainer
    {
        private static EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(AutoRetainer), "AutoRetainer.PluginState", SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber.IsReadyThrottled("AutoRetainer");

        [EzIPC] internal static readonly Func<bool> IsBusy;
        [EzIPC] internal static readonly Func<Dictionary<ulong, HashSet<string>>> GetEnabledRetainers;
        [EzIPC] internal static readonly Func<bool> AreAnyRetainersAvailableForCurrentChara;
        [EzIPC] internal static readonly Action AbortAllTasks;
        [EzIPC] internal static readonly Action DisableAllFunctions;
        [EzIPC] internal static readonly Action EnableMultiMode;
        [EzIPC] internal static readonly Func<int> GetInventoryFreeSlotCount;
        // ⚠️ 簽名修正（2026-08-03）：這裡原本宣告成無參數的 Action，但提供端是
        //    AutoRetainer/Modules/EzIPCManagers/IPC_PluginState.cs 的
        //    `public void EnqueueHET(Action onFailure)` —— 一個參數。
        //    參數個數不符時 Dalamud 會丟例外，而這個 class 帶的是 SafeWrapper.IPCException，
        //    例外會被吞掉、變成完全靜默的空操作。目前全 repo 沒有呼叫點，所以還沒被踩到。
        [EzIPC] internal static readonly Action<Action> EnqueueHET;
        [EzIPC("AutoRetainer.GC.EnqueueInitiation", applyPrefix: false)] internal static readonly Action EnqueueGCInitiation;

        // ── 具名壓制租約（憑證形狀，與 YesAlready 的租約端點逐字相同的簽章）──────────
        // 🔴 舊的 AutoRetainer.SetSuppressed 是一個**無主的單一布林**:Artisan(僱員補貨時會壓制)
        //    與 ICE(宇宙任務)也在用同一個旗標,誰先結束誰就把別人的壓制一起解除。
        //    租約端點有憑證、可計數:全部租用者都還完,壓制才真的解除。
        // 🔑 形狀＝Guid 憑證(2026-09-03 起兩套統一)。改動前這裡宣告的是 Func<string, bool>
        //    (用租用者名字當鍵),與 YesAlready 那套的 Func<string, int, Guid> 形狀不一致 ——
        //    而 Dalamud 的 CallGate 在型別對不上時**不報錯**,會走 JSON 來回轉換
        //    (CallGateChannel.ConvertObject),Guid 轉 string 這個方向**轉得過去**,
        //    於是「歸還租約」會變成一個回傳 true 的空操作。統一形狀就是為了讓這種寫錯不可能發生。
        // 🔴 租約會逾時(提供端 5 分鐘),所以要拿著憑證週期性 RenewSuppression 續約,
        //    而且**要把續約的回傳值當真**:回 false 代表那把已經不在了,要重新取得 —— 見 AutoRetainerSuppression。
        // ⚠️ 這個 class 帶的是 SafeWrapper.IPCException:AutoRetainer 沒安裝、或舊版沒有這幾個端點時,
        //    呼叫會丟 IpcNotReadyError 被吞掉並回傳 default(Guid.Empty / false)——
        //    那正好就是我們要的 fail-safe 語意「沒拿到租約」。
        [EzIPC("AutoRetainer.AcquireSuppressionFor", applyPrefix: false)] internal static readonly Func<string, int, Guid> AcquireSuppressionFor;
        [EzIPC("AutoRetainer.RenewSuppression", applyPrefix: false)] internal static readonly Func<Guid, bool> RenewSuppression;
        [EzIPC("AutoRetainer.ReleaseSuppression", applyPrefix: false)] internal static readonly Func<Guid, bool> ReleaseSuppression;
    }
}
