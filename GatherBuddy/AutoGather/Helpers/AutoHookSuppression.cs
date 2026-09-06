using System;
using System.Collections.Generic;
using ECommons.DalamudServices;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.Helpers;

/// <summary>
/// 請 AutoHook 在自動採集期間別動的<b>具名暫停租約</b>(<c>AutoHook.AcquireSuppressionFor</c> /
/// <c>RenewSuppression</c> / <c>ReleaseSuppression</c>),外加一條<b>退回舊
/// <c>AutoHook.SetPluginState</c> 對稱借還</b>的軌道。
/// </summary>
/// <remarks>
/// 🔴 <b>要解決的問題</b>:更早之前這裡是<b>單向寫入</b>——啟用自動採集時呼叫一次
/// <c>SetPluginState(false)</c>,而全 repo <b>連一個 <c>(true)</c> 都沒有</b> ⇒ 使用者的 AutoHook
/// 從此永遠是關的。上一輪已經補成「拍快照 → 關掉 → 停用時還原」的對稱借還,但那只是把
/// 「<b>永久</b>」降級成「<b>單次執行期間短暫</b>」:遊戲崩潰、行程被強制結束、或這個外掛自己
/// 當在中間,AutoHook 就<b>還是還不回去</b>。<br/>
/// <br/>
/// 🔑 <b>租約軌解掉的正是那個殘留</b>:提供端 <c>AutoHook/Configurations/PluginEnabledLeases.cs</c>
/// 每一把租約都有 5 分鐘的硬性到期時間,逾時自動掃除並在使用者的 log 寫一行 <c>Information</c>
/// 指名是誰壓著 —— <b>放約／逾時自動還原,不需要任何人記得還</b>。
/// 而且它疊的是 <c>Configuration.EffectivePluginEnabled</c>(＝使用者的值 &amp;&amp; 沒有任何一把租約在壓制),
/// <b>使用者自己的 <c>PluginEnabled</c> 一個位元都沒被動過</b> ⇒ 也不會再和別的借用端互相覆蓋快照。<br/>
/// <br/>
/// 🔴🔴 <b>AutoHook 的租約是 refcount 語意,拿到租約本身就開始壓制。</b>
/// <b>不要</b>再呼叫 <c>SetLeasedPluginState(憑證, false)</c> —— 那是選配的(用來在不交回租約的前提下
/// 暫時不壓制)。這一點<b>與 vnavmesh 的 <c>MovementLeases</c> 刻意不同</b>:那邊拿到租約是
/// <b>惰性</b>的(它同時管移動開關與路徑容許值兩個受控值,新租約對兩者都「沒有意見」),
/// 照 vnavmesh 的三步驟套到這裡會多押一次、照 AutoHook 的兩步驟套到 vnavmesh 會<b>完全靜默地
/// 什麼都沒發生</b>。<b>兩邊的幫手不能共用一套。</b><br/>
/// <br/>
/// 🔴 <b>雙軌:使用者手上的 AutoHook 可能還沒有這組端點。</b>
/// 先試租約軌;呼叫擲例外(舊版沒註冊 ⇒ <c>IpcNotReadyError</c>)就<b>退回上一輪那條對稱借還</b>,
/// 行為與改動前逐字相同(拍 <c>GetPluginState</c> 快照 → <c>SetPluginState(false)</c> →
/// 停用時寫回快照)。<br/>
/// ⚠️ <c>GatherBuddy.Plugin.AutoHook</c> 這個 class <b>沒有帶 SafeWrapper</b>
/// (<c>EzIPC.Init</c> 的預設就是 <c>SafeWrapper.None</c>),所以例外真的會傳出來 —— 這裡就是靠它
/// 分辨兩軌的。(帶了 <c>SafeWrapper.IPCException</c> 的話例外會被吞成 <c>default(Guid)</c>＝
/// <see cref="Guid.Empty"/>,那就與「端點在、但提供端拒絕」分不出來了。)<br/>
/// <br/>
/// 🔑 <b>兩軌互斥,而且一個「借用回合」只選一次</b>:選中退回軌之後,這一回合就一路走到
/// <see cref="RestoreNow"/> 為止,不會每個 tick 又去敲一次租約端點(那會變成每 5 秒擲一次例外、
/// 每 5 秒重打一次 <c>SetPluginState(false)</c>)。下一次 <see cref="SuppressNow"/> 才重新探測。<br/>
/// <br/>
/// 🔴 <b>續約的回傳值要當真</b>:回 <see langword="false"/> 代表那把租約已經不在了
/// (逾時、AutoHook 重載),必須重新取得,不能繼續假設自己還壓著。<br/>
/// ⚠️ <b>續約間隔不能接近租期</b>:提供端 <c>Renew</c> 的第一件事是掃除(條件 <c>now &gt;= ExpiresAt</c>),
/// 間隔只要接近租期,第一次心跳送到時那把已經被掃掉、續約<b>必定</b>回 <see langword="false"/>
/// ——那不是競態,是每次都會發生。所以租期要滿(5 分鐘)而心跳留 10 倍餘裕(30 秒)。<br/>
/// <br/>
/// 🔴 <b>提供端缺席時 fail-safe</b>:AutoHook 沒安裝／沒載入完／端點在但被拒絕(沒帶名字、
/// 已達 32 把上限)時,一律<b>照現況跑</b>,絕不卡住自動採集自己的流程。續約的週期同時也是重試機會。<br/>
/// <br/>
/// <para>
/// 🔴🔴 <b>執行緒與鎖的紀律(這一段是硬性的,不是風格)。</b>
/// <c>AutoGather.Enabled</c> 的 setter 會從<b>繪製執行緒</b>(UI 的開關按鈕、<c>GatherWindow.Ui</c>)
/// 以及<b>呼叫端的執行緒</b>(<c>GatherBuddyIpc</c> 的 <c>SetAutoGatherEnabled</c> 端點)進來,
/// 而 <see cref="Sync"/> 每個 tick 從 Framework 執行緒進來 ⇒ 狀態欄位<b>一律在 <see cref="Gate"/> 內存取</b>。
/// 但是:<br/>
/// 🔴 <b><see cref="Gate"/> 內絕不呼叫 IPC、絕不寫 log、絕不碰 ImGui、絕不做檔案 I/O</b>
/// ——<b>取值／拍快照就出來</b>。理由不是風格:
/// <list type="number">
/// <item>跨外掛 IPC 在鎖內＝<b>跨外掛鎖序</b>(GBR 的 <see cref="Gate"/> → AutoHook 的租約鎖),
/// 對面日後只要有任何一條路徑反過來呼叫 GatherBuddy 的 IPC 端點就是<b>死鎖(遊戲凍結)</b>,
/// 而且那是在別人的 repo 裡長出來的,這邊看不到。</item>
/// <item>AutoHook 那側的 <c>Flush</c> 會寫 Serilog ⇒ <b>檔案 I/O 會發生在我持鎖期間</b>,
/// 把別人的元件拉進我的鎖的臨界區。</item>
/// </list>
/// 🔑 <b>所以形狀是三段</b>:<br/>
/// ① <c>lock</c> 內只做「拍狀態快照 ＋ 決定這次要走哪一步(<see cref="Step"/>)」;<br/>
/// ② 出鎖之後才呼叫 IPC;<br/>
/// ③ 再進一次 <c>lock</c>,用 <see cref="_epoch"/> 比對「在途期間意圖有沒有被改掉」,
/// 沒被改才把結果寫回;被改掉就<b>作廢並在鎖外把剛拿到的東西還回去</b>。<br/>
/// 🔑 <b><see cref="_busy"/> 是旗標不是鎖</b>:別的執行緒撞上進行中的操作時<b>直接放棄這一次</b>
/// (下一個 tick 會再來),<b>不會有任何執行緒阻塞等待 IPC</b> —— 這就是為什麼這個設計
/// <b>結構上不可能死鎖</b>。<br/>
/// 🔴 <b>絕不使用 ECommons 的 <c>EzThrottler</c> 做這裡的節流</b>——它是整個外掛共用的靜態
/// <c>Dictionary</c> 且零同步,從 IPC／背景執行緒碰它的失敗形式不是「拿到舊值」而是<b>字典本身壞掉</b>,
/// 還會連帶弄壞同一外掛內所有模組的節流。這裡自帶 <see cref="_nextAttemptAt"/> 一個 <c>long</c> 就夠了。<br/>
/// 📌 <b>穩態每幀不配置</b>:訊息用 <c>List&lt;string&gt;?</c> 惰性配置,沒有訊息時全程是 <see langword="null"/>;
/// 沒到重試／續約時間的那些幀在第一段就回頭了。
/// </para>
/// <para>
/// 📌 <b>這不是自動接手鏈</b>:租約只叫 AutoHook <b>不要動</b>,不觸發任何新的自動化。
/// </para>
/// </remarks>
internal static class AutoHookSuppression
{
    /// <summary>要求的租期。提供端(AutoHook)的硬性上限就是 5 分鐘,這裡直接要滿。</summary>
    /// <remarks>
    /// 🔑 要滿的理由是<b>續約只當保險</b>:萬一續約整條路壞掉,仍然有滿額的緩衝時間,
    /// 而不是提早讓 AutoHook 醒過來搶著釣魚。
    /// </remarks>
    private const int LeaseMilliseconds = 300_000;

