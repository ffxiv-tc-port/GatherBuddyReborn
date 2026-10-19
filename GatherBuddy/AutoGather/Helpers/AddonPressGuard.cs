using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
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
/// 🔴🔴 <b>存在的唯一理由是原生 AccessViolation</b>:<c>SelectYesno</c> 這類確認框被按下之後有「正在關閉中」的幾幀,
/// 這段期間 <c>GetAddonByName</c> 仍回得到實例、<c>IsAddonReady</c> 三關全過;此時再送 callback／<c>ReceiveEvent</c>
/// 就是攔不到的存取違規 —— <b>唯一的防護是「不要送第二次」</b>。<c>DelayNext</c>／節流都不是防護。
/// <para>
/// 粒度＝(視窗位址,按法);位址<b>只做等值比較,永遠不解參</b>。<see cref="SingleAnswerAddons"/> 一律併成同一把 key;
/// <see cref="ClosePressKey"/> 是萬用鍵:還熱著時同位址任何按法都擋,反過來同位址任何按法還熱著時也不准關。
/// 解除點:位址從同名清單消失(每幀輪詢)、同位址 <c>PostSetup</c>(這一幀才登記的不清)、常駐窗連續隱藏 N 幀、逃生口。
/// <c>PreFinalize</c> 是<b>上鎖點</b>,鎖到位址消失或同位址重建。刻意不把 <c>PostRefresh</c> 當解除點(關閉中也可能觸發)。
/// </para>
/// <para>
/// 被擋回 <see langword="false"/> ＝「這一輪沒按到,下一輪再來」,🔴 絕不回 <see langword="null"/>(TaskManager 的 null 是 Abort)。
/// ⚠️ 只在主執行緒使用;任何不確定一律不放行。
/// </para>
/// </remarks>
internal static unsafe class AddonPressGuard
{
    /// <summary>
    /// 終結按法(按下即關)的逃生口:已經按過、那扇窗卻既沒消失也沒重建時,最多再等這麼多幀才允許補按一次。
    /// </summary>
    /// <remarks>
    /// 這不是節流,只是防死鎖的逃生口;幀是守衛自己數的 framework tick(60fps 約 1.5 秒)。
    /// ⚠️ 呼叫端如果是有毫秒逾時的任務,逾時預算要比這個值換算出來的時間長。走到這裡寫 <c>Information</c>。
    /// </remarks>
    internal const int DefaultEscapeFrames = 90;

    /// <summary>
    /// 「按一次翻一頁、窗不會因為被按而消失」的多次互動窗用的短逃生口:
    /// Talk 翻頁、Gathering 同一格每個 integrity、Materialize/PurifyItemSelector 每輪重送。
    /// </summary>
    /// <remarks>走逃生口是常態,log 寫 Debug。關閉中的危險窗口 &lt; 10 幀,15 幀不落在裡面。</remarks>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>
    /// 關窗按法(<c>Callback.Fire(addon, true, -1)</c>)登記用的按法名,<b>萬用鍵</b>(語意見類別說明)。
    /// </summary>
    internal const string ClosePressKey = "Close";

    /// <summary>同名實例最多掃到第幾個(遊戲同名實例上限);掃到第一個空的就提早停。</summary>
    private const int MaxAddonIndex = 256;

    /// <summary>常駐視窗按下後,要連續觀察到「還在清單裡、但已隱藏」這麼多幀才解除;看到可見就歸零重數。</summary>
    private const int HiddenReleaseFrames = 20;

    /// <summary>超過這麼多幀沒再被按過、也沒上鎖的位址紀錄,定期回收(早就過了逃生口,不改變任何判定)。</summary>
    private const int SweepMinAgeFrames = 3600;

    private const int SweepIntervalFrames = 60;

    /// <summary>
    /// 「一扇窗一生只回答一次」的視窗:這些名字底下的按法一律併成同一個 key。
    /// </summary>
    /// <remarks>
    /// 同一扇 SelectYesno 會被 <c>FireOnAddon</c>(callback)與 <c>AddonMaster.Yes()</c>(ReceiveEvent)兩種機制按到,不併 key
    /// 就會兩條路徑接力按同一扇關閉中的窗。⚠️ Gathering/Materialize/PurifyItemSelector/ContentsFinderMenu/Repair
    /// 這種「窗一直開著、刻意連送不同 callback」的絕對不能放進來。
    /// </remarks>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal)
    {
        "SelectYesno",
        "SelectString",
        "ContentsFinderConfirm",
        "MaterializeDialog",
    };

