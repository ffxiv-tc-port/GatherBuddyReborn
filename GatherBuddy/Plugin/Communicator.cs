using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using GatherBuddy.Alarms;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.Structs;
using GatherBuddy.Time;

namespace GatherBuddy.Plugin;

internal static class SeStringBuilderExtension
{
    public static SeStringBuilder AddColoredText(this SeStringBuilder builder, string text, int colorId)
        => builder.AddUiForeground((ushort)colorId)
            .AddText(text)
            .AddUiForegroundOff();

    public static SeStringBuilder AddFullMapLink(this SeStringBuilder builder, string name, Territory territory, float xCoord, float yCoord,
        bool openMapLink = false, bool withCoordinates = true, float fudgeFactor = 0.05f)
    {
        var mapPayload = new MapLinkPayload(territory.Id, territory.Data.Map.RowId, xCoord, yCoord, fudgeFactor);
        if (openMapLink)
            Dalamud.GameGui.OpenMapWithMapLink(mapPayload);
        if (withCoordinates)
            name = $"{name} ({xCoord.ToString("00.0", CultureInfo.InvariantCulture)}, {yCoord.ToString("00.0", CultureInfo.InvariantCulture)})";
        return builder.AddUiForeground(0x0225)
            .AddUiGlow(0x0226)
            .Add(mapPayload)
            .AddUiForeground(500)
            .AddUiGlow(501)
            .AddText($"{(char)SeIconChar.LinkMarker}")
            .AddUiGlowOff()
            .AddUiForegroundOff()
            .AddText(name)
            .Add(RawPayload.LinkTerminator)
            .AddUiGlowOff()
            .AddUiForegroundOff();
    }

    public static SeStringBuilder AddFullItemLink(this SeStringBuilder builder, uint itemId, string itemName)
        => builder.AddItemLink(itemId, false, itemName);

    public static SeStringBuilder DelayString(this SeStringBuilder builder, TimeInterval uptime)
    {
        if (uptime.Start > GatherBuddy.Time.ServerTime)
            return builder.AddText("will be up in ")
                .AddColoredText(TimeInterval.DurationString(uptime.Start, GatherBuddy.Time.ServerTime, false),
                    GatherBuddy.Config.SeColorArguments);

        return builder.AddText("will be up for the next ")
            .AddColoredText(TimeInterval.DurationString(uptime.End, GatherBuddy.Time.ServerTime, false), GatherBuddy.Config.SeColorArguments);
    }
}

public static class Communicator
{
    public delegate SeStringBuilder ReplacePlaceholder(SeStringBuilder builder, string placeholder);