    /// <summary>續約間隔。提供端的租約壽命是 5 分鐘,這裡留 10 倍餘裕。</summary>
    private const long RenewIntervalMs = 30_000;

    /// <summary>取不到租約時的重試間隔(AutoHook 還沒載入完,或那一次呼叫剛好失敗)。</summary>
    private const long RetryIntervalMs = 5_000;

    /// <summary>
    /// 保護底下這一整組狀態欄位。
    /// <b>鎖內只做拍快照與決策:不呼叫 IPC、不寫 log、不碰 ImGui、不做檔案 I/O。</b>
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>目前持有的租約憑證;<see cref="Guid.Empty"/>＝沒有(或這一回合走的是退回軌)。</summary>
    private static Guid _lease;

    /// <summary>下一次可以嘗試續約／重新取得的時刻(<see cref="Environment.TickCount64"/> 座標系)。</summary>
    private static long _nextAttemptAt;

    /// <summary>「拿不到租約」已經回報過一次了(同一回合不重複洗版)。</summary>
    private static bool _loggedLeaseUnavailable;

    /// <summary>
    /// 這一回合走的是<b>退回軌</b>(舊的 <c>SetPluginState</c> 對稱借還)。
    /// </summary>
    /// <remarks>
    /// 🔴 它與 <see cref="_lease"/> <b>互斥</b>,而且刻意<b>不</b>每個 tick 重新探測租約端點:
    /// 舊版 AutoHook 上那會變成每 5 秒擲一次例外、每 5 秒重打一次 <c>SetPluginState(false)</c>。
    /// </remarks>
    private static bool _legacyTrack;

