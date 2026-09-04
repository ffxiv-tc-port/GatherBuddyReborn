using System;
using Dalamud.Plugin.Ipc.Exceptions;

namespace GatherBuddy.Plugin;

/// <summary>
/// 單向橋接到「塔塔露誇獎」(TataruPraise)：自動採集<b>自己</b>停下來時請它念一句。
/// </summary>
/// <remarks>
/// 🔴 <b>零組件相依。</b>只用 Dalamud 原生 CallGate 的字串契約，不引 TataruPraise 的 dll ——
/// 兩邊裝／移除任一方永遠不會弄壞另一邊。對方沒安裝時本檔的每一條路徑都是安靜的 no-op。
/// <para>
/// 🔴 契約名與情境鍵逐字取自 TataruPraise 的 <c>IpcContract.cs</c> 與
/// <c>Core/PraiseCategory.cs</c>（<c>PraiseCategory.GatherStopped</c>）。CallGate 是純字串比對，
/// 名字打錯不會有任何錯誤訊息，只會永遠得到「這個頻道沒有人註冊」——<b>靜默斷線</b>。
/// 所以字串都寫成常數，不散在呼叫點上，也不要「順手整理」。
/// </para>
/// <para>
/// 🔴 <b>只能從主執行緒呼叫。</b>IPC 的實作是在<b>呼叫端</b>的執行緒上跑的，從背景 Task 叫過去
/// 等於把對方的程式碼拉到背景執行緒。唯一的呼叫點（<c>AutoGather.NotifyStoppedItself</c>）
/// 已經用 <c>Svc.Framework.RunOnFrameworkThread</c> 包起來 —— 那裡本來就必須包，
/// 因為 IPC 端點 <c>SetAutoGatherEnabled</c> 讓別的外掛可以從背景執行緒把 <c>Enabled</c> 設成 false。
/// </para>
/// <para>
/// ⚠️ 這是<b>單向通知</b>：回傳值只拿來寫記錄，不影響自動採集的任何流程，不重試，
/// 也絕不會因此觸發任何遊戲動作。純粹「出個聲」。
/// </para>
/// </remarks>
internal static class TataruPraiseIpc
{
    /// <summary><c>Func&lt;string, bool&gt;</c>：<b>這一個情境</b>現在出不出得了聲（總開關＋這個情境的開關＋這個情境有已合成的語音）。</summary>
    /// <remarks>📌 刻意<b>不</b>看冷卻：冷卻是「這一次剛好不出聲」，不是「不能出聲」。</remarks>
    internal const string TagIsAvailableFor = "TataruPraise.IsAvailableFor";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句來念。</summary>
    internal const string TagPraise = "TataruPraise.Praise";

    /// <summary>
    /// 送過去的情境字串，逐字對應 TataruPraise 的 <c>PraiseCategory.GatherStopped</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ TataruPraise 拿這個字串當 <c>pool.json</c> 的鍵，<b>對不上就靜默不出聲</b>
    /// （它只會寫一行 Information 說這個情境沒有已合成語音的句子）。
    /// 📌 語意是「採集<b>停下來</b>」而不是「採完了」：清單跑完、卡住、背包滿都走這個鍵。
    /// </remarks>
    internal const string CategoryGatherStopped = "採集停止";

    /// <summary>
    /// 請塔塔露念一句。對方沒裝、關著、冷卻中、或池裡沒東西，這裡都是安靜的 no-op。
    /// </summary>
    /// <param name="reason">寫進記錄用的來源描述（英文原文），讓 log 分得出是哪一條邊觸發的。</param>
    /// <remarks>
    /// 🔴 <b>這個方法自己沒有去重。</b>呼叫端必須確定自己站在「狀態邊緣」上。
    /// <c>AutoGather.Enabled</c> 的 setter 有 <c>_enabled == value</c> 的 early-return 守衛，
    /// 而通知是在守衛<b>之後</b>才發的，所以那裡是真邊緣；放到輪詢路徑上的失敗形式是「一直念」。
    /// </remarks>
    internal static void TryPraise(string reason)
    {
        try
        {
            // 先問 IsAvailableFor(情境)：問的是「這一個情境」出不出得了聲——總開關關著、
            // 使用者把這個情境關掉、或這個情境一句已合成的都沒有，都在這裡擋掉。
            // 🔴 不要退回去問 IsAvailable：那個問的是「整池」，於是「別的情境有句子、
            //    我這個情境一句都沒有」時它照樣回 true，這道閘門等於白做。
            // 這一步同時兼作「對方在不在」的探測——沒註冊就會在這裡擲 IpcNotReadyError。
            if (!Dalamud.PluginInterface.GetIpcSubscriber<string, bool>(TagIsAvailableFor)
                        .InvokeFunc(CategoryGatherStopped))
                return;

            var accepted = Dalamud.PluginInterface.GetIpcSubscriber<string, bool>(TagPraise)
                                  .InvokeFunc(CategoryGatherStopped);

            // Information 級：這是「使用者說沒出聲」時唯一問得出真相的一行。
            // ⚠️ 回傳 false 不是錯誤：可能還在冷卻，也可能池裡這個情境一句都沒有。
            GatherBuddy.Log.Information(
                $"[TataruPraise] {reason}：Praise(「{CategoryGatherStopped}」) 回傳 {accepted}。");
        }
        catch (IpcNotReadyError)
        {
            // 對方沒安裝／還沒載入。這是完全正常的狀態，刻意不寫 log——沒裝的人每次停下來都會走到這裡。
        }
        catch (Exception e)
        {
            // 對方版本不合、簽名對不上、或它自己的回呼爆掉。記一筆就好，
            // 絕不要讓它往上冒打斷停止流程的收尾。
            GatherBuddy.Log.Information($"[TataruPraise] 呼叫失敗（{reason}）：{e.Message}");
        }
    }
}