    /// <summary>還沒送出的聊天訊息。<b>順序就是呼叫順序。</b></summary>
    /// <remarks>
    /// 🔴🔴 <b>為什麼要排到 framework 執行緒才送出。</b>本 pin 的 Dalamud
    /// <c>ChatGui.Print(XivChatEntry)</c> 只是把項目 <c>Enqueue</c> 進一個<b>沒有任何同步</b>的
    /// <c>Queue&lt;XivChatEntry&gt;</c>(<c>Dalamud/Game/Gui/ChatGui.cs:43</c>),而 <c>UpdateQueue</c>
    /// 在 framework 執行緒上 <c>TryDequeue</c>。從別的執行緒呼叫 ⇒ 與 framework 執行緒並行改同一個
    /// <c>Queue</c>,<b>失敗形式不是「訊息晚一點出現」而是那個佇列本身壞掉</b>。
    /// <para>
    /// 🔴 這個外掛確實有在非 framework 執行緒上印聊天的路徑(逐一開檔確認過):
    /// <c>GatherBuddy.CheckForOGGB</c>(偵測到舊版 GatherBuddy 時的那則警告)與
    /// <c>AutoGatherListsManager.Load</c>(清單載入失敗那則)都在<b>外掛建構子</b>裡,
    /// 而外掛建構子跑在 Dalamud 的 LongRunning 專用執行緒上;
    /// <c>Reflection.ImportArtisanList</c> 的「匯入成功」在 <c>Task.Run</c> 起的執行緒池工作上;
    /// IPC 端點 <c>GatherBuddyReborn.SetAutoGatherEnabled</c> 跑在<b>呼叫端</b>的執行緒上,
    /// 它會走進 <c>AutoGather.Enabled</c> 的 setter,那條路上的每一則訊息也一樣。
    /// </para>
    /// <para>
    /// 📌 <c>IFramework.RunOnFrameworkThread(Action)</c> 在<b>已經是</b> framework 執行緒時就地同步
    /// 執行(<c>Dalamud/Game/Framework.cs</c>),所以聊天指令、ImGui 回呼與 <c>DoAutoGather</c> 這些
    /// 路徑一個位元都沒變 —— 訊息仍然在同一格、依同一個順序出現。
    /// </para>
    /// <para>
    /// 🔑 <b>為什麼還要自己排一個佇列</b>:Dalamud 的 <c>ThreadBoundTaskScheduler</c> 用
    /// <c>ConcurrentDictionary</c> 存待跑的工作、<c>Run()</c> 走訪它的 <c>Keys</c>
    /// ⇒ <b>不保證先進先出</b>。把每一次 <c>Print</c> 各自包成一個排程工作的話,同一格內送出的兩則
    /// 訊息順序會變成隨機的;自己排隊、到了 framework 執行緒一次排乾,順序就與呼叫順序逐字相同。
    /// </para>
    /// </remarks>
    private static readonly ConcurrentQueue<XivChatEntry> PendingChat = new();

    public static void Print(SeString message)
    {
        var entry = new XivChatEntry()
        {
            Message = message,
            Name    = SeString.Empty,
            Type    = GatherBuddy.Config.ChatTypeMessage,
        };
        QueueForFramework(entry);
    }

    public static void PrintError(SeString message)
    {
        var entry = new XivChatEntry()
        {
            Message = message,
            Name    = SeString.Empty,
            Type    = GatherBuddy.Config.ChatTypeError,
        };
        QueueForFramework(entry);
    }

    /// <summary>把一則<b>已經組好</b>的訊息排進佇列,並要求在 framework 執行緒上排乾。</summary>
    /// <remarks>
    /// 📌 訊息內容與聊天頻道(<c>GatherBuddy.Config.ChatTypeMessage</c> / <c>ChatTypeError</c>)
    /// <b>在呼叫端的執行緒上就組好了</b>,排隊的只是「送出」這個動作 ⇒ 使用者看到的字一個都沒變。<br/>
    /// 🔴 <b>刻意不等它跑完</b>:全部呼叫點都是「印一行就繼續做事」,沒有任何一處需要印完才能往下走;
    /// 同步等待只會在 framework 執行緒以外的地方多一個阻塞點。
    /// </remarks>
    private static void QueueForFramework(XivChatEntry entry)
    {
        // 卸載窗內轉派會就地在呼叫端執行緒跑，那等於直接動 Dalamud 的裸聊天佇列。
        if (FrameworkUnloadGuard.ShouldSkip("聊天輸出"))
            return;

        PendingChat.Enqueue(entry);
        _ = Dalamud.Framework.RunOnFrameworkThread(static () =>
        {
            while (PendingChat.TryDequeue(out var pending))
                Dalamud.Chat.Print(pending);
        });
    }

    public static void Print(string message)
        => Print((SeString)message);

    public static void PrintError(string message)
        => PrintError((SeString)message);

    public static void Print(string left, string center, int color, string right)
    {
        SeStringBuilder builder = new();
        builder.AddText(left).AddColoredText(center, color).AddText(right);
        Print(builder.BuiltString);
    }