    /// <summary>
    /// 退回軌<b>還得回去</b>(＝ <see cref="_legacySaved"/> 是有效的快照)。
    /// </summary>
    /// <remarks>
    /// 🔴 改動前的行為要逐字保留:讀不到 <c>GetPluginState</c> 時<b>照樣關掉但不還原</b>
    /// ——那時候手上根本沒有使用者的原值,硬還一個 <see langword="false"/> 回去
    /// 等於替他做了他沒有做的選擇。
    /// </remarks>
    private static bool _legacyCanRestore;

    /// <summary>退回軌借走當下 AutoHook 的原值,還的時候寫回這個。</summary>
    private static bool _legacySaved;

    /// <summary>現在有一個跨外掛操作正在<b>鎖外</b>進行。</summary>
    /// <remarks>
    /// 🔑 <b>這是旗標不是鎖</b>:撞上的人直接放棄這一次(下一個 tick 會再來),
    /// <b>沒有任何執行緒會阻塞等待 IPC</b> —— 這是本檔不可能死鎖的原因。
    /// </remarks>
    private static bool _busy;

    /// <summary>
    /// 意圖世代。<see cref="RunRestore"/> 在「有操作在途」時把它 ++,
    /// 讓那個操作的提交階段知道自己的結果已經作廢。
    /// </summary>
    /// <remarks>
    /// 🔑 只有「放手」這一個事件會改變意圖,所以只有它 ++。
    /// 成功取得租約不必 ++(那時候不可能有第二個操作在途,<see cref="_busy"/> 擋著)。
    /// </remarks>
    private static long _epoch;