    /// <summary>
    /// 常駐視窗:關閉只是隱藏,位址整場留在清單裡,<see cref="AddonEvent.PreFinalize"/>／<see cref="AddonEvent.PostSetup"/>
    /// 都不會再發生。這些窗改由「連續隱藏 <see cref="HiddenReleaseFrames"/> 幀」解除,而且送出前必須看得見(兩者互為反面)。
    /// </summary>
    /// <remarks>⚠️ 只收實機證實常駐的名字:把會被銷毀的窗誤收進來,等於在它拆除途中提早解除封鎖。</remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextIconMenu",
        "ContextMenu",
        "Talk",
    };

    /// <param name="Frame">按下時的守衛幀號。</param>
    /// <param name="EscapeFrames">登記當時呼叫端給的逃生口;判「這筆還熱著」用它。</param>
    private readonly record struct PressRecord(long Frame, int EscapeFrames);

    private sealed class AddressRecords(string addonName)
    {
        public string AddonName { get; set; } = addonName;
        public Dictionary<string, PressRecord> ByKey { get; } = new(StringComparer.Ordinal);
        public long LastFrame { get; set; }

        /// <summary>連續觀察到「還在清單裡、但已隱藏」的幀數;只有常駐窗會累加,看得見或重新按下就歸零。</summary>
        public int HiddenFrames { get; set; }

        /// <summary>登記時在同名清單裡找得到這個位址;只有它成立才准用「從清單消失」解除。</summary>
        public bool Listed { get; set; }

        /// <summary>看過 <see cref="AddonEvent.PreFinalize"/>:在位址消失或同位址重建之前,任何按法都不准,也沒有逃生口。</summary>
        public bool Finalizing { get; set; }
    }

    /// <summary>這一幀在同名清單裡對某個位址的觀察。零值必須是「不解除」的答案。</summary>
    private enum AddonPresence
    {
        /// <summary>查詢不成立(名稱空、位址 0、同名實例超過掃描上限)。零值。</summary>
        Unknown = 0,

        /// <summary>掃完同名清單都沒有這個位址＝那扇窗已經收乾淨。</summary>
        NotListed = 1,

        Visible = 2,
        Hidden  = 3,
    }

    /// <summary>位址 → 這個實例被按過的按法。位址只當字典鍵,從不解參考。</summary>
    private static readonly Dictionary<nint, AddressRecords> Pressed = [];

    /// <summary>曾經守護過的窗名:這些名字的實例一進 <see cref="AddonEvent.PreFinalize"/> 就上鎖(即使還沒按過)。</summary>
    private static readonly HashSet<string> GuardedNames = new(StringComparer.Ordinal);

    private static readonly List<nint>   ReleaseBuf   = [];
    private static readonly List<string> StaleKeysBuf = [];

    /// <summary>「這個窗名第一次走隱藏解除」只寫一行 Information。</summary>
    private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

    /// <summary>0＝還沒掛監聽器。用 <see cref="Interlocked"/>:重複訂閱會讓計數器一個 tick 前進 2,逃生口對半砍。</summary>
    private static int watching;

    /// <summary>守衛自己的幀計數器:每一個 framework tick +1。</summary>
    /// <remarks>
    /// 🔴 刻意不用 <c>UiBuilder.FrameCount</c>:它在隱藏 UI／過場動畫／GPose 期間完全停止前進,逃生口會永遠不到期。
    /// </remarks>
    private static long frameCount;

    private static IAddonLifecycle.AddonEventDelegate? lifecycleHandler;

    /// <summary>掛上幀計數器與解除封鎖用的全域監聽器(重複呼叫是 no-op)。</summary>
    internal static void EnsureWatching()
    {
        if (Interlocked.CompareExchange(ref watching, 1, 0) != 0)
            return;

        Dalamud.Framework.Update += OnFrameworkUpdate;
        lifecycleHandler         =  OnAddonLifecycle;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,   lifecycleHandler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, lifecycleHandler);
    }

    /// <summary>外掛卸載時硬拆所有監聽器(不留指向本組件的委派)並清掉紀錄。</summary>
    internal static void ForceTeardown()
    {
        if (Interlocked.Exchange(ref watching, 0) == 1)
        {
            Dalamud.Framework.Update -= OnFrameworkUpdate;
            if (lifecycleHandler != null)
            {
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup,   lifecycleHandler);
                Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, lifecycleHandler);
            }
        }

        lifecycleHandler = null;
        Pressed.Clear();
        GuardedNames.Clear();
    }

    /// <inheritdoc cref="TryBeginPress(string, nint, string, int)"/>
    internal static bool TryBeginPress(string addonName, AtkUnitBase* addon, string pressKey = "", int escapeFrames = DefaultEscapeFrames)
        => TryBeginPress(addonName, (nint)addon, pressKey, escapeFrames);

    /// <summary>
    /// 登記「即將對這扇視窗送出這一個按法」。<b>回 <see langword="false"/> ＝這一幀絕對不能送。</b>
    /// </summary>
    /// <param name="addonName">視窗名稱(輪詢解除以它為準);空的一律不放行。</param>
    /// <param name="address">目標實例。<b>只當作識別用的位址,本方法不解參。</b></param>
    /// <param name="pressKey">
    /// 這一次的「按法」(<see cref="BuildPressKey"/> 組出來,或自訂如 <c>Click</c>/<c>RepairAll</c>);
    /// 傳空字串代表「整扇窗只有一種按法」;傳 <see cref="ClosePressKey"/> 代表關窗(萬用鍵)。
    /// </param>
    /// <param name="escapeFrames">逃生口幀數:終結窗用 <see cref="DefaultEscapeFrames"/>,多次互動窗用 <see cref="RoutineRePressEscapeFrames"/>。</param>
    /// <remarks>呼叫點要放在<b>緊接著送出動作之前</b>:一回 <see langword="true"/> 就已經記下「按過了」。</remarks>
    internal static bool TryBeginPress(string addonName, nint address, string pressKey = "", int escapeFrames = DefaultEscapeFrames)
    {
        // 🔴 放在所有 early return 之前:時鐘停住的話兩個逃生口都永遠不會到期。
        EnsureWatching();

        if (address == nint.Zero || string.IsNullOrEmpty(addonName))
            return false;

        if (SingleAnswerAddons.Contains(addonName))
            pressKey = string.Empty;
        pressKey ??= string.Empty;

        var isClose = pressKey == ClosePressKey;
        var routine = escapeFrames <= RoutineRePressEscapeFrames;
        var frame   = frameCount;

        // 常駐窗的解除條件是「連續隱藏」,這裡是它的反面:隱藏中(或這一幀查不到)的常駐窗一律不送。
        if (PersistentAddons.Contains(addonName) && LookUp(addonName, address, readVisibility: true) != AddonPresence.Visible)
        {
            LogHold(addonName, address, pressKey, routine, "是常駐窗,這一幀不可見(或不在同名清單裡)");
            return false;
        }

        GuardedNames.Add(addonName);
        Pressed.TryGetValue(address, out var records);
        if (records != null)
        {
            if (records.Finalizing)
            {
                LogHold(addonName, address, pressKey, false, "已經看到它 PreFinalize、還沒看到它消失");
                return false;
            }

            if (records.ByKey.TryGetValue(pressKey, out var pressed))
            {
                var waited = frame - pressed.Frame;
                if (waited < escapeFrames)
                {
                    LogHold(addonName, address, pressKey, routine, "按過之後還沒觀察到它收掉");
                    return false;
                }

                LogEscape(addonName, address, pressKey, waited, routine);
            }

            if (isClose)
            {
                // 同位址任何按法還在它自己的逃生口內就不准關:那一發本來就可能正在把窗關掉。
                foreach (var (otherKey, other) in records.ByKey)
                {
                    if (otherKey == ClosePressKey || frame - other.Frame >= other.EscapeFrames)
                        continue;

                    LogHold(addonName, address, ClosePressKey + "←" + otherKey, routine, "還有按法熱著,不准關");
                    return false;
                }
            }
            else if (IsCloseHot(records, frame))
            {
                LogHold(addonName, address, ClosePressKey + "→" + pressKey, routine, "送過關閉之後還沒觀察到它收掉");
                return false;
            }
        }
        else
        {
            records           = new AddressRecords(addonName);
            Pressed[address] = records;
        }

        if (!records.Listed && LookUp(addonName, address, readVisibility: false) == AddonPresence.Visible)
        {
            records.AddonName = addonName;
            records.Listed    = true;
        }

        records.ByKey[pressKey] = new PressRecord(frame, escapeFrames);
        records.LastFrame       = frame;
        // 🔴 承重:隱藏解除是「從最後一次按下起算」連續隱藏幾幀;漏掉這一行,前一次累到一半的幀數會被沿用。
        records.HiddenFrames = 0;

        // 跨外掛重按診斷:全艦隊逐字統一這一行;不節流、只印位址數值。
        // ⚠️ plugin= 是發版鍵名的小寫 b 拼法(與 feed 的 InternalName 一致),不要「修正」成大寫 B。
        GatherBuddy.Log.Verbose($"[按窗診斷] plugin=GatherbuddyReborn addon={addonName} addr=0x{address:X} key={pressKey}");
        return true;
    }

    /// <summary>
    /// <paramref name="addonName"/> 是常駐窗,而 <paramref name="address"/> 這一幀不可見(或查不到)＝那扇窗已經收起來。
    /// </summary>
    /// <remarks>給「跑到時窗不在就跳過」的呼叫端用:常駐窗收起後位址仍非 0,只判非 0 會對隱藏的窗送事件。</remarks>
    internal static bool IsPersistentHidden(string addonName, nint address)
        => PersistentAddons.Contains(addonName) && LookUp(addonName, address, readVisibility: true) != AddonPresence.Visible;

    /// <summary>
    /// 讀窗上的文字來做判定的站,讀到 U+FFFD 就代表視窗記憶體正在變動(多半是關閉中),<b>這一幀不碰</b>。
    /// </summary>
    /// <returns><see langword="true"/> ＝ 文字讀壞了,呼叫端這一幀什麼都不要做。</returns>
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
    /// <remarks>用不變文化格式化,免得同一個按法在別的地區設定下變成兩種字串。</remarks>
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

    /// <summary>同位址的 <see cref="ClosePressKey"/> 紀錄還在它的逃生口內。</summary>
    private static bool IsCloseHot(AddressRecords records, long frame)
        => records.ByKey.TryGetValue(ClosePressKey, out var closed)
         && frame - closed.Frame < closed.EscapeFrames;

    /// <summary>被擋那一幀的診斷:終結窗寫 Information,多次互動窗被擋是常態寫 Debug;每扇窗 1 秒節流。</summary>
    private static void LogHold(string addonName, nint address, string pressKey, bool routine, string reason)
    {
        if (!EzThrottler.Throttle($"AddonPressGuard-Hold-{addonName}", 1000))
            return;

        var msg = $"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」){reason},這一幀不再碰它 —— 對關閉中的視窗送 callback 是攔不到的存取違規。";
        if (routine)
            GatherBuddy.Log.Debug(msg);
        else
            GatherBuddy.Log.Information(msg);
    }

    /// <summary>走逃生口的診斷:多次互動窗是常態寫 Debug;終結窗走到這裡才是異常,寫 Information。</summary>
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

    /// <summary>🔴 遞增必須是第一行、前面不可以有任何條件:一旦時鐘會停,逃生口就跟著失準。</summary>
    private static void OnFrameworkUpdate(IFramework framework)
    {
        frameCount++;
        PollRecords();
        if (frameCount % SweepIntervalFrames == 0)
            SweepColdRecords();
    }

    /// <summary>
    /// 每幀的解除路徑:①登記時找得到、現在從同名清單消失 ⇒ 解除(含 PreFinalize 鎖);
    /// ②常駐窗連續隱藏 <see cref="HiddenReleaseFrames"/> 幀 ⇒ 解除。
    /// </summary>
    /// <remarks>判準刻意不用「還可不可見」(常駐窗例外):拆除途中那幾幀三關全過。只有「找到了而且看不見」才累加隱藏幀。</remarks>
    private static void PollRecords()
    {
        if (Pressed.Count == 0)
            return;

        ReleaseBuf.Clear();
        foreach (var (address, records) in Pressed)
        {
            var persistent = PersistentAddons.Contains(records.AddonName);
            var presence   = LookUp(records.AddonName, address, readVisibility: persistent);
            var present    = presence != AddonPresence.NotListed;
            if (!present)
            {
                if (records.Listed)
                    ReleaseBuf.Add(address);
                else
                    records.HiddenFrames = 0;
                continue;
            }

            if (!persistent || records.Finalizing)
                continue;

            if (presence != AddonPresence.Hidden)
            {
                records.HiddenFrames = 0;
                continue;
            }

            if (++records.HiddenFrames < HiddenReleaseFrames)
                continue;

            ReleaseBuf.Add(address);
            if (HiddenReleaseReported.Add(records.AddonName))
                GatherBuddy.Log.Information($"[AddonPressGuard] 「{records.AddonName}」是常駐視窗(隱藏而不銷毀),按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個視窗名每次遊戲只寫一次。");
        }

        foreach (var address in ReleaseBuf)
            Pressed.Remove(address);
        ReleaseBuf.Clear();
    }

    /// <summary>在同名的所有實例裡找 <paramref name="address"/>。</summary>
    /// <remarks>
    /// 位址只做等值比較;解參考的是 <c>GetAddonByName</c> 這一幀交回來的指標,而且只在 <paramref name="readVisibility"/> 時才讀。
    /// 不讀可見性時,找到一律回 <see cref="AddonPresence.Visible"/>。
    /// </remarks>
    private static AddonPresence LookUp(string addonName, nint address, bool readVisibility)
    {
        if (string.IsNullOrEmpty(addonName) || address == nint.Zero)
            return AddonPresence.Unknown;

        for (var index = 1; index <= MaxAddonIndex; index++)
        {
            var unit = (AtkUnitBase*)Dalamud.GameGui.GetAddonByName(addonName, index).Address;
            if (unit == null)
                return AddonPresence.NotListed;
            if ((nint)unit != address)
                continue;

            if (!readVisibility)
                return AddonPresence.Visible;

            return unit->IsVisible ? AddonPresence.Visible : AddonPresence.Hidden;
        }

        return AddonPresence.Unknown;
    }

    /// <summary>回收「已經不可能再擋住任何人」的槽位(不改變任何一次「按／不按」的判定)。</summary>
    private static void SweepColdRecords()
    {
        if (Pressed.Count == 0)
            return;

        ReleaseBuf.Clear();
        foreach (var (address, records) in Pressed)
        {
            if (records.Finalizing || frameCount - records.LastFrame < SweepMinAgeFrames)
                continue;

            ReleaseBuf.Add(address);
        }

        foreach (var address in ReleaseBuf)
            Pressed.Remove(address);
        ReleaseBuf.Clear();
    }

    /// <summary>該位址進入銷毀(上鎖)或重新開始(解除)生命週期。</summary>
    /// <remarks>
    /// 🔴 按位址比對,不是按名稱清空:同名的兩扇窗可以並存(第一扇正在關閉、第二扇剛建好)。
    /// <see cref="AddonEvent.PostSetup"/> 只清「不是這一幀才登記的」紀錄;<see cref="AddonEvent.PreFinalize"/> 把位址鎖到它從清單消失。
    /// </remarks>
    private static void OnAddonLifecycle(AddonEvent type, AddonArgs args)
    {
        var address = (nint)args.Addon.Address;
        if (address == nint.Zero)
            return;

        if (type == AddonEvent.PreFinalize)
        {
            LockFinalizing(address, args);
            return;
        }

        if (!Pressed.TryGetValue(address, out var records))
            return;

        records.Finalizing = false;
        StaleKeysBuf.Clear();
        foreach (var (pressKey, record) in records.ByKey)
        {
            if (record.Frame == frameCount)
                continue;

            StaleKeysBuf.Add(pressKey);
        }

        foreach (var pressKey in StaleKeysBuf)
            records.ByKey.Remove(pressKey);
        StaleKeysBuf.Clear();
        if (records.ByKey.Count == 0)
            Pressed.Remove(address);
    }

    private static void LockFinalizing(nint address, AddonArgs args)
    {
        if (Pressed.TryGetValue(address, out var records))
        {
            records.Finalizing = true;
            return;
        }

        if (GuardedNames.Count == 0)
            return;

        var name = args.AddonName;
        if (!GuardedNames.Contains(name))
            return;

        Pressed[address] = new AddressRecords(name) { Listed = true, Finalizing = true, LastFrame = frameCount };
    }
}
