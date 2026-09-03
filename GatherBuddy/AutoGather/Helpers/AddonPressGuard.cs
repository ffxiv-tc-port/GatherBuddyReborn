using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GatherBuddy.AutoGather.Helpers;

/// <summary>
/// 「同一扇視窗的同一個按法,按過就不要再按,直到它真的收掉」的共用閘門。
/// AutoGather 所有對 addon 的按法(<c>Callback.Fire</c>、<c>AddonMaster</c> 的 <c>Yes()</c>/<c>Select()</c>/<c>Click()</c>/
/// <c>Commence()</c>/<c>Materialize()</c>/<c>RepairAll()</c>/<c>Automatic()</c>)都要先問過 <see cref="TryBeginPress(string, nint, string, int)"/>。
/// </summary>
/// <remarks>
/// 🔴🔴 <b>存在的唯一理由是原生 AccessViolation</b>:<c>SelectYesno</c> 這類確認框被按下之後
/// 有<b>「正在關閉中」的幾幀</b>,這段期間 <c>GetAddonByName</c> 仍然回得到實例、
/// <c>IsVisible</c>、<c>IsFullyLoaded()</c> 與 <c>IsReady</c>(CS <c>Flags1A1</c> bit0「OnSetup 已呼叫」)都還成立 ——
/// 也就是說 <c>AutoGather.GetAddon&lt;T&gt;</c> 的檢查<b>三關全過、擋不住這個窗口</b>。
/// 此時再對它 <c>Callback.Fire</c>/送 <c>ReceiveEvent</c> 就是原生 AccessViolationException:
/// 在 .NET Core 是 corrupted-state exception,<c>try</c>/<c>catch</c> 完全攔不到,遊戲當場關閉 ——
/// <b>唯一的防護是「不要送第二次」,不是「送了再接住」</b>。
/// <para>
/// ⚠️ <c>TaskManager.DelayNext</c>/<c>EnqueueActionWithDelay</c> 的 <c>ExecutionDelay</c><b>不是</b>防護:
/// 它們記的是「距離上一個動作多久」而不是「這扇窗已經按過」,<c>ExecutionDelay</c> 預設 0,
/// 而 Diadem 排隊分支根本沒有延遲(每 2 個 framework tick 就對還在的窗再 Enqueue 一次)。
/// </para>
/// <para>
/// 🔴 「按過的按鈕會被遊戲停用所以不會重按」<b>不成立</b>:ECommons <c>AddonMaster.SelectYesno.Yes()</c>
/// 遇到停用的「是」鈕會翻 <c>NodeFlags</c> 強制啟用再點下去。
/// </para>
/// <para>
/// 🔑 <b>做法</b>:按下之前先登記「這個名字底下的哪一個實例、被送過哪一種按法」,
/// 在觀察到那扇窗真的走完生命週期之前不准再送同一種。
/// 🔴 全程只做<b>位址等值比較,永遠不解參</b> —— 被記下的那個位址隨時可能已經失效。
/// </para>
/// <para>
/// 📌 <b>粒度=(窗,位址,參數組)</b>:AutoGather 有多處<b>刻意</b>在同一扇還開著的窗上連送:
/// Gathering 視窗同一格 index 每個 integrity 合法重按一次、ContentsFinderMenu 連送 <c>(true,0)</c> 與 <c>(false,-2)</c>、
/// Materialize 每輪對同窗重送 <c>(2,0)</c>、PurifyItemSelector 每輪重送 <c>(12,0)</c>。
/// 只看位址會把這些正常流程一起擋掉,所以擋的粒度是「同一扇窗 ＋ 同一組參數」= 真正的「重按」。
/// 「回答一次就結束」的窗(<see cref="SingleAnswerAddons"/>)白名單併 key:不管走哪條路徑、送什麼參數都算同一次按。
/// <c>Callback.Fire(addon, true, -1)</c> 這種關窗按法登記在 <see cref="ClosePressKey"/> 底下,是<b>萬用鍵</b>:
/// 關過之後、還沒觀察到它收掉之前,同位址的<b>任何</b>按法都不准(Spiritbond 關 Materialize 後下一輪的 <c>(2,0)</c>、
/// Repair 關窗後下一輪的 RepairAll、Purify 關 selector 後下一輪的 <c>(12,0)</c> 就是這個形狀)。
/// </para>
/// <para>
/// <b>解除封鎖有兩條互補的觀察點</b>(兩條都只會讓封鎖<b>提早</b>解除,不會延後):
/// <list type="number">
/// <item><b>輪詢</b>:被記下的位址已經不在該名稱的 addon 清單裡(掃全索引)⇒ 那扇窗真的收乾淨了。
/// 這條在 AutoGather 可行,是因為所有按下點都由 <c>Framework.Update → DoAutoGather → LegacyTaskManager</c> 驅動,
/// <b>每個 tick 都會再進來一次</b>。</item>
/// <item><b><see cref="IAddonLifecycle"/> 事件</b>:<see cref="AddonEvent.PreFinalize"/>(這一扇正在被銷毀)
/// 與 <see cref="AddonEvent.PostSetup"/>(有新的一扇被建立起來)。
/// 🔴 這條是<b>必要的</b>:同名 addon 關掉再開常常會<b>重用同一塊記憶體位址</b>,只靠第 1 條的話,
/// 重開的那扇會被誤認成「按過的那扇還沒收掉」而白白被擋到逃生口。
/// ⚠️ 刻意<b>不</b>把 <c>PostRefresh</c> 當解除點:它有可能在「關閉中」那幾幀觸發,那會把防線變成沒有。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>逃生口是刻意的</b>(<see cref="DefaultEscapeFrames"/>):萬一某扇窗既不 finalize 也不重新 setup
/// (上一次的 callback 根本沒生效、視窗就是還開著 —— <c>CloseGatheringAddons</c> 的註解就說 Gathering 剛出現時
/// callback 會被忽略),沒有逃生口的話呼叫端會<b>永遠</b>按不下去,等於把崩潰換成靜默失效。
/// 用<b>幀數</b>而不是毫秒:危險窗口的長度本來就是以幀計的,遊戲卡頓時兩者一起拉長。
/// </para>
/// <para>
/// 📌 <b>正常路徑行為零變化</b>:第一次看到某扇窗的某個按法一律當場按下去;
/// 被擋下時回 <see langword="false"/>,對呼叫端的意義一律是「這一輪沒按到,下一輪再來」,
/// 與「addon 還沒出現」走同一條既有路徑。🔴 絕不回 <see langword="null"/>(TaskManager 的 <c>bool?</c> 三態裡 null 是 Abort)。
/// </para>
/// <para>⚠️ 只在主執行緒使用(與呼叫端的 TaskManager 同一個前提)。</para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>
    /// 已經按過、那扇窗卻既沒消失也沒重建時,最多再等這麼多幀才允許補按一次。
    /// </summary>
    /// <remarks>
    /// 🔑 這不是節流 —— 真正的防護是「同一扇窗的同一個按法只按一次」,這個值只是防死鎖的逃生口。
    /// 90 幀(這裡的「幀」是 <b>framework tick</b>:60fps 下約 1.5 秒、30fps 下約 3 秒)遠遠大於「關閉中的那幾幀」,
    /// 補按永遠不會落在危險窗口內。
    /// ⚠️ <b>呼叫端如果是有毫秒逾時的任務,逾時預算要比這個值換算出來的時間長</b> ——
    /// 否則逃生口還沒放行,呼叫端就先逾時(<c>abortOnTimeout</c> 會清掉整條佇列)。
    /// 走到這個逃生口代表「按了卻沒生效」,寫 <c>Information</c>(使用者跑 LogLevel 1,Debug 收得到但單檔數十萬行會淹沒)。
    /// </remarks>
    internal const int DefaultEscapeFrames = 90;

    /// <summary>
    /// 給「按一次翻一頁、窗不會因為被按而消失」的多次互動窗用的短逃生口(15 幀):
    /// Talk 按一次翻一頁、Gathering 同一格每個 integrity 按一次、Materialize/PurifyItemSelector 每輪重送同一組參數。
    /// </summary>
    /// <remarks>
    /// 這類窗整段都不關也不重建,輪詢與生命週期兩條解除點都不會觸發,走逃生口是<b>常態</b>而不是異常 ——
    /// 所以放行與擋下的 log 都寫 Debug 不洗版。關閉中的危險窗口 &lt; 10 幀,15 幀不落在裡面;每頁多等 0.25 秒幾乎無感。
    /// ⚠️ 刻意<b>不</b>用「文字變了」當翻頁證據:關閉中的窗文字會讀壞(U+FFFD)。
    /// (2026-09-02 艦隊政策:Talk 類一律 15 幀。)
    /// </remarks>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>
    /// 關窗按法(<c>Callback.Fire(addon, true, -1)</c>)登記用的按法名。<b>它是萬用鍵</b>:對某扇窗送過關閉之後、
    /// 還沒觀察到它收掉之前,<see cref="TryBeginPress(string, nint, string, int)"/> 對同一位址的<b>任何</b>按法都會被擋;
    /// 反過來,同一位址任何按法還在它自己的逃生口內時,也不准送關閉(那一發本來就可能正在把窗關掉)。
    /// </summary>
    internal const string ClosePressKey = "Close";

    /// <summary>輪詢解除時最多掃到第幾個同名實例。</summary>
    /// <remarks>同名視窗同時開著超過這個數量在實務上不存在;掃到第一個空的就提早停。</remarks>
    private const int MaxAddonIndex = 32;

    /// <summary>
    /// 「一扇窗一生只回答一次」的視窗:這些名字底下的按法一律併成同一個 key。
    /// </summary>
    /// <remarks>
    /// 🔴 這一組是<b>必要的</b>:同一扇 SelectYesno 在 AutoGather 裡會被<b>兩種機制</b>按到 ——
    /// <c>FireOnAddon("SelectYesno", true, 0)</c>(送 callback)與 <c>AddonMaster.SelectYesno.Yes()</c>(送 ReceiveEvent),
    /// 參數字串各不相同,不併 key 就會出現「兩條路徑接力按同一扇關閉中的窗」。
    /// <para>
    /// ⚠️ 只放<b>回答一次就結束</b>的窗。Gathering/Materialize/PurifyItemSelector/ContentsFinderMenu/Repair
    /// 這種「窗一直開著、刻意連送不同 callback」的<b>絕對不能</b>放進來。
    /// <c>SelectString</c> 刻意<b>不</b>在此(巢狀選單常重用同一個實例只換內容),改用 <c>Select|index</c> 這種按法字串。
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "ContentsFinderConfirm",
        "MaterializeDialog",
    };

    /// <param name="Address">被按的那個實例的位址,<b>只做等值比較</b>。</param>
    /// <param name="Frame">按下時的幀號(<see cref="frameCount"/>,每個 framework tick +1;<b>不是</b>繪製幀)。</param>
    /// <param name="EscapeFrames">登記當時呼叫端給的逃生口;判「這筆還熱著」用它。</param>
    private readonly record struct PressRecord(nint Address, long Frame, int EscapeFrames);

    /// <summary>addon 名稱 → (按法 → 上一次按的是哪個實例、在第幾幀)。</summary>
    private static readonly Dictionary<string, Dictionary<string, PressRecord>> PressedByAddon = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers = new(StringComparer.Ordinal);

    /// <summary>守衛自己的幀計數器:每一個 framework tick +1。</summary>
    /// <remarks>
    /// 🔴🔴 <b>刻意不用 <c>UiBuilder.FrameCount</c></b>:那個計數器在<b>外掛 UI 被隱藏時完全停止前進</b> ——
    /// 本 pin 的 <c>UiBuilder.OnDraw()</c> 在①使用者隱藏 UI(<c>ToggleUiHide</c>)②<b>過場動畫</b>
    /// (<c>ToggleUiHideDuringCutscenes</c>,<b>預設開</b>)③GPose 這三種情形下<b>直接 return</b>,
    /// 而 <c>FrameCount++</c> 寫在那個 return <b>之後</b>。
    /// 拿它當時鐘的話,過場或隱藏 UI 期間 <see cref="DefaultEscapeFrames"/> 與 <see cref="RoutineRePressEscapeFrames"/>
    /// 兩個逃生口<b>永遠不會到期</b>,呼叫端會一路被擋到自己的逾時(方向是 fail-closed:不會崩,但會停擺)。
    /// <para>
    /// <c>Framework.Update</c> 掛在遊戲的 update hook 上,和繪製、UI 隱藏都無關,過場中照樣前進 ——
    /// 所以計數器改由它來推。⚠️ 呼叫端如果用<b>毫秒</b>逾時,預算必須大於逃生口換算出來的時間
    /// (90 tick:60fps 約 1.5 秒、30fps 約 3 秒),否則逃生口還沒放行、呼叫端就先逾時了。
    /// </para>
    /// </remarks>
    private static long frameCount;

    /// <summary>是否已經掛上 <see cref="OnFrameworkUpdate"/>(<see cref="frameCount"/> 的唯一來源)。</summary>
    private static bool watchingFramework;

    private static long CurrentFrame
        => frameCount;

    /// <summary>掛上幀計數器(重複呼叫是 no-op)。</summary>
    /// <remarks>
    /// 🔴 這支<b>不能</b>併進 <see cref="EnsureWatching"/>:那支開頭就有「這個名字已經看過就 return」,
    /// 併進去等於計數器只在第一次遇到新 addon 名稱時才推得動(＝又停住了)。
    /// </remarks>
    private static void EnsureFrameClock()
    {
        if (watchingFramework)
            return;

        watchingFramework        =  true;
        Dalamud.Framework.Update += OnFrameworkUpdate;
    }

    private static void OnFrameworkUpdate(IFramework framework)
        => frameCount++;

    /// <inheritdoc cref="TryBeginPress(string, nint, string, int)"/>
    internal static bool TryBeginPress(string addonName, AtkUnitBase* addon, string pressKey = "", int escapeFrames = DefaultEscapeFrames)
        => TryBeginPress(addonName, (nint)addon, pressKey, escapeFrames);

    /// <summary>
    /// 登記「即將對這扇視窗送出這一個按法」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
    /// </summary>
    /// <param name="addonName">視窗名稱(解除封鎖的監聽器與輪詢都以它為準)。</param>
    /// <param name="address">目標實例。<b>只當作識別用的位址,本方法不解參。</b></param>
    /// <param name="pressKey">
    /// 這一次的「按法」(用 <see cref="BuildPressKey"/> 從 callback 參數組出來,或自訂如 <c>Click</c>/<c>RepairAll</c>)。
    /// 同一扇窗上不同的按法互不干擾;要擋的是<b>同一個按法重複送</b>。
    /// 傳空字串代表「整扇窗只有一種按法」;傳 <see cref="ClosePressKey"/> 代表關窗(萬用鍵)。
    /// </param>
    /// <param name="escapeFrames">逃生口幀數:單答終結窗用 <see cref="DefaultEscapeFrames"/>,多次互動窗用 <see cref="RoutineRePressEscapeFrames"/>。</param>
    /// <remarks>
    /// 呼叫點要放在<b>緊接著送出動作之前</b> —— 這支一回 <see langword="true"/> 就已經把「按過了」記下去,
    /// 登記完卻不按的話會白白封鎖到逃生口為止。
    /// </remarks>
    internal static bool TryBeginPress(string addonName, nint address, string pressKey = "", int escapeFrames = DefaultEscapeFrames)
    {
        // 🔴 放在所有 early return 之前:時鐘停住的話兩個逃生口都永遠不會到期。
        EnsureFrameClock();

        if (address == nint.Zero || string.IsNullOrEmpty(addonName))
            return false;

        // 回答一次就結束的窗:不管是哪一條路徑、送的是什麼參數,一律算同一次按。
        if (SingleAnswerAddons.Contains(addonName))
            pressKey = string.Empty;

        var isClose = pressKey == ClosePressKey;
        var routine = escapeFrames <= RoutineRePressEscapeFrames;

        // 先把「那扇窗已經從 addon 清單消失」的紀錄清掉(含其他名字的),下一扇同名窗才會被當成全新的窗處理。
        ReleaseVanished();
        EnsureWatching(addonName);

        var frame = CurrentFrame;

        if (PressedByAddon.TryGetValue(addonName, out var presses))
        {
            if (presses.TryGetValue(pressKey, out var pressed) && pressed.Address == address)
            {
                var waited = frame - pressed.Frame;
                if (waited < escapeFrames)
                {
                    // 🔴 這就是崩潰的那一幀。
                    LogHold(addonName, address, pressKey, routine);
                    return false;
                }

                LogEscape(addonName, address, pressKey, waited, routine);
            }

            if (isClose)
            {
                // 關窗之前:同位址任何按法只要還在它自己的逃生口內就不准關 —— 那一發本來就可能正在把窗關掉,
                // 這時候補一發關閉正好落在危險窗口裡。
                foreach (var (otherKey, other) in presses)
                {
                    if (otherKey == ClosePressKey || other.Address != address)
                        continue;

                    if (frame - other.Frame < other.EscapeFrames)
                    {
                        LogHold(addonName, address, ClosePressKey + "←" + otherKey, routine);
                        return false;
                    }
                }
            }
            else if (IsCloseHot(presses, address, frame))
            {
                // 🔴 關閉是萬用鍵:對這扇窗送過關閉之後、還沒觀察到它收掉之前,任何按法都不准。
                LogHold(addonName, address, ClosePressKey + "→" + pressKey, routine);
                return false;
            }
        }
        else
        {
            presses                   = new Dictionary<string, PressRecord>(StringComparer.Ordinal);
            PressedByAddon[addonName] = presses;
        }

        presses[pressKey] = new PressRecord(address, frame, escapeFrames);

        // ─── 按窗診斷(全艦隊統一格式,用來回答「跨外掛重按是不是真的在發生」)───
        // 🔴 格式逐字統一,15 份各自獨立的 AddonPressGuard 才能互相比對:
        //    [按窗診斷] plugin=<外掛名> addon=<addon名> addr=0x<位址16進位大寫> key=<參數鍵>
        // 🔴 只在「這一幀真的要送出按壓」時寫一行(每幀的檢查與被擋下的那些都不寫);
        //    🔴 不節流(節流會讓「兩個外掛在同一毫秒按同一個位址」這件事看不見);
        //    🔴 只印位址數值,絕不解參。
        // ⚠️ plugin= 用的是**發版鍵名**的小寫 b 拼法(GatherbuddyReborn),與 feed 的 InternalName 一致
        //    —— 不要「修正」成大寫 B,那會讓這份 log 對不上其他工具。
        // 📌 Information 級:使用者跑 LogLevel 1,盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒。
        GatherBuddy.Log.Information($"[按窗診斷] plugin=GatherbuddyReborn addon={addonName} addr=0x{address:X} key={pressKey}");

        return true;
    }

    /// <summary>
    /// 讀窗上的文字來做判定的站,讀到 U+FFFD 就代表視窗記憶體正在變動(多半是關閉中),<b>這一幀不碰</b>。
    /// </summary>
    /// <returns><see langword="true"/> ＝ 文字讀壞了,呼叫端這一幀什麼都不要做。</returns>
    /// <remarks>
    /// 這是崩潰的旁證而不是防護本體(防護是 <see cref="TryBeginPress(string, nint, string, int)"/>):
    /// 實機崩潰前 log 裡的 prompt 就是這種亂碼。寫 Information 讓使用者回報時看得到。
    /// </remarks>
    internal static bool IsTextCorrupt(string addonName, string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf((char)0xFFFD) < 0)
            return false;

        if (EzThrottler.Throttle($"AddonPressGuard-Corrupt-{addonName}", 1000))
            GatherBuddy.Log.Information($"[AddonPressGuard] 「{addonName}」的文字讀到 U+FFFD 亂碼(視窗記憶體正在變動,多半是關閉中),這一幀不碰它。");

        return true;
    }

    /// <summary>
    /// 把 <c>Callback.Fire</c> 的參數組壓成穩定的「按法」字串;唯一參數是 <c>-1</c>(關窗)時回 <see cref="ClosePressKey"/>。
    /// </summary>
    /// <remarks>用不變文化格式化,免得數字在別的地區設定下變成不同的字串(那會讓同一個按法被當成兩種)。</remarks>
    internal static string BuildPressKey(bool updateState, params object[]? values)
    {
        if (values is [int single] && single == -1)
            return ClosePressKey;

        if (values == null || values.Length == 0)
            return updateState ? "T" : "F";

        var sb = new StringBuilder(updateState ? "T" : "F");
        foreach (var value in values)
        {
            sb.Append('|');
            sb.Append(value switch
            {
                null              => "null",
                IFormattable form => form.ToString(null, CultureInfo.InvariantCulture),
                _                 => value.ToString() ?? string.Empty,
            });
        }

        return sb.ToString();
    }

    /// <summary>外掛卸載時硬拆所有監聽器(不留指向本組件的委派)並清掉紀錄。</summary>
    internal static void ForceTeardown()
    {
        foreach (var (addonName, handler) in Watchers)
        {
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup,   addonName, handler);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
        }

        Watchers.Clear();
        PressedByAddon.Clear();

        if (watchingFramework)
        {
            Dalamud.Framework.Update -= OnFrameworkUpdate;
            watchingFramework        =  false;
        }
    }

    /// <summary>同位址的 <see cref="ClosePressKey"/> 紀錄還在它的逃生口內。</summary>
    private static bool IsCloseHot(Dictionary<string, PressRecord> presses, nint address, long frame)
        => presses.TryGetValue(ClosePressKey, out var closed)
         && closed.Address == address
         && frame - closed.Frame < closed.EscapeFrames;

    /// <summary>
    /// 被擋那一幀的診斷:單答終結窗寫 Information(使用者跑 LogLevel 1),多次互動窗被擋是常態寫 Debug;每扇窗 1 秒節流免得洗版。
    /// </summary>
    private static void LogHold(string addonName, nint address, string pressKey, bool routine)
    {
        if (!EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
            return;

        var msg = $"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)按過之後還沒觀察到它收掉,這一幀不再碰它 —— 對關閉中的視窗送 callback 是攔不到的存取違規。";
        if (routine)
            GatherBuddy.Log.Debug(msg);
        else
            GatherBuddy.Log.Information(msg);
    }

    /// <summary>走逃生口的診斷:多次互動窗是常態寫 Debug;單答終結窗走到這裡才是異常,寫 Information。</summary>
    private static void LogEscape(string addonName, nint address, string pressKey, long waited, bool routine)
    {
        if (routine)
        {
            if (EzThrottler.Throttle($"AddonPressGuard-RoutineRelease-{addonName}", 10000))
                GatherBuddy.Log.Debug($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)按下後 {waited} 幀窗還在(多次互動窗的常態),放行下一次。");
        }
        else if (EzThrottler.Throttle($"AddonPressGuard-Release-{addonName}", 10000))
        {
            GatherBuddy.Log.Information($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)按下後 {waited} 幀既沒有被銷毀也沒有重新建立,判定為「上一次按下沒生效」而不是「正在關閉」,解除封鎖讓呼叫端重試。");
        }
    }

    /// <summary>
    /// 清掉「被記下的那個實例已經不在同名 addon 清單裡」的紀錄。
    /// </summary>
    /// <remarks>
    /// 🔴 只做位址等值比較,永遠不解參。
    /// ⚠️ 判準刻意<b>不</b>用「視窗看起來還 ready 嗎」:關閉中的那幾幀三關全過,
    /// 拿那個當「窗不見了」會在最危險的那幾幀把封鎖解除掉,等於沒有這道防線。
    /// </remarks>
    private static void ReleaseVanished()
    {
        if (PressedByAddon.Count == 0)
            return;

        // 先抄一份鍵:字典在迭代途中不能移除。同時存在的紀錄實務上是 0~3 個,這份複製可忽略。
        foreach (var addonName in PressedByAddon.Keys.ToArray())
        {
            if (!PressedByAddon.TryGetValue(addonName, out var presses))
                continue;

            foreach (var pressKey in presses.Keys.ToArray())
            {
                if (!IsStillPresent(addonName, presses[pressKey].Address))
                    presses.Remove(pressKey);
            }

            if (presses.Count == 0)
                PressedByAddon.Remove(addonName);
        }
    }

    /// <summary>掃全索引(1..<see cref="MaxAddonIndex"/>,掃到第一個空的停)找被記下的位址;只看第 1 格會漏掉多窗情境。</summary>
    private static bool IsStillPresent(string addonName, nint address)
    {
        for (var i = 1; i <= MaxAddonIndex; i++)
        {
            var live = Dalamud.GameGui.GetAddonByName(addonName, i).Address;
            if (live == nint.Zero)
                return false;
            if (live == address)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 第一次守護某個 addon 名稱時掛上解除封鎖用的監聽器:只清<b>事件帶來的那個位址</b>的紀錄。
    /// </summary>
    /// <remarks>
    /// 掛上去之後就不再拆(只在 <see cref="ForceTeardown"/> 拆):這兩條監聽器只做一次字典移除,
    /// 成本可忽略,而動態掛/拆比較容易留下懸空的監聽器。
    /// <para>
    /// 🔴🔴 <b>只清該位址,不是把整個名字底下的紀錄一起清掉。</b>
    /// 同名視窗可以同時開好幾扇(SelectYesno 是代表):A 被按過、正在關閉中的那幾幀,
    /// 第二扇 B 被建立起來會發 <see cref="AddonEvent.PostSetup"/> ——
    /// 按名字整包清的話 A 的紀錄會一起不見,下一幀任何按下點解到 A 就查無紀錄而放行,
    /// 對關閉中的 A 送出第二發 = 攔不到的原生存取違規。這與本類宣稱的「粒度=(窗,位址)」也直接矛盾。
    /// </para>
    /// <para>
    /// ⚙️ 不需要「這一幀才登記的不清」那種豁免(某些 repo 有):本 repo <b>沒有任何在 PostSetup 處理常式裡按下</b>
    /// 的路徑 —— 所有按下點都由 <c>Framework.Update → DoAutoGather → LegacyTaskManager</c> 驅動,
    /// 而唯一另一個 <c>Gathering</c> 的 PostSetup/PostRefresh 監聽器(<c>GatheringTracker</c>)只讀 AtkValue、不按任何東西。
    /// 同一幀內也不可能「舊的還在、新的已經建在同一個位址」:位址要被重用得先 finalize,
    /// 而 <see cref="AddonEvent.PreFinalize"/> 早就把紀錄清掉了。
    /// </para>
    /// </remarks>
    private static void EnsureWatching(string addonName)
    {
        if (Watchers.ContainsKey(addonName))
            return;

        IAddonLifecycle.AddonEventDelegate handler = (_, args) =>
        {
            // 🔴 位址只做等值比較,永遠不解參。
            var address = (nint)args.Addon.Address;
            if (address == nint.Zero || !PressedByAddon.TryGetValue(addonName, out var presses))
                return;

            foreach (var key in presses.Where(kv => kv.Value.Address == address).Select(kv => kv.Key).ToArray())
                presses.Remove(key);

            if (presses.Count == 0)
                PressedByAddon.Remove(addonName);
        };

        Watchers[addonName] = handler;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   addonName, handler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
    }
}