    /// <summary>「連 <c>AutoHook.Enabled</c> 都問不出來」已經回報過了(盡力而為的單次旗標)。</summary>
    private static volatile bool _loggedPresenceFailure;

    /// <summary>這一次 tick 要走的動作。</summary>
    /// <remarks>⚠️ 刻意給 <see cref="None"/> 零值:沒有零值的列舉在 <c>default</c> 時會落在無效值上。</remarks>
    private enum Step
    {
        /// <summary>什麼都不做。</summary>
        None = 0,

        /// <summary>取得租約(端點缺席時退回舊 setter 軌)。</summary>
        Acquire,

        /// <summary>續約(心跳)。</summary>
        Renew,

        /// <summary>只確認 AutoHook 還在不在(退回軌用,它沒有續約這回事)。</summary>
        Probe,
    }

    /// <summary>租用者識別字串。用外掛自己的 InternalName,不寫死字面值。</summary>
    private static string Owner => Svc.PluginInterface.InternalName;

    /// <summary>現在有沒有壓著 AutoHook(顯示用,不是判定依據)。</summary>
    internal static bool Holding
    {
        get
        {
            lock (Gate)
                return _lease != Guid.Empty || _legacyTrack;
        }
    }

    /// <summary>
    /// 請 AutoHook 在自動採集期間別動。<b>冪等</b>:已經壓著(或有操作在途)就什麼都不做。
    /// </summary>
    internal static void SuppressNow()
    {
        List<string>? logs = null;

        long epoch;
        lock (Gate)
        {
            if (!BeginLocked(PlanSuppressLocked(), out epoch))
                return;
        }

        AcquireOutsideGate(epoch, ref logs);
        Flush(logs);
    }

    /// <summary>
    /// 把租約狀態對齊「自動採集現在該不該壓著 AutoHook」。<b>每個 tick 呼叫一次,冪等。</b>
    /// </summary>
    /// <remarks>
    /// 📌 條件刻意是<b>單純的 <c>Enabled</c></b>,不是 <c>AutoRetainerSuppression</c> 那個
    /// <c>Enabled &amp;&amp; !Waiting</c>:自動採集在等待期間仍然可能走到釣魚點,那時候讓 AutoHook
    /// 醒過來搶竿正是這整份檔要擋的事。<b>壓制窗口與改動前完全相同</b>(啟用時開始、停用時結束)。
    /// </remarks>
    internal static void Sync(bool shouldHold)
    {
        if (!shouldHold)
        {
            RestoreNow("自動採集沒有在跑");
            return;
        }

        List<string>? logs = null;

        Step step;
        Guid lease;
        long epoch;
        lock (Gate)
        {
            step  = PlanSyncLocked(out lease);
            if (!BeginLocked(step, out epoch))
                return;
        }

        switch (step)
        {
            case Step.Acquire:
                AcquireOutsideGate(epoch, ref logs);
                break;
            case Step.Renew:
                RenewOutsideGate(lease, epoch, ref logs);
                break;
            case Step.Probe:
                ProbeOutsideGate(epoch, ref logs);
                break;
            default:
                // 🔴 走不到(BeginLocked 已經濾掉 None),但將來多一個 Step 又忘了補 case 時,
                //    _busy 會**永遠**卡著 ⇒ 這個外掛從此再也壓不住 AutoHook,而且完全靜默。
                lock (Gate)
                    _busy = false;
                break;
        }

        Flush(logs);
    }

    /// <summary>把壓制解除(沒壓著就什麼都不做)。</summary>
    internal static void RestoreNow(string reason)
    {
        List<string>? logs = null;
        RunRestore(reason, ref logs);
        Flush(logs);
    }

    // ── 第一段:拍快照與決策(全部在 Gate 內,一行 IPC / log 都沒有)─────────────

    /// <summary><b>呼叫端必須先持有 <see cref="Gate"/>。</b></summary>
    private static Step PlanSuppressLocked()
    {
        if (_busy || _lease != Guid.Empty || _legacyTrack)
            return Step.None;
        if (Environment.TickCount64 < _nextAttemptAt)
            return Step.None;
        return Step.Acquire;
    }

