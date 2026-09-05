using System;
using GatherBuddy.Plugin;

namespace GatherBuddy.AutoGather.Helpers;

/// <summary>
/// 對 AutoHook 全域啟用開關(<c>AutoHook.SetPluginState</c>)的<b>對稱借還</b>。
/// </summary>
/// <remarks>
/// 🔴 <b>要解決的問題</b>:改動前這裡是<b>單向寫入</b>——啟用自動採集時呼叫一次
/// <c>SetPluginState(false)</c>,而全 repo <b>連一個 <c>(true)</c> 都沒有</b>。
/// 提供端(<c>AutoHook/IPC/AutoHookIPC.cs</c>)寫的是 <c>Configuration.PluginEnabled</c> 並且
/// 立刻 <c>Service.Save()</c> ⇒ <b>會寫進使用者的設定檔,重開遊戲也回不來</b>。
/// 使用者只是跑了一次自動採集,他的 AutoHook 就從此是關的,而且沒有任何訊息。<br/>
/// <br/>
/// 🔑 <b>對稱性反證就在同一個 setter 裡</b>:停用自動採集的分支早就有 <c>YesAlready.Unlock()</c>、
/// <c>AutoRetainerSuppression.ReleaseNow(...)</c>、<c>StopNavigation()</c> —— <b>唯獨漏掉 AutoHook</b>。<br/>
/// <br/>
/// 🔑 <b>形狀比照同目錄的 <see cref="AutoRetainerSuppression"/></b>,但<b>刻意不做租約</b>:
/// AutoHook 那側目前沒有租約端點(艦隊裡有租約協定的是 YesAlready / WrathCombo /
/// AutoRetainer / TextAdvance,AutoHook 與 vnavmesh 正好是缺的那兩個)。<br/>
/// <br/>
/// 🔴🔴 <b>已知極限,不要當成根治</b>:
/// <list type="bullet">
/// <item>沒有租約就沒有逾時保險 ⇒ <b>遊戲崩潰或行程被強制結束時仍然還不回去</b>。</item>
/// <item>快照仍然會與別的外掛互相覆蓋(例如 ICE 的 <c>Task_Fishing</c> / <c>Task_DualClass</c>
/// 也是只開不關地寫同一個開關)。</item>
/// </list>
/// ⇒ 這只是把「<b>永久且寫進磁碟</b>」降級成「<b>單次執行期間短暫</b>」。
/// 根治要把租約協定移植到 AutoHook 那側(另案)。
/// </remarks>
internal static class AutoHookSuppression
{
    /// <summary>現在有沒有借著(＝ <see cref="_saved"/> 是否有效、該不該還)。</summary>
    private static bool _suppressed;

    /// <summary>借走當下 AutoHook 的原值,還的時候寫回這個。</summary>
    private static bool _saved;

    /// <summary>現在有沒有壓著 AutoHook(顯示用,不是判定依據)。</summary>
    internal static bool Holding => _suppressed;

    /// <summary>
    /// 記下 AutoHook 目前的狀態,然後把它關掉。<b>冪等</b>:已經借著就什麼都不做。
    /// </summary>
    internal static void SuppressNow()
    {
        if (_suppressed)
            return;

        // AutoHook 沒安裝就沒事做(AutoHook.Enabled 其實是「有沒有裝」,不是「有沒有啟用」)。
        if (!AutoHook.Enabled)
            return;

        bool saved;
        try
        {
            saved = AutoHook.GetPluginState();
        }
        catch (Exception e)
        {
            // 🔴 舊版 AutoHook 沒有 GetPluginState 這個端點 ⇒ 讀不到原值就<b>還不回去</b>。
            //    這裡刻意維持改動前的行為(照樣關掉),但明講「這次不會自動還原」,
            //    不要讓使用者以為已經修好了。Information 級,使用者回報得到。
            GatherBuddy.Log.Information(
                $"[AutoHook 借還] 讀不到 AutoHook 目前的啟用狀態(端點 AutoHook.GetPluginState,多半是 AutoHook 版本較舊):{e.Message}。"
              + " 這次仍然會關閉 AutoHook 以免它干擾自動採集,但停用自動採集時不會自動還原 —— 請更新 AutoHook 後再試。");
            AutoHook.SetPluginState(false);
            return;
        }

        _saved      = saved;
        _suppressed = true;
        AutoHook.SetPluginState(false);
        GatherBuddy.Log.Information(
            $"[AutoHook 借還] 已借走 AutoHook 的啟用開關並關閉它(借走當下的原值是 {_saved}),避免它在自動採集期間搶著釣魚。停用自動採集或卸載時會還原回去。");
    }

    /// <summary>把 AutoHook 的啟用狀態還原成借走當下的值(沒借就什麼都不做)。</summary>
    internal static void RestoreNow(string reason)
    {
        if (!_suppressed)
            return;

        _suppressed = false;

        // AutoHook 被卸載了:它的設定跟著它走,這裡只要把自己的狀態歸零。
        if (!AutoHook.Enabled)
            return;

        try
        {
            AutoHook.SetPluginState(_saved);
            GatherBuddy.Log.Information($"[AutoHook 借還] 已把 AutoHook 的啟用狀態還原為 {_saved}({reason})。");
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Information($"[AutoHook 借還] 還原 AutoHook 的啟用狀態失敗({reason}):{e.Message}。使用者可能需要自己去 AutoHook 把它打開。");
        }
    }
}
