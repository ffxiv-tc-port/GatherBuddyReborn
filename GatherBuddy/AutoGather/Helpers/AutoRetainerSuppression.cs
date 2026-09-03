using System;
using ECommons.DalamudServices;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.Helpers;

/// <summary>
/// 對 AutoRetainer 的<b>具名壓制租約</b>(<c>AutoRetainer.AcquireSuppressionFor</c>/
/// <c>RenewSuppression</c>/<c>ReleaseSuppression</c>)。
/// </summary>
/// <remarks>
/// 🔴 <b>要解決的問題</b>:AutoRetainer 的 MultiMode 只要判定 <c>!IsOccupied()</c> 成立就會去跑僱員/換角色,
/// 而 AutoGather 在兩個節點之間那幾秒正好符合 —— 結果是採集跑到一半角色被登出、狀態機停在半路。<br/>
/// <br/>
/// 🔴 <b>為什麼不用舊的 <c>AutoRetainerApi.Suppressed</c></b>(本 repo 在 <c>AutoGather.Waiting</c> 的
/// setter 裡仍然有一處,行為刻意不動):那是一個<b>無主的單一布林</b>,Artisan 與 ICE 也在寫同一個旗標,
/// 誰先結束誰就把別人的壓制一起解除。租約端點是有憑證、可計數的。<br/>
/// <br/>
/// 🔑 <b>形狀＝<see cref="Guid"/> 憑證</b>(2026-09-03 起與 YesAlready 的租約端點統一)。
/// 改動前這裡宣告的是 <c>Func&lt;string, bool&gt;</c>(用租用者名字當鍵),與 YesAlready 那套的
/// <c>Func&lt;string, int, Guid&gt;</c> 形狀不一致 —— 而 Dalamud 的 CallGate 在型別對不上時<b>不報錯</b>,
/// 會走 JSON 來回轉換(<c>CallGateChannel.ConvertObject</c>),<c>Guid</c> 轉 <c>string</c> 這個方向
/// <b>轉得過去</b>,於是「歸還租約」會變成一個回傳 <see langword="true"/> 的空操作。
/// 兩邊統一成同一個形狀就是為了讓這種寫錯不可能發生。<br/>
/// <br/>
/// 🔴 <b>提供端缺席時 fail-safe</b>:AutoRetainer 沒安裝/沒載入完/舊版沒有這個端點時,一律
/// <b>當作沒有壓制、照現況跑</b>,絕不卡住 AutoGather 自己的流程。續約的週期同時也是重試機會。<br/>
/// <br/>
/// 🔴 <b>續約的回傳值要當真</b>:回 <see langword="false"/> 代表那把租約已經不在了
/// (逾時、AutoRetainer 重載、或使用者按了它主視窗的「取消」),必須重新取得,
/// 不能繼續假設自己還壓著。<br/>
/// <br/>
/// 📌 <b>什麼時候該壓著</b>:<c>Enabled &amp;&amp; !Waiting</c>。
/// <c>Waiting</c> 代表 AutoGather 自己也沒事做(沒有可採的東西,或正在把場子讓給 AutoRetainer 的 MultiMode),
/// 那時候擋著 AutoRetainer 沒有道理 —— 這也讓既有的「Wait for AutoRetainer Multi-mode」設定照常運作。<br/>
/// <br/>
/// 📌 <b>這不是自動接手鏈</b>:租約只叫 AutoRetainer <b>不要動</b>,不觸發任何新的自動化。
/// </remarks>
internal static class AutoRetainerSuppression
{
    /// <summary>要求的租期。提供端(AutoRetainer)的硬性上限就是 5 分鐘,這裡直接要滿。</summary>
    /// <remarks>
    /// 🔑 要滿的理由是<b>續約只當保險</b>:萬一續約整條路壞掉,仍然有滿額的緩衝時間,
    /// 而不是提早讓 AutoRetainer 醒過來搶角色。
    /// </remarks>
    private const int LeaseMilliseconds = 300_000;

    /// <summary>續約間隔。提供端的租約壽命是 5 分鐘,這裡留 10 倍餘裕。</summary>
    private const long RenewIntervalMs = 30_000;

    /// <summary>取不到租約時的重試間隔(AutoRetainer 還沒載入完,或那一次呼叫剛好失敗)。</summary>
    private const long RetryIntervalMs = 5_000;