    /// <summary><b>呼叫端必須先持有 <see cref="Gate"/>。</b></summary>
    private static Step PlanSyncLocked(out Guid lease)
    {
        lease = Guid.Empty;
        if (_busy)
            return Step.None;
        if (Environment.TickCount64 < _nextAttemptAt)
            return Step.None;
        if (_legacyTrack)
            return Step.Probe;
        if (_lease != Guid.Empty)
        {
            lease = _lease;
            return Step.Renew;
        }

        return Step.Acquire;
    }

    /// <summary>
    /// 把決策定案:非 <see cref="Step.None"/> 就記下世代並豎起 <see cref="_busy"/>。
    /// <b>呼叫端必須先持有 <see cref="Gate"/>。</b>
    /// </summary>
    private static bool BeginLocked(Step step, out long epoch)
    {
        epoch = _epoch;
        if (step == Step.None)
            return false;

        _busy = true;
        return true;
    }

    // ── 第二段＋第三段:鎖外的 IPC,以及帶世代比對的提交 ────────────────────────

    /// <summary>取得租約;端點缺席就退回舊 setter 軌。<b>絕不可在持有 <see cref="Gate"/> 時呼叫。</b></summary>
    private static void AcquireOutsideGate(long epoch, ref List<string>? logs)
    {
        var  present          = false;
        var  acquired         = Guid.Empty;
        var  legacyChosen     = false;
        var  legacySaved      = false;
        var  legacyCanRestore = false;

        try
        {
            present = IsAutoHookPresent(ref logs);
            if (present)
            {
                Exception? notReady = null;
                try
                {
                    // 🔴 直接呼叫、不用 ?.Invoke:欄位若沒被 EzIPC 指派好,NullReferenceException 也該走到
                    //    下面的退回軌(安全方向)。用 ?.Invoke 會靜默變成 Guid.Empty,與「端點在但被拒絕」混在一起。
                    acquired = AutoHook.AcquireSuppressionFor(Owner, LeaseMilliseconds);
                }
                catch (Exception e)
                {
                    notReady = e;
                }

                if (notReady != null)
                {
                    (logs ??= []).Add(
                        $"[AutoHook 壓制] AutoHook 沒有暫停租約端點(AutoHook.AcquireSuppressionFor ⇒ {notReady.GetType().Name}:{notReady.Message}),"
                      + "多半是 AutoHook 版本較舊。改用舊的「拍快照 → 關掉 → 停用時還原」路徑,行為與更新前相同"
                      + $"(缺點是遊戲崩潰或行程被強制結束時還不回去;租約軌則會在最多 {LeaseMilliseconds / 1000} 秒後自動逾時)。");
                    legacyChosen = true;
                    LegacySuppressOutsideGate(ref legacySaved, ref legacyCanRestore, ref logs);
                }
            }
        }
        catch (Exception e)
        {
            // 走不到,但 _busy 絕不能因為任何逸出的例外而卡住。
            (logs ??= []).Add($"[AutoHook 壓制] 取得暫停租約的過程擲出未預期的例外:{e.GetType().Name}:{e.Message}。這一輪不壓制,稍後重試。");
        }

        var  now       = Environment.TickCount64;
        bool stale;
        var  reportUnavailable = false;

        lock (Gate)
        {
            _busy = false;
            stale = epoch != _epoch;
            if (!stale)
            {
                if (!present)
                {
                    _nextAttemptAt = now + RetryIntervalMs;
                }
                else if (acquired != Guid.Empty)
                {
                    _lease                  = acquired;
                    _loggedLeaseUnavailable = false;
                    _nextAttemptAt          = now + RenewIntervalMs;
                }
                else if (legacyChosen)
                {
                    _legacyTrack      = true;
                    _legacyCanRestore = legacyCanRestore;
                    _legacySaved      = legacySaved;
                    _nextAttemptAt    = now + RenewIntervalMs;
                }
                else
                {
                    // 端點在、但提供端拒絕(沒帶名字,或已達 32 把租約上限)。
                    // 🔴 這裡**不**退回舊 setter:端點既然在,退回去只會把「單向寫入使用者的設定」那個
                    //    已經修好的問題再帶回來。照現況跑就好,重試間隔到了再試一次。
                    if (!_loggedLeaseUnavailable)
                    {
                        _loggedLeaseUnavailable = true;
                        reportUnavailable       = true;
                    }

                    _nextAttemptAt = now + RetryIntervalMs;
                }
            }
        }

        if (reportUnavailable)
            (logs ??= []).Add(
                $"[AutoHook 壓制] 向 AutoHook 取得暫停租約失敗(端點 AutoHook.AcquireSuppressionFor 回了空憑證,租用者「{Owner}」)。"
              + $"自動採集照常繼續,但 AutoHook 可能會在採集途中搶著釣魚。{RetryIntervalMs / 1000} 秒後重試。");

        if (!stale)
        {
            if (acquired != Guid.Empty)
                (logs ??= []).Add(
                    $"[AutoHook 壓制] 已請 AutoHook 在自動採集期間不要動(具名租約 {acquired},租用者「{Owner}」)"
                  + " —— 避免它搶著釣魚。使用者自己的「Enable AutoHook」設定一個位元都沒被動過,"
                  + $"停用自動採集或卸載時歸還,最壞情況也會在 {LeaseMilliseconds / 1000} 秒後自動逾時。");
            return;
        }

        // 在途期間有人放手了(多半是使用者停用了自動採集,或外掛正在卸載)⇒ 這次的結果作廢,
        // 而且要立刻還回去。🔴 一樣在鎖外做。
        RollbackOutsideGate(acquired, legacyChosen, legacyCanRestore, legacySaved, ref logs);
    }