    public static void PrintError(string left, string center, int color, string right)
    {
        SeStringBuilder builder = new();
        builder.AddText(left).AddColoredText(center, color).AddText(right);
        PrintError(builder.BuiltString);
    }

    public static void PrintClipboardMessage(string objectType, string name, Exception? e = null)
    {
        if (e != null)
        {
            name = name.Length > 0 ? name : "<Unnamed>";
            GatherBuddy.Log.Error($"Could not save {objectType}{name} to Clipboard:\n{e}");
            PrintError($"Could not save {objectType}", name, GatherBuddy.Config.SeColorNames, " to Clipboard.");
        }
        else if (GatherBuddy.Config.PrintClipboardMessages)
        {
            Print(objectType, name.Length > 0 ? name : "<Unnamed>", GatherBuddy.Config.SeColorNames, " saved to Clipboard.");
        }
    }

    public static void PrintUptime(TimeInterval uptime)
    {
        if (!GatherBuddy.Config.PrintUptime
         || uptime.Equals(TimeInterval.Always)
         || uptime.Equals(TimeInterval.Invalid)
         || uptime.Equals(TimeInterval.Never))
            return;

        if (uptime.Start > GatherBuddy.Time.ServerTime)
            Print("Next up in ",                     TimeInterval.DurationString(uptime.Start, GatherBuddy.Time.ServerTime, false),
                GatherBuddy.Config.SeColorArguments, ".");
        else
            Print("Currently up for the next ",      TimeInterval.DurationString(uptime.End, GatherBuddy.Time.ServerTime, false),
                GatherBuddy.Config.SeColorArguments, ".");
    }

    public static void PrintCoordinates(SeString link)
    {
        if (GatherBuddy.Config.WriteCoordinates)
            Print(link);
    }


    // Split a format string with '{text}' placeholders into a SeString with Payloads, 
    // and replace all placeholders by the returned payloads.
    private static SeString Format(string format, ReplacePlaceholder func)
    {
        SeStringBuilder builder     = new();
        var             lastPayload = 0;
        var             openBracket = -1;
        for (var i = 0; i < format.Length; ++i)
        {
            if (format[i] == '{')
            {
                openBracket = i;
            }
            else if (openBracket != -1 && format[i] == '}')
            {
                builder.AddText(format.Substring(lastPayload,   openBracket - lastPayload));
                var placeholder = format.Substring(openBracket, i - openBracket + 1);
                Debug.Assert(placeholder.StartsWith('{') && placeholder.EndsWith('}'));
                func(builder, placeholder);
                lastPayload = i + 1;
                openBracket = -1;
            }
        }

        if (lastPayload != format.Length)
            builder.AddText(format[lastPayload..]);
        return builder.BuiltString;
    }


    public static void PrintIdentifiedItem(string name, IGatherable? item)
    {
        if (item == null)
        {
            Print("Could not find item corresponding to \"", name, GatherBuddy.Config.SeColorNames, "\".");
            GatherBuddy.Log.Verbose($"Could not find item corresponding to \"{name}\".");
            return;
        }

        if (GatherBuddy.Config.IdentifiedGatherableFormat.Length > 0)
            Print(FormatIdentifiedItemMessage(GatherBuddy.Config.IdentifiedGatherableFormat, name, item));
        GatherBuddy.Log.Verbose(Configuration.DefaultIdentifiedGatherableFormat, item.ItemId, item.Name[ClientLanguage.English], name);
    }

    public static void PrintAlarmMessage(Alarm alarm, ILocation location, TimeInterval uptime)
    {
        if (GatherBuddy.Config.AlarmFormat.Length > 0)
            Print(FormatAlarmMessage(GatherBuddy.Config.AlarmFormat, alarm, location, uptime));
        GatherBuddy.Log.Verbose(Configuration.DefaultAlarmFormat, alarm.Name, alarm.Item.Name[ClientLanguage.English], string.Empty,
            location.Name); // Duration string too ugly.
    }