    /// <summary>目前持有的租約憑證;<see cref="Guid.Empty"/>＝沒有。</summary>
    private static Guid _lease;

    private static bool _loggedUnavailable;
    private static long _nextAttemptAt;

    /// <summary>租用者識別字串。用外掛自己的 InternalName,不寫死字面值。</summary>
    private static string Owner => Svc.PluginInterface.InternalName;

    /// <summary>現在有沒有壓著 AutoRetainer(顯示用,不是判定依據)。</summary>
    internal static bool Holding => _lease != Guid.Empty;

    /// <summary>
    /// 把租約狀態對齊「AutoGather 現在該不該壓著 AutoRetainer」。<b>每個 tick 呼叫一次,冪等。</b>
    /// </summary>
    internal static void Sync(bool shouldHold)
    {
        if (!shouldHold)
        {
            ReleaseNow("AutoGather 沒有在採集");
            return;
        }

        if (!IPCSubscriber.IsReady("AutoRetainer"))
        {
            // AutoRetainer 不在(或被卸載了)。它的租約表跟著它一起消失,這裡只要把自己的狀態歸零。
            if (_lease != Guid.Empty)
            {
                _lease = Guid.Empty;
                GatherBuddy.Log.Information("[AutoRetainer 壓制] AutoRetainer 已經不在了,本機的壓制租約狀態一併歸零(AutoRetainer 卸載時租約表本來就跟著消失)。");
            }

            _nextAttemptAt = 0;
            return;
        }

        var now = Environment.TickCount64;
        if (now < _nextAttemptAt)
            return;

        // 🔴 已經有憑證就先續約。續約回 false＝那把已經不在了(逾時/AutoRetainer 重載/
        //    使用者按了「取消」),這時候**不能**當成還壓著,要掉頭重新取得。
        if (_lease != Guid.Empty)
        {
            if (AutoRetainer.RenewSuppression?.Invoke(_lease) == true)
            {
                _nextAttemptAt = now + RenewIntervalMs;
                return;
            }

            GatherBuddy.Log.Information($"[AutoRetainer 壓制] 壓制租約 {_lease} 續約失敗(多半是 AutoRetainer 重載、或使用者按了它主視窗的「取消」),重新取得一把。");
            _lease = Guid.Empty;
        }

        var acquired = AutoRetainer.AcquireSuppressionFor?.Invoke(Owner, LeaseMilliseconds) ?? Guid.Empty;
        if (acquired != Guid.Empty)
        {
            GatherBuddy.Log.Information($"[AutoRetainer 壓制] 已請 AutoRetainer 在自動採集期間不要動(具名租約 {acquired},租用者「{Owner}」)—— 避免採集節點之間那幾秒被它拿去跑僱員或換角色。");

            _lease             = acquired;
            _loggedUnavailable = false;
            _nextAttemptAt     = now + RenewIntervalMs;
            return;
        }

        // 拿不到租約:可能是 AutoRetainer 還沒把 IPC 註冊好,也可能是舊版沒有這個端點。
        // 🔴 這裡**不**卡住 AutoGather 的流程,照現況跑就好;只是 AutoRetainer 可能會來搶。
        if (!_loggedUnavailable)
        {
            GatherBuddy.Log.Information($"[AutoRetainer 壓制] 向 AutoRetainer 取得壓制租約失敗(端點 AutoRetainer.AcquireSuppressionFor,租用者「{Owner}」)。自動採集照常繼續;若 AutoRetainer 版本太舊沒有這個端點,它仍可能在節點之間把角色拿去跑僱員。{RetryIntervalMs / 1000} 秒後重試。");
            _loggedUnavailable = true;
        }

        _nextAttemptAt = now + RetryIntervalMs;
    }

    /// <summary>把租約還回去(沒持有就什麼都不做)。</summary>
    internal static void ReleaseNow(string reason)
    {
        _nextAttemptAt     = 0;
        _loggedUnavailable = false;
        if (_lease == Guid.Empty)
            return;

        var id = _lease;
        _lease = Guid.Empty;
        if (!IPCSubscriber.IsReady("AutoRetainer"))
            return;

        AutoRetainer.ReleaseSuppression?.Invoke(id);
        GatherBuddy.Log.Information($"[AutoRetainer 壓制] 已歸還 AutoRetainer 的壓制租約 {id}({reason})。");
    }
}