    /// <summary>續約(心跳)。<b>絕不可在持有 <see cref="Gate"/> 時呼叫。</b></summary>
    private static void RenewOutsideGate(Guid lease, long epoch, ref List<string>? logs)
    {
        var present = false;
        var renewed = false;

        try
        {
            present = IsAutoHookPresent(ref logs);
            if (present)
            {
                try
                {
                    renewed = AutoHook.RenewSuppression(lease);
                }
                catch (Exception e)
                {
                    (logs ??= []).Add(
                        $"[AutoHook 壓制] 暫停租約 {lease} 續約時擲出 {e.GetType().Name}:{e.Message}。當作那把已經不在了,重新取得一把。");
                }
            }
        }
        catch (Exception e)
        {
            (logs ??= []).Add($"[AutoHook 壓制] 續約的過程擲出未預期的例外:{e.GetType().Name}:{e.Message}。");
        }

        var  now = Environment.TickCount64;
        var  dropped = false;
        var  presenceLost = false;

        lock (Gate)
        {
            _busy = false;
            if (epoch != _epoch || _lease != lease)
                return;

            if (!present)
            {
                // AutoHook 不在(或被卸載了)。它的租約表跟著它一起消失,這裡只要把自己的狀態歸零。
                _lease         = Guid.Empty;
                _nextAttemptAt = now + RetryIntervalMs;
                presenceLost   = true;
            }
            else if (renewed)
            {
                _nextAttemptAt = now + RenewIntervalMs;
            }
            else
            {
                // 🔴 續約回 false＝那把已經不在了(逾時、AutoHook 重載),這時候**不能**當成還壓著。
                //    這裡只把憑證丟掉,下一個 tick 的 Sync 會重新取得一把。
                _lease         = Guid.Empty;
                _nextAttemptAt = 0;
                dropped        = true;
            }
        }

        if (presenceLost)
            (logs ??= []).Add(
                $"[AutoHook 壓制] AutoHook 已經不在了,本機的暫停租約 {lease} 狀態一併歸零(AutoHook 卸載時租約表本來就跟著消失)。");
        else if (dropped)
            (logs ??= []).Add(
                $"[AutoHook 壓制] 暫停租約 {lease} 續約失敗(多半是 AutoHook 重載,或這把已經逾時),重新取得一把。");
    }

    /// <summary>退回軌的週期檢查:只確認 AutoHook 還在不在。<b>絕不可在持有 <see cref="Gate"/> 時呼叫。</b></summary>
    private static void ProbeOutsideGate(long epoch, ref List<string>? logs)
    {
        var present = false;
        try
        {
            present = IsAutoHookPresent(ref logs);
        }
        catch (Exception e)
        {
            (logs ??= []).Add($"[AutoHook 壓制] 確認 AutoHook 是否還在時擲出未預期的例外:{e.GetType().Name}:{e.Message}。");
        }

        var now = Environment.TickCount64;
        lock (Gate)
        {
            _busy = false;
            if (epoch != _epoch)
                return;

            // 🔴 退回軌的快照**刻意不清掉**,即使 AutoHook 不在:那條路走的是舊 AutoHook,
            //    重載回來時它的啟用開關仍然是被我們關掉的那個值,快照還有意義,
            //    留著讓 RestoreNow 有機會把使用者的原值還回去。租約則不同——它真的隨外掛消失。
            _nextAttemptAt = now + (present ? RenewIntervalMs : RetryIntervalMs);
        }
    }