    public static void LocationNotFound(IGatherable? item, GatheringType? type)
    {
        SeStringBuilder sb = new();
        sb.AddText("No associated location or attuned aetheryte found for ");
        if (item != null)
            sb.AddFullItemLink(item.ItemId, item.Name[GatherBuddy.Language]);
        else
            sb.AddColoredText("Unknown", GatherBuddy.Config.SeColorNames);

        if (type != null)
            sb.AddText(" with condition ")
                .AddColoredText(type.Value.ToString(), GatherBuddy.Config.SeColorArguments);
        sb.AddText(".");
        Print(sb.BuiltString);
        GatherBuddy.Log.Verbose(sb.BuiltString.TextValue);
    }

    public static void NoItemName(string command, string itemType)
    {
        PrintError(new SeStringBuilder().AddText($"Please supply a (partial) {itemType} name, ")
            .AddColoredText("alarm", GatherBuddy.Config.SeColorArguments)
            .AddText(" or ")
            .AddColoredText("next", GatherBuddy.Config.SeColorArguments)
            .AddText(" for ")
            .AddColoredText(command, GatherBuddy.Config.SeColorCommands)
            .AddText(".").BuiltString);
    }

    public static void NoBaitFound(Bait bait)
    {
        PrintError(new SeStringBuilder().AddText("Bait ")
            .AddFullItemLink(bait.Id, bait.Name)
            .AddText(" could not be equipped because you do not carry it.").BuiltString);
    }

    public static void NoGatherGroup(string groupName)
        => PrintError("The gather group ", groupName, GatherBuddy.Config.SeColorNames, " does not exist.");

    public static void NoGatherGroupItem(string groupName, int minute)
    {
        SeStringBuilder sb = new();
        sb.AddText("The gather group ")
            .AddColoredText(groupName, GatherBuddy.Config.SeColorNames)
            .AddText(" has no item corresponding to the eorzea time ")
            .AddColoredText($"{minute / RealTime.MinutesPerHour:D2}:{minute % RealTime.MinutesPerHour:D2}",
                GatherBuddy.Config.SeColorArguments)
            .AddText(".");
        PrintError(sb.BuiltString);
    }

    private static SeString FormatIdentifiedItemMessage(string format, string input, IGatherable item)
    {
        SeStringBuilder Replace(SeStringBuilder builder, string s)
            => s.ToLowerInvariant() switch
            {
                "{item}"  => builder.AddFullItemLink(item.ItemId, item.Name[GatherBuddy.Language]),
                "{input}" => builder.AddColoredText(input, GatherBuddy.Config.SeColorArguments),
                _         => builder.AddText(s),
            };

        return Format(format, Replace);
    }


    private static SeString FormatAlarmMessage(string format, Alarm alarm, ILocation location, TimeInterval uptime)
    {
        SeStringBuilder NodeReplace(SeStringBuilder builder, string s)
            => s.ToLowerInvariant() switch
            {
                "{alarm}"       => builder.AddColoredText(alarm.Name.Any() ? $"[{alarm.Name}]" : "[Alarm]", GatherBuddy.Config.SeColorNames),
                "{item}"        => builder.AddFullItemLink(alarm.Item.ItemId, alarm.Item.Name[GatherBuddy.Language]),
                "{offset}"      => builder.AddText(alarm.SecondOffset.ToString()),
                "{delaystring}" => builder.DelayString(uptime),
                "{location}" => builder.AddFullMapLink(location.Name, location.Territory, location.IntegralXCoord / 100f,
                    location.IntegralYCoord / 100f),
                _ => builder.AddText(s),
            };

        var msg = Format(format, NodeReplace);
        msg.Payloads.Insert(0, new UIForegroundPayload((ushort)GatherBuddy.Config.SeColorAlarm));
        msg.Payloads.Add(UIForegroundPayload.UIForegroundOff);
        return msg;
    }
}
