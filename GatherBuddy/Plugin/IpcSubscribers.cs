using ECommons.EzIpcManager;
using ECommons.Reflection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Ipc.Exceptions;
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
        /// 🔴 <b>每幀路徑不要用這一支,用 <see cref="IsReadyThrottled"/>。</b>
        /// ⚠️ <b>為什麼不乾脆改成 <c>ignoreCache: false</c></b>:ECommons 的那個快取<b>只存得下正面結果</b>
        /// (<c>DalamudReflector.TryGetDalamudPlugin</c> 只在「找到了」那條路徑寫 <c>pluginCache</c>),
        /// 對缺席的外掛<b>一點都沒省到</b>;而且開快取會順便把 <c>MonitorPlugins</c> 掛上 <c>Framework.Update</c>,
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
        /// 🔑 真正的查詢<b>一定在鎖外</b>(它會做反射,而且可能寫 log)。代價是偶爾兩條執行緒同時查同一個名字 —— 那是冪等的(答案一樣)。
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
            //    這一行是唯一的線索(要使用者回報的診斷)。
            if (hadPrevious && previous != present)
                GatherBuddy.Log.Information(present
                    ? $"[插件偵測] 偵測到 {pluginName} 已載入,相關整合恢復。"
                    : $"[插件偵測] {pluginName} 已經不在了(卸載或重載中),相關整合暫停。");

            return present;
        }

        /// <summary>
        /// 作廢某個外掛的存在性快取,讓<b>下一次</b> <see cref="IsReadyThrottled"/> 立刻重查。
        /// </summary>
        /// <remarks>
        /// 🔴 <b>用途只有一個</b>:呼叫對方的 IPC 端點時吃到 <see cref="IpcNotReadyError"/>,
        /// 代表對方在節流窗(<see cref="PresenceProbeIntervalMs"/> 毫秒)內被卸載了,手上那筆「它在」的答案已經是錯的。
        /// 🔴 <b>只攔 <see cref="IpcNotReadyError"/>,不要攔 <c>Exception</c></b>:參數個數/型別寫錯
        /// 擲的是別的 <c>IpcError</c>,那些必須繼續往上冒,吞掉會讓寫錯的呼叫從此無聲無息。
        /// 🔑 這支從<b>繪製執行緒</b>與 <b>framework 執行緒</b>兩邊都可達,所以動字典一律在 <see cref="PresenceGate"/> 裡面;
        /// <b>log 一定在鎖外</b>,而且只在「本來真的快取著一筆」時才寫(對端根本沒安裝時不會洗版)。
        /// </remarks>
        public static void InvalidatePresence(string pluginName)
        {
            bool removed;
            lock (PresenceGate)
                removed = PresenceResults.Remove(pluginName);

            if (removed)
                GatherBuddy.Log.Information(
                    $"[插件偵測] 呼叫 {pluginName} 的 IPC 端點時對方已經不在了(卸載或重載中),存在性快取立刻作廢,下一次查詢會重新確認。");
        }
    }

    internal static class VNavmesh
    {
        /// <summary>這個外掛在 Dalamud 裡的內部名稱。存在性查詢與作廢<b>一定要用同一個常數</b>,
        /// 兩邊各自寫字面值的話,打錯字的失敗形式是「作廢永遠打不中」而且完全無聲。</summary>
        internal const string InternalName = "vnavmesh";

        internal static bool Enabled
            => IPCSubscriber.IsReadyThrottled(InternalName);

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

        /// <summary>網格載入好了沒。對端不在時回 <see langword="false"/>(＝還沒好)。</summary>
        /// <remarks>
        /// 🔴 <b>GBR 對 vnavmesh 的每一個呼叫都應該走安全版。</b>裸的 <c>[EzIPC]</c> 欄位在對端不在時
        /// 會擲 <see cref="IpcNotReadyError"/>,而呼叫點分佈在<b>每幀的 DoAutoGather</b>、
        /// <b>ImGui 按鈕回呼</b>(採集點清單的「導航」、偵錯分頁的 NavTo)與 <c>TaskManager</c> 的
        /// 延後步驟三種地方 —— 在繪製回呼上那會變成 Dalamud 的視窗錯誤面板。<br/>
        /// 🔑 兩層保護:①先看節流過的 <c>VNavmesh.Enabled</c>,沒裝的人連 IPC 都不打;
        /// ②真的擲了(卸載剛好落在 5 秒節流窗裡)就作廢存在性快取。只攔 <see cref="IpcNotReadyError"/>,其餘 <c>IpcError</c> 照樣往上冒。
        /// </remarks>
            internal static bool IsReadySafe()
            {
                if (!Enabled)
                    return false;
                try
                {
                    return IsReady();
                }
                catch (IpcNotReadyError)
                {
                    IPCSubscriber.InvalidatePresence(InternalName);
                    return false;
                }
            }

        /// <summary>vnavmesh 正在算路徑嗎。對端不在時回 <see langword="false"/>(＝沒在算)。</summary>
            internal static bool PathfindInProgressSafe()
            {
                if (!Enabled)
                    return false;
                try
                {
                    return PathfindInProgress();
                }
                catch (IpcNotReadyError)
                {
                    IPCSubscriber.InvalidatePresence(InternalName);
                    return false;
                }
            }

        /// <summary>取消全部路徑計算。對端不在＝本來就沒有東西要取消,什麼都不做。</summary>
            internal static void PathfindCancelAllSafe()
            {
                if (!Enabled)
                    return;
                try
                {
                    PathfindCancelAll();
                }
                catch (IpcNotReadyError)
                {
                    IPCSubscriber.InvalidatePresence(InternalName);
                }
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
                //    NullReferenceException —— 失敗形式是罕見的例外,不是型別不合。
                /// <summary>網格上離指定點最近的可行走點。對端不在時回 <see langword="null"/>,
                /// 與提供端「網格沒載入或該點不在網格上」的回答完全相同 ⇒ 呼叫端既有的
                /// <c>HasValue</c> 判斷原樣適用,不必改。</summary>
                internal static Vector3? NearestPointSafe(Vector3 point, float halfExtentXZ, float halfExtentY)
                {
                    if (!Enabled)
                        return null;
                    try
                    {
                        return NearestPoint(point, halfExtentXZ, halfExtentY);
                    }
                    catch (IpcNotReadyError)
                    {
                        IPCSubscriber.InvalidatePresence(InternalName);
                        return null;
                    }
                }

                /// <summary>指定點正下方的地板點。對端不在時回 <see langword="null"/>(與 <see cref="NearestPointSafe"/> 同一個語意)。</summary>
                internal static Vector3? PointOnFloorSafe(Vector3 point, bool allowUnlandable, float halfExtentXZ)
                {
                    if (!Enabled)
                        return null;
                    try
                    {
                        return PointOnFloor(point, allowUnlandable, halfExtentXZ);
                    }
                    catch (IpcNotReadyError)
                    {
                        IPCSubscriber.InvalidatePresence(InternalName);
                        return null;
                    }
                }

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

            /// <summary>正在沿路徑移動嗎。對端不在時回 <see langword="false"/>(＝沒在動)。</summary>
            internal static bool IsRunningSafe()
            {
                if (!Enabled)
                    return false;
                try
                {
                    return IsRunning();
                }
                catch (IpcNotReadyError)
                {
                    IPCSubscriber.InvalidatePresence(InternalName);
                    return false;
                }
            }

            /// <summary>停止移動。對端不在＝已經沒有路徑在跑,什麼都不做。</summary>
            internal static void StopSafe()
            {
                if (!Enabled)
                    return;
                try
                {
                    Stop();
                }
                catch (IpcNotReadyError)
                {
                    IPCSubscriber.InvalidatePresence(InternalName);
                }
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

            /// <summary>算路徑並開始移動。對端不在時回 <see langword="false"/>(＝這次導航失敗),
            /// 與 vnavmesh 自己「算不出路徑」的回答相同 ⇒ 呼叫端的 <c>LastNavigationResult</c>
            /// 與各處的失敗處理原樣適用。</summary>
            internal static bool PathfindAndMoveToSafe(Vector3 destination, bool fly)
            {
                if (!Enabled)
                    return false;
                try
                {
                    return PathfindAndMoveTo(destination, fly);
                }
                catch (IpcNotReadyError)
                {
                    IPCSubscriber.InvalidatePresence(InternalName);
                    return false;
                }
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

        /// <summary>這個外掛在 Dalamud 裡的內部名稱(存在性查詢與作廢共用)。</summary>
        internal const string InternalName = "Lifestream";

        internal static bool Enabled
            => IPCSubscriber.IsReadyThrottled(InternalName);

        /// <summary>
        /// <see cref="IsBusy"/> 的安全版:對端在節流窗內被卸載時,作廢存在性快取並回
        /// <see langword="false"/>(＝<b>沒在忙</b>),讓等它做完的工作視為完成而不是卡到逾時。
        /// </summary>
        /// <remarks>🔴 只攔 <see cref="IpcNotReadyError"/>;其餘 <c>IpcError</c> 照樣往上冒。</remarks>
        internal static bool IsBusySafe()
        {
            try
            {
                return IsBusy();
            }
            catch (IpcNotReadyError)
            {
                IPCSubscriber.InvalidatePresence(InternalName);
                return false;
            }
        }

        /// <summary>
        /// <see cref="IsBusy"/> 的可分辨版:回 <see langword="false"/> 代表<b>問不到</b>
        /// (對端已經不在),此時 <paramref name="busy"/> 沒有意義。呼叫端要對「沒在忙」與
        /// 「問不到」做出不同反應時用這一支。
        /// </summary>
        internal static bool TryIsBusy(out bool busy)
        {
            try
            {
                busy = IsBusy();
                return true;
            }
            catch (IpcNotReadyError)
            {
                IPCSubscriber.InvalidatePresence(InternalName);
                busy = false;
                return false;
            }
        }

        /// <summary><see cref="Abort"/> 的安全版:對端不在＝本來就沒有東西要中止,作廢快取後當作做完。</summary>
        internal static void AbortSafe()
        {
            try
            {
                Abort();
            }
            catch (IpcNotReadyError)
            {
                IPCSubscriber.InvalidatePresence(InternalName);
            }
        }

        /// <summary>
        /// <see cref="AethernetTeleport"/> 的安全版。<b>對端不在時回 <see langword="true"/></b>
        /// —— 它是丟進 <c>TaskManager</c> 的步驟,回 <see langword="true"/> 代表「這一步結束了」,
        /// 讓工作鏈往下走而不是原地重試到逾時(120 秒)。傳送本身沒發生,下一輪 DoAutoGather
        /// 會因為 Lifestream 已經不在而走別的分支。
        /// </summary>
        internal static bool AethernetTeleportSafe(string name)
        {
            try
            {
                return AethernetTeleport(name);
            }
            catch (IpcNotReadyError)
            {
                IPCSubscriber.InvalidatePresence(InternalName);
                return true;
            }
        }

        [EzIPC("Lifestream.ExecuteCommand", applyPrefix: false)]
        internal static readonly Action<string> ExecuteCommand;

        [EzIPC("Lifestream.IsBusy", applyPrefix: false)]
        internal static readonly Func<bool> IsBusy;

        [EzIPC("Lifestream.Abort", applyPrefix: false)]
        internal static readonly Action Abort;

        [EzIPC("Lifestream.AethernetTeleport", applyPrefix: false)]
        internal static readonly Func<string, bool> AethernetTeleport;
    }

    /// <summary>
    /// 把本插件放進別的插件的共享暫停名單（<c>*.StopRequests</c>），持有期間每幀補回。只在框架執行緒呼叫。
    /// </summary>
    /// <remarks>
    /// 補回的理由：TextAdvance 重新載入會清空名單、YesAlready 的看門狗逾時會移除持有者，兩邊都不會通知我們。
    /// 集合參考由 <see cref="EzSharedData"/> 快取（只登記一次）；本插件的登記撐著條目，對方重載拿到的仍是同一個集合。
    /// </remarks>
    internal sealed class SharedStopRequest(string dataName)
    {
        private bool     _locked;
        private bool     _subscribed;
        private bool     _reAddLogged;
        private DateTime _addedInFrame = DateTime.MaxValue;

        private static string Key => Svc.PluginInterface.InternalName;

        internal void Lock()
        {
            if (_locked || !EzSharedData.TryGet<HashSet<string>>(dataName, out _))
                return;

            _locked       = true;
            _reAddLogged  = false;
            _addedInFrame = Svc.Framework.LastUpdateUTC;
            if (!_subscribed)
            {
                Svc.Framework.Update += OnUpdate;
                _subscribed          =  true;
            }

            Reassert(initial: true);
        }

        internal void Unlock()
        {
            if (!_locked)
                return;

            _locked       = false;
            _addedInFrame = DateTime.MaxValue;
            if (_subscribed)
            {
                Svc.Framework.Update -= OnUpdate;
                _subscribed          =  false;
            }

            if (!EzSharedData.TryGet<HashSet<string>>(dataName, out var set))
                return;

            try
            {
                set.Remove(Key);
            }
            catch (InvalidOperationException ex)
            {
                GatherBuddy.Log.Warning($"[{dataName}] 從名單拿掉本插件失敗（{ex.Message}），對方會維持暫停到它重新載入。");
            }
        }

        /// <summary>沒持有＝true；持有中則要等名字在名單上滿一整幀（對方同一幀稍早可能已經按過）。</summary>
        internal bool MayClickTalk
            => !_locked || Svc.Framework.LastUpdateUTC > _addedInFrame;

        private void OnUpdate(global::Dalamud.Plugin.Services.IFramework _)
        {
            if (_locked)
                Reassert(initial: false);
        }

        private void Reassert(bool initial)
        {
            if (!EzSharedData.TryGet<HashSet<string>>(dataName, out var set))
                return;

            try
            {
                if (!set.Add(Key))
                    return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            _addedInFrame = Svc.Framework.LastUpdateUTC;
            if (initial || _reAddLogged)
                return;

            _reAddLogged = true;
            GatherBuddy.Log.Information($"[{dataName}] 名單裡沒有本插件了（對方重新載入或逾時移除），已補回。");
        }
    }

    internal static class YesAlready
    {
        private static readonly SharedStopRequest Request = new("YesAlready.StopRequests");
        internal static void Lock()   => Request.Lock();
        internal static void Unlock() => Request.Unlock();
    }

    /// <summary>TextAdvance 的暫停請求（與 AutoRetainer 同形）。TextAdvance 不檢查持有者還在不在 ⇒ 每條停止與卸載路徑都要 Unlock。</summary>
    internal static class TextAdvance
    {
        private static readonly SharedStopRequest Request = new("TextAdvance.StopRequests");
        internal static void Lock()       => Request.Lock();
        internal static void Unlock()     => Request.Unlock();
        internal static bool MayClickTalk => Request.MayClickTalk;
    }

    internal static class AllaganTools
    {
        static AllaganTools()
        {
            EzIPC.Init(typeof(AllaganTools), "AllaganTools");
        }

        /// <summary>
        /// 這個外掛在 Dalamud 裡的內部名稱。⚠️ <b>是 <c>InventoryTools</c>,不是 <c>AllaganTools</c></b>
        /// —— 類別名與 IPC 前綴都叫 AllaganTools,只有存在性查詢用的內部名不一樣。
        /// 作廢快取時寫成 AllaganTools 的話,失敗形式是<b>永遠打不中那個鍵</b>而且完全無聲。
        /// </summary>
        internal const string InternalName = "InventoryTools";

        internal static bool Enabled => IPCSubscriber.IsReadyThrottled(InternalName);

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
        // 🔴🔴 AutoHook 的租約是 **refcount** 語意:**Acquire 本身就開始壓制**,不必再呼叫 SetLeasedPluginState
        //    (與 vnavmesh 的 MovementLeases 刻意不同)。押的是 EffectivePluginEnabled(疊加值),有 5 分鐘硬性逾時。

        // 🔴 提供端簽章(AutoHook/IPC/AutoHookIPC.cs):
        //      Guid AcquireSuppressionFor(string owner, int milliseconds)
        //      bool RenewSuppression(Guid lease) / bool ReleaseSuppression(Guid lease)
        //    全部是**不可為 null 的值型別**,失敗回 Guid.Empty / false,永不回 null ⇒ 宣告成非可空的 Guid/bool 是對的。

        // ⚠️ 這個 class **沒有帶 SafeWrapper**,**不要**加:舊版 AutoHook 沒有這幾個端點時呼叫會擲 IpcNotReadyError,
        //    AutoHookSuppression 就是靠這個例外分辨要不要退回舊的 SetPluginState 對稱借還。
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

        /// <summary>
        /// ⚠️ <b>這一支掛不上 <see cref="IPCSubscriber.InvalidatePresence"/>,而且不需要。</b>
        /// 這個類別的 <c>EzIPC.Init</c> 帶了 <c>SafeWrapper.IPCException</c>,<see cref="IpcNotReadyError"/> 被吞掉、改回型別預設值
        /// ⇒ 這裡永遠不會擲例外,也就沒有東西可以觸發作廢。後果是安全的:唯一的使用點是 DoAutoGather 的
        /// <c>MultiMode &amp;&amp; IsEnabled &amp;&amp; AreAnyRetainersAvailableForCurrentChara()</c>,
        /// AutoRetainer 不在時第三個運算元回 <see langword="false"/>(SafeWrapper 的預設值),整條判斷式為假。
        /// <c>AutoRetainerSuppression</c> 走的是<b>不節流</b>的 <see cref="IPCSubscriber.IsReady"/>,看的永遠是當下的真值。
        /// </summary>
        internal static bool IsEnabled => IPCSubscriber.IsReadyThrottled("AutoRetainer");

        [EzIPC] internal static readonly Func<bool> IsBusy;
        [EzIPC] internal static readonly Func<Dictionary<ulong, HashSet<string>>> GetEnabledRetainers;
        [EzIPC] internal static readonly Func<bool> AreAnyRetainersAvailableForCurrentChara;
        [EzIPC] internal static readonly Action AbortAllTasks;
        [EzIPC] internal static readonly Action DisableAllFunctions;
        [EzIPC] internal static readonly Action EnableMultiMode;
        [EzIPC] internal static readonly Func<int> GetInventoryFreeSlotCount;
        // ⚠️ 提供端是 AutoRetainer/Modules/EzIPCManagers/IPC_PluginState.cs 的
        //    `public void EnqueueHET(Action onFailure)` —— 一個參數，所以這裡是 Action<Action>。
        //    參數個數不符時 Dalamud 會丟例外，而這個 class 帶的是 SafeWrapper.IPCException，
        //    例外會被吞掉、變成完全靜默的空操作。目前全 repo 沒有呼叫點。
        [EzIPC] internal static readonly Action<Action> EnqueueHET;
        [EzIPC("AutoRetainer.GC.EnqueueInitiation", applyPrefix: false)] internal static readonly Action EnqueueGCInitiation;

        // ── 具名壓制租約（憑證形狀，與 YesAlready 的租約端點逐字相同的簽章）──────────
        // 🔴 舊的 AutoRetainer.SetSuppressed 是一個**無主的單一布林**:Artisan 與 ICE 也在用同一個旗標,
        //    誰先結束誰就把別人的壓制一起解除。租約端點有憑證、可計數:全部租用者都還完,壓制才真的解除。

        // 🔴 租約會逾時(提供端 5 分鐘),所以要拿著憑證週期性 RenewSuppression 續約,
        //    而且**要把續約的回傳值當真**:回 false 代表那把已經不在了,要重新取得 —— 見 AutoRetainerSuppression。
        // ⚠️ 這個 class 帶的是 SafeWrapper.IPCException:AutoRetainer 沒安裝、或舊版沒有這幾個端點時,
        //    呼叫會丟 IpcNotReadyError 被吞掉並回傳 default(Guid.Empty / false)＝fail-safe 語意「沒拿到租約」。
        [EzIPC("AutoRetainer.AcquireSuppressionFor", applyPrefix: false)] internal static readonly Func<string, int, Guid> AcquireSuppressionFor;
        [EzIPC("AutoRetainer.RenewSuppression", applyPrefix: false)] internal static readonly Func<Guid, bool> RenewSuppression;
        [EzIPC("AutoRetainer.ReleaseSuppression", applyPrefix: false)] internal static readonly Func<Guid, bool> ReleaseSuppression;
    }
}