    /// <summary>
    /// 退回軌:記下 AutoHook 目前的狀態,然後把它關掉。
    /// <b>絕不可在持有 <see cref="Gate"/> 時呼叫</b>(它會打三支 IPC)。
    /// </summary>
    /// <remarks>🔴 這一段的行為與改動前逐字相同,包含它已知的極限。</remarks>
    private static void LegacySuppressOutsideGate(ref bool saved, ref bool canRestore, ref List<string>? logs)
    {
        try
        {
            saved      = AutoHook.GetPluginState();
            canRestore = true;
        }
        catch (Exception e)
        {
            // 🔴 舊版 AutoHook 沒有 GetPluginState 這個端點 ⇒ 讀不到原值就<b>還不回去</b>。
            //    這裡刻意維持改動前的行為(照樣關掉),但明講「這次不會自動還原」,
            //    不要讓使用者以為已經修好了。Information 級,使用者回報得到。
            canRestore = false;
            (logs ??= []).Add(
                $"[AutoHook 借還] 讀不到 AutoHook 目前的啟用狀態(端點 AutoHook.GetPluginState,多半是 AutoHook 版本較舊):{e.Message}。"
              + " 這次仍然會關閉 AutoHook 以免它干擾自動採集,但停用自動採集時不會自動還原 —— 請更新 AutoHook 後再試。");
        }

        try
        {
            AutoHook.SetPluginState(false);
        }
        catch (Exception e)
        {
            // ⚠️ 改動前這一行是裸呼叫(SetPluginState 在提供端不存在的機率極低,它比 GetPluginState
            //    老得多),但既然已經走到「這版 AutoHook 很舊」的分支,就不要讓它把例外一路擲出去。
            canRestore = false;
            (logs ??= []).Add(
                $"[AutoHook 借還] 呼叫 AutoHook.SetPluginState(false) 失敗:{e.GetType().Name}:{e.Message}。"
              + "這版 AutoHook 連舊端點都沒有,自動採集期間它可能會搶著釣魚。");
            return;
        }

        if (canRestore)
            (logs ??= []).Add(
                $"[AutoHook 借還] 已借走 AutoHook 的啟用開關並關閉它(借走當下的原值是 {saved}),避免它在自動採集期間搶著釣魚。停用自動採集或卸載時會還原回去。");
    }

    /// <summary>把剛拿到但已經作廢的東西還回去。<b>絕不可在持有 <see cref="Gate"/> 時呼叫。</b></summary>
    private static void RollbackOutsideGate(Guid acquired, bool legacyChosen, bool canRestore, bool saved, ref List<string>? logs)
    {
        if (acquired != Guid.Empty)
        {
            try
            {
                AutoHook.ReleaseSuppression(acquired);
            }
            catch (Exception e)
            {
                (logs ??= []).Add(
                    $"[AutoHook 壓制] 作廢的暫停租約 {acquired} 歸還失敗:{e.GetType().Name}:{e.Message}。"
                  + $"它會在最多 {LeaseMilliseconds / 1000} 秒後自動逾時。");
                return;
            }

            (logs ??= []).Add(
                $"[AutoHook 壓制] 取得暫停租約 {acquired} 的期間自動採集已經停用,立刻歸還(沒有留下任何壓制)。");
            return;
        }

        if (!legacyChosen || !canRestore)
            return;

        try
        {
            AutoHook.SetPluginState(saved);
            (logs ??= []).Add($"[AutoHook 借還] 借走 AutoHook 的啟用開關的期間自動採集已經停用,立刻還原為 {saved}。");
        }
        catch (Exception e)
        {
            (logs ??= []).Add($"[AutoHook 借還] 還原 AutoHook 的啟用狀態失敗(取得期間已停用):{e.Message}。使用者可能需要自己去 AutoHook 把它打開。");
        }
    }

    /// <summary>放手:拍快照、清狀態(鎖內),再在鎖外把東西還回去。</summary>
    private static void RunRestore(string reason, ref List<string>? logs)
    {
        Guid lease;
        bool legacy, canRestore, saved;

        lock (Gate)
        {
            // 🔑 只有「有操作在途」時才需要作廢它 —— 沒在途就不要動世代,
            //    否則自動採集沒開的每一幀都在把世代往上推。
            if (_busy)
                _epoch++;

            _nextAttemptAt          = 0;
            _loggedLeaseUnavailable = false;

            lease      = _lease;
            legacy     = _legacyTrack;
            canRestore = _legacyCanRestore;
            saved      = _legacySaved;

            _lease            = Guid.Empty;
            _legacyTrack      = false;
            _legacyCanRestore = false;
        }

        // 📌 穩態(自動採集沒開)就在這裡回頭:零 IPC、零配置。
        if (lease == Guid.Empty && !legacy)
            return;

        // ── 以下全部在鎖外 ──
        if (!IsAutoHookPresent(ref logs))
            // AutoHook 被卸載了:它的租約表與它的設定都跟著它走,狀態已經歸零,沒有別的事要做。
            return;

        if (lease != Guid.Empty)
        {
            try
            {
                AutoHook.ReleaseSuppression(lease);
                (logs ??= []).Add($"[AutoHook 壓制] 已歸還 AutoHook 的暫停租約 {lease}({reason})。");
            }
            catch (Exception e)
            {
                (logs ??= []).Add(
                    $"[AutoHook 壓制] 歸還暫停租約 {lease} 失敗({reason}):{e.GetType().Name}:{e.Message}。"
                  + $"那把租約會在最多 {LeaseMilliseconds / 1000} 秒後自動逾時,AutoHook 屆時恢復使用者自己的設定。");
            }
        }

        if (legacy && canRestore)
        {
            try
            {
                AutoHook.SetPluginState(saved);
                (logs ??= []).Add($"[AutoHook 借還] 已把 AutoHook 的啟用狀態還原為 {saved}({reason})。");
            }
            catch (Exception e)
            {
                (logs ??= []).Add($"[AutoHook 借還] 還原 AutoHook 的啟用狀態失敗({reason}):{e.Message}。使用者可能需要自己去 AutoHook 把它打開。");
            }
        }
    }

    /// <summary>
    /// AutoHook 現在在不在(＝有沒有裝、載入完了沒有,不是「有沒有啟用」)。
    /// <b>絕不可在持有 <see cref="Gate"/> 時呼叫。</b>
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>IPCSubscriber.IsReady</c> 帶的是 <c>ignoreCache: true</c>,每次呼叫都會把 Dalamud 的
    /// <c>InstalledPlugins</c> 整表反射掃一遍 —— 所以呼叫點一律先過 <see cref="_nextAttemptAt"/> 的閘門,
    /// <b>不要每幀問</b>。它本身也可能寫 log(ECommons 第一次建快取時),更不該在鎖內。
    /// </remarks>
    private static bool IsAutoHookPresent(ref List<string>? logs)
    {
        try
        {
            return AutoHook.Enabled;
        }
        catch (Exception e)
        {
            if (!_loggedPresenceFailure)
            {
                _loggedPresenceFailure = true;
                (logs ??= []).Add(
                    $"[AutoHook 壓制] 連「AutoHook 有沒有載入」都問不出來:{e.GetType().Name}:{e.Message}。"
                  + "當成沒有安裝處理(不壓制),這行訊息只會出現一次。");
            }

            return false;
        }
    }

    /// <summary>把收在鎖內／鎖外的訊息寫出去。<b>一定要在鎖外呼叫。</b></summary>
    /// <remarks>🔴 一律 Information:使用者的記錄等級只會濾掉 Verbose,而這些訊息正是他回報
    /// 「AutoHook 突然不會自動上鉤了」時唯一的線索。<b>不要用 DuoLog</b>——那會直接洗聊天視窗。</remarks>
    private static void Flush(List<string>? logs)
    {
        if (logs == null)
            return;

        foreach (var line in logs)
            GatherBuddy.Log.Information(line);
    }
}
