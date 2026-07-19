using System;
using System.Numerics;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.STD;
using GatherBuddy.Alarms;
using GatherBuddy.AutoGather;
using GatherBuddy.Config;
using ImGuiNET;
using OtterGui;
using OtterGui.Widgets;
using FishRecord = GatherBuddy.FishTimer.FishRecord;
using GatheringType = GatherBuddy.Enums.GatheringType;
using ImRaii = OtterGui.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private static class ConfigFunctions
    {
        public static Interface _base = null!;

        public static void DrawSetInput(string jobName, string oldName, Action<string> setName)
        {
            var tmp = oldName;
            ImGui.SetNextItemWidth(SetInputWidth);
            if (ImGui.InputText($"{jobName} 套裝", ref tmp, 15) && tmp != oldName)
            {
                setName(tmp);
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip($"設定你的 {jobName} 套裝名稱，也可以改用數字編號代替。");
        }

        private static void DrawCheckbox(string label, string description, bool oldValue, Action<bool> setter)
        {
            if (ImGuiUtil.Checkbox(label, description, oldValue, setter))
                GatherBuddy.Config.Save();
        }

        private static void DrawChatTypeSelector(string label, string description, XivChatType currentValue, Action<XivChatType> setter)
        {
            ImGui.SetNextItemWidth(SetInputWidth);
            if (Widget.DrawChatTypeSelector(label, description, currentValue, setter))
                GatherBuddy.Config.Save();
        }

        // Auto-Gather Config
        public static void DrawAutoGatherBox()
            => DrawCheckbox("啟用採集視窗互動（停用此選項不受支援）",
                "切換是否自動採集物品。（若要使用「僅導航模式」請停用此選項）",
                GatherBuddy.Config.AutoGatherConfig.DoGathering, b => GatherBuddy.Config.AutoGatherConfig.DoGathering = b);

        public static void DrawGoHomeBox()
        {
            DrawCheckbox("完成後回家",                       "使用「/li auto」指令在採集完成後帶你回家",
                GatherBuddy.Config.AutoGatherConfig.GoHomeWhenDone, b => GatherBuddy.Config.AutoGatherConfig.GoHomeWhenDone = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("Lifestream")]);
            DrawCheckbox("閒置時回家",                       "使用「/li auto」指令在等待限時採集點時帶你回家",
                GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle, b => GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("Lifestream")]);
        }

        public static void DrawUseSkillsForFallabckBox()
            => DrawCheckbox("對備用清單使用技能", "從備用預設中採集物品時使用技能",
                GatherBuddy.Config.AutoGatherConfig.UseSkillsForFallbackItems,
                b => GatherBuddy.Config.AutoGatherConfig.UseSkillsForFallbackItems = b);

        public static void DrawAbandonNodesBox()
            => DrawCheckbox("放棄沒有所需物品的採集點",
                "當已採集足夠數量的物品時，停止採集並放棄該採集點，\n"
              + "或該採集點一開始就沒有任何所需物品時也一樣。",
                GatherBuddy.Config.AutoGatherConfig.AbandonNodes, b => GatherBuddy.Config.AutoGatherConfig.AbandonNodes = b);

        public static void DrawCheckRetainersBox()
        {
            DrawCheckbox("檢查雇員物品欄", "計算庫存時使用 Allagan Tools 檢查雇員物品欄",
                GatherBuddy.Config.AutoGatherConfig.CheckRetainers, b => GatherBuddy.Config.AutoGatherConfig.CheckRetainers = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("InventoryTools", "Allagan Tools")]);
        }

        public static void DrawHonkVolumeSlider()
        {
            ImGui.SetNextItemWidth(150);
            var volume = GatherBuddy.Config.AutoGatherConfig.SoundPlaybackVolume;
            if (ImGui.DragInt("播放音量", ref volume, 1, 0, 100))
            {
                if (volume < 0)
                    volume = 0;
                else if (volume > 100)
                    volume = 100;
                GatherBuddy.Config.AutoGatherConfig.SoundPlaybackVolume = volume;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip(
                "當自動採集因清單完成而停止時所播放音效的音量。\n按住 Ctrl 並點擊可輸入自訂數值");
        }

        public static void DrawHonkModeBox()
            => DrawCheckbox("採集完成時播放音效", "當自動採集因清單完成而停止時播放音效",
                GatherBuddy.Config.AutoGatherConfig.HonkMode,   b => GatherBuddy.Config.AutoGatherConfig.HonkMode = b);

        public static void DrawRepairBox()
            => DrawCheckbox("需要時修理裝備",        "裝備即將損壞時進行修理",
                GatherBuddy.Config.AutoGatherConfig.DoRepair, b => GatherBuddy.Config.AutoGatherConfig.DoRepair = b);

        public static void DrawRepairThreshold()
        {
            var tmp = GatherBuddy.Config.AutoGatherConfig.RepairThreshold;
            if (ImGui.DragInt("修理閾值", ref tmp, 1, 1, 100))
            {
                GatherBuddy.Config.AutoGatherConfig.RepairThreshold = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("裝備耐久度低於此百分比時將進行修理。");
        }

        public static void DrawFishingSpotMinutes()
        {
            var tmp = GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes;
            if (ImGui.DragInt("最長釣點停留分鐘數", ref tmp, 1, 1, 40))
            {
                GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("在一個釣點最多停留釣魚的分鐘數。");
        }

        public static void DrawAutoretainerBox()
        {
            DrawCheckbox("等待 AutoRetainer 多重模式", "當 AutoRetainer 在多重模式下有雇員需要處理時自動暫停 GBR",
                GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode, b => GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new ImGuiEx.RequiredPluginInfo("AutoRetainer")]);
        }

        public static void DrawLifestreamCommandTextInput()
        {
            var tmp = GatherBuddy.Config.AutoGatherConfig.LifestreamCommand;
            if (ImGui.InputText("Lifestream 指令", ref tmp, 100))
            {
                if (string.IsNullOrEmpty(tmp))
                    tmp = "auto";
                GatherBuddy.Config.AutoGatherConfig.LifestreamCommand = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip(
                "閒置或採集完成時所使用的指令。請勿包含「/li」\n變更此設定時請小心，GBR 不會驗證此指令是否有效！");
        }

        public static void DrawFishCollectionBox()
            => DrawCheckbox("加入釣魚資料收集",
                "啟用後，每當你釣到魚，該魚的資料就會上傳到遠端伺服器\n"
              + "此資料收集的目的是為了打造可用的自動釣魚功能\n"
              + "不會收集任何關於你或你角色的個人資訊，只會收集與所捕獲魚類相關的資料\n"
              + "你可以隨時停用此選項以取消加入。", GatherBuddy.Config.AutoGatherConfig.FishDataCollection,
                b => GatherBuddy.Config.AutoGatherConfig.FishDataCollection = b);

        public static void DrawMaterialExtraction()
            => DrawCheckbox("啟用萃取靈魂水晶",
                "自動從完全精念的物品中萃取靈魂水晶",
                GatherBuddy.Config.AutoGatherConfig.DoMaterialize,
                b => GatherBuddy.Config.AutoGatherConfig.DoMaterialize = b);

        public static void DrawAetherialReduction()
            => DrawCheckbox("啟用以太還原",
                "閒置或物品欄已滿時自動進行以太還原",
                GatherBuddy.Config.AutoGatherConfig.DoReduce,
                b => GatherBuddy.Config.AutoGatherConfig.DoReduce = b);

        public static void DrawUseFlagBox()
            => DrawCheckbox("停用地圖標記導航",            "是否使用地圖標記進行導航（僅限限時採集點）",
                GatherBuddy.Config.AutoGatherConfig.DisableFlagPathing, b => GatherBuddy.Config.AutoGatherConfig.DisableFlagPathing = b);

        public static void DrawFarNodeFilterDistance()
        {
            var tmp = GatherBuddy.Config.AutoGatherConfig.FarNodeFilterDistance;
            if (ImGui.DragFloat("遠距採集點篩選距離", ref tmp, 0.1f, 0.1f, 100f))
            {
                GatherBuddy.Config.AutoGatherConfig.FarNodeFilterDistance = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip(
                "尋找未採空的採集點時，GBR 會過濾掉比此距離更靠近你的採集點。可避免重複檢查明顯已採空的採集點。");
        }

        public static void DrawTimedNodePrecog()
        {
            var tmp = GatherBuddy.Config.AutoGatherConfig.TimedNodePrecog;
            if (ImGui.DragInt("限時採集點提前時間（秒）", ref tmp, 1, 0, 600))
            {
                GatherBuddy.Config.AutoGatherConfig.TimedNodePrecog = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("GBR 應提前多久將採集點視為已上線");
        }

        public static void DrawExecutionDelay()
        {
            var tmp = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
            if (ImGui.DragInt("執行延遲（毫秒）", ref tmp, 1, 0, 1500))
            {
                GatherBuddy.Config.AutoGatherConfig.ExecutionDelay = (uint)Math.Min(Math.Max(0, tmp), 10000);
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("延遲指定時間後才執行每個動作。");
        }

        public static void DrawUseGivingLandOnCooldown()
            => DrawCheckbox("大地的恩惠冷卻結束時採集任意水晶",
                "無論目前目標物品為何，只要大地的恩惠可用，就在任意一般採集點採集隨機水晶。",
                GatherBuddy.Config.AutoGatherConfig.UseGivingLandOnCooldown,
                b => GatherBuddy.Config.AutoGatherConfig.UseGivingLandOnCooldown = b);

        public static void DrawMountUpDistance()
        {
            var tmp = GatherBuddy.Config.AutoGatherConfig.MountUpDistance;
            if (ImGui.DragFloat("騎乘距離", ref tmp, 0.1f, 0.1f, 100f))
            {
                GatherBuddy.Config.AutoGatherConfig.MountUpDistance = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("移動到採集點時，超過此距離將會騎乘坐騎。");
        }

        public static void DrawMoveWhileMounting()
            => DrawCheckbox("召喚坐騎時就開始移動",
                "召喚坐騎的同時就開始向下一個採集點尋路",
                GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting,
                b => GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting = b);

        public static void DrawAntiStuckCooldown()
        {
            var tmp = GatherBuddy.Config.AutoGatherConfig.NavResetCooldown;
            if (ImGui.DragFloat("防卡住冷卻時間", ref tmp, 0.1f, 0.1f, 10f))
            {
                GatherBuddy.Config.AutoGatherConfig.NavResetCooldown = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("卡住時，導航系統重置前需等待的秒數。");
        }

        public static void DrawForceWalkingBox()
            => DrawCheckbox("強制步行",                      "強制步行前往採集點而不使用坐騎。",
                GatherBuddy.Config.AutoGatherConfig.ForceWalking, b => GatherBuddy.Config.AutoGatherConfig.ForceWalking = b);

        public static void DrawUseNavigationBox()
            => DrawCheckbox("使用 vnavmesh 導航",             "使用 vnavmesh 導航自動移動你的角色",
                GatherBuddy.Config.AutoGatherConfig.UseNavigation, b => GatherBuddy.Config.AutoGatherConfig.UseNavigation = b);

        public static void DrawStuckThreshold()
        {
            var tmp = GatherBuddy.Config.AutoGatherConfig.NavResetThreshold;
            if (ImGui.DragFloat("卡住判定閾值", ref tmp, 0.1f, 0.1f, 10f))
            {
                GatherBuddy.Config.AutoGatherConfig.NavResetThreshold = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("導航系統判定你卡住前需等待的秒數。");
        }

        public static void DrawSortingMethodCombo()
        {
            var v = GatherBuddy.Config.AutoGatherConfig.SortingMethod;
            ImGui.SetNextItemWidth(SetInputWidth);

            using var combo = ImRaii.Combo("物品排序方式", v.ToString());
            ImGuiUtil.HoverTooltip("內部排序物品時所使用的方法");
            if (!combo)
                return;

            if (ImGui.Selectable(AutoGatherConfig.SortingType.Location.ToString(), v == AutoGatherConfig.SortingType.Location))
            {
                GatherBuddy.Config.AutoGatherConfig.SortingMethod = AutoGatherConfig.SortingType.Location;
                GatherBuddy.Config.Save();
            }

            if (ImGui.Selectable(AutoGatherConfig.SortingType.None.ToString(), v == AutoGatherConfig.SortingType.None))
            {
                GatherBuddy.Config.AutoGatherConfig.SortingMethod = AutoGatherConfig.SortingType.None;
                GatherBuddy.Config.Save();
            }
        }

        // General Config
        public static void DrawOpenOnStartBox()
            => DrawCheckbox("啟動時開啟設定介面",
                "切換是否在遊戲啟動後顯示 GatherBuddy 介面。",
                GatherBuddy.Config.OpenOnStart, b => GatherBuddy.Config.OpenOnStart = b);

        public static void DrawLockPositionBox()
            => DrawCheckbox("鎖定設定介面位置",
                "切換是否鎖定 GatherBuddy 介面的移動。",
                GatherBuddy.Config.MainWindowLockPosition, b =>
                {
                    GatherBuddy.Config.MainWindowLockPosition = b;
                    _base.UpdateFlags();
                });

        public static void DrawLockResizeBox()
            => DrawCheckbox("鎖定設定介面大小",
                "切換是否鎖定 GatherBuddy 介面的大小。",
                GatherBuddy.Config.MainWindowLockResize, b =>
                {
                    GatherBuddy.Config.MainWindowLockResize = b;
                    _base.UpdateFlags();
                });

        public static void DrawRespectEscapeBox()
            => DrawCheckbox("按 Esc 關閉主視窗",
                "切換當主視窗獲得焦點時，按下 Esc 是否會關閉它。",
                GatherBuddy.Config.CloseOnEscape, b =>
                {
                    GatherBuddy.Config.CloseOnEscape = b;
                    _base.UpdateFlags();
                });

        public static void DrawGearChangeBox()
            => DrawCheckbox("啟用裝備切換",
                "切換是否自動切換為對應採集點的職業裝備。\n使用採礦工套裝、園藝工套裝與捕魚人套裝。",
                GatherBuddy.Config.UseGearChange, b => GatherBuddy.Config.UseGearChange = b);

        public static void DrawTeleportBox()
            => DrawCheckbox("啟用傳送",
                "切換是否自動傳送到選定的採集點。",
                GatherBuddy.Config.UseTeleport, b => GatherBuddy.Config.UseTeleport = b);

        public static void DrawMapOpenBox()
            => DrawCheckbox("開啟地圖並標示位置",
                "切換是否自動開啟選定採集點所在區域的地圖，並標示出採集位置。",
                GatherBuddy.Config.UseCoordinates, b => GatherBuddy.Config.UseCoordinates = b);

        public static void DrawPlaceMarkerBox()
            => DrawCheckbox("在地圖上放置旗標",
                "切換是否在不開啟地圖的情況下，自動在選定採集點的大致位置放置紅色旗標。",
                GatherBuddy.Config.UseFlag, b => GatherBuddy.Config.UseFlag = b);

        public static void DrawMapMarkerPrintBox()
            => DrawCheckbox("輸出地圖位置",
                "切換是否自動將選定採集點大致位置的地圖連結輸出到聊天室。",
                GatherBuddy.Config.WriteCoordinates, b => GatherBuddy.Config.WriteCoordinates = b);

        public static void DrawPlaceWaymarkBox()
            => DrawCheckbox("放置自訂場地標記",
                "切換是否在特定地點放置你手動設定的自訂場地標記。",
                GatherBuddy.Config.PlaceCustomWaymarks, b => GatherBuddy.Config.PlaceCustomWaymarks = b);

        public static void DrawPrintUptimesBox()
            => DrawCheckbox("採集時輸出採集點上線時間",
                "當使用 /gather 前往的採集點並非永遠上線時，在聊天室輸出其上線時間。",
                GatherBuddy.Config.PrintUptime, b => GatherBuddy.Config.PrintUptime = b);

        public static void DrawSkipTeleportBox()
            => DrawCheckbox("跳過鄰近傳送",
                "若你已經在同一張地圖上，且比選定的以太之光更靠近目標，則跳過傳送。",
                GatherBuddy.Config.SkipTeleportIfClose, b => GatherBuddy.Config.SkipTeleportIfClose = b);

        public static void DrawShowStatusLineBox()
            => DrawCheckbox("顯示狀態列",
                "在採集物與魚類表格下方顯示狀態列。",
                GatherBuddy.Config.ShowStatusLine, v => GatherBuddy.Config.ShowStatusLine = v);

        public static void DrawHideClippyBox()
            => DrawCheckbox("隱藏 GatherClippy 按鈕",
                "永久隱藏採集物與釣魚頁籤中的 GatherClippy 按鈕。",
                GatherBuddy.Config.HideClippy, v => GatherBuddy.Config.HideClippy = v);

        private const string ChatInformationString =
            "請注意，無論選擇何種頻道，訊息都只會顯示在你自己的聊天記錄中"
          + "——其他人不會看到你的「說話」訊息。";

        public static void DrawPrintTypeSelector()
            => DrawChatTypeSelector("一般訊息聊天類型",
                "GatherBuddy 輸出一般訊息時所使用的聊天類型。\n"
              + ChatInformationString,
                GatherBuddy.Config.ChatTypeMessage, t => GatherBuddy.Config.ChatTypeMessage = t);

        public static void DrawErrorTypeSelector()
            => DrawChatTypeSelector("錯誤訊息聊天類型",
                "GatherBuddy 輸出錯誤訊息時所使用的聊天類型。\n"
              + ChatInformationString,
                GatherBuddy.Config.ChatTypeError, t => GatherBuddy.Config.ChatTypeError = t);

        public static void DrawContextMenuBox()
            => DrawCheckbox("加入遊戲內右鍵選單",
                "為可採集物品的遊戲內右鍵選單加入「採集」項目。",
                GatherBuddy.Config.AddIngameContextMenus, b =>
                {
                    GatherBuddy.Config.AddIngameContextMenus = b;
                    if (b)
                        _plugin.ContextMenu.Enable();
                    else
                        _plugin.ContextMenu.Disable();
                });

        public static void DrawPreferredJobSelect()
        {
            var v       = GatherBuddy.Config.PreferredGatheringType;
            var current = v == GatheringType.Multiple ? "無偏好" : v.ToString();
            ImGui.SetNextItemWidth(SetInputWidth);
            using var combo = ImRaii.Combo("偏好職業", current);
            ImGuiUtil.HoverTooltip(
                "選擇當物品可同時由採礦工與園藝工採集時，你偏好使用的職業。\n"
              + "此設定實際上會在物品可由兩種職業採集時，將一般的採集指令轉為 /gathermin 或 /gatherbtn，"
              + "即使連續嘗試也會忽略其他選項。");
            if (!combo)
                return;

            if (ImGui.Selectable("無偏好", v == GatheringType.Multiple) && v != GatheringType.Multiple)
            {
                GatherBuddy.Config.PreferredGatheringType = GatheringType.Multiple;
                GatherBuddy.Config.Save();
            }

            if (ImGui.Selectable(GatheringType.Miner.ToString(), v == GatheringType.Miner) && v != GatheringType.Miner)
            {
                GatherBuddy.Config.PreferredGatheringType = GatheringType.Miner;
                GatherBuddy.Config.Save();
            }

            if (ImGui.Selectable(GatheringType.Botanist.ToString(), v == GatheringType.Botanist) && v != GatheringType.Botanist)
            {
                GatherBuddy.Config.PreferredGatheringType = GatheringType.Botanist;
                GatherBuddy.Config.Save();
            }
        }

        public static void DrawPrintClipboardBox()
            => DrawCheckbox("輸出剪貼簿資訊",
                "每當你將物件儲存到剪貼簿時，於聊天室輸出訊息。失敗時無論如何都會輸出訊息。",
                GatherBuddy.Config.PrintClipboardMessages, b => GatherBuddy.Config.PrintClipboardMessages = b);

        // Weather Tab
        public static void DrawWeatherTabNamesBox()
            => DrawCheckbox("在天氣頁籤顯示名稱",
                "切換天氣頁籤的表格中是否顯示名稱文字，或只顯示圖示並在滑鼠懸停時顯示名稱。",
                GatherBuddy.Config.ShowWeatherNames, b => GatherBuddy.Config.ShowWeatherNames = b);

        // Alarms
        public static void DrawAlarmToggle()
            => DrawCheckbox("啟用提醒", "切換所有提醒的開關。", GatherBuddy.Config.AlarmsEnabled,
                b =>
                {
                    if (b)
                        _plugin.AlarmManager.Enable();
                    else
                        _plugin.AlarmManager.Disable();
                });

        private static bool _gatherDebug = false;

        public static void DrawAlarmsInDutyToggle()
            => DrawCheckbox("在任務中啟用提醒", "設定當你受任務束縛時，提醒是否應該觸發。",
                GatherBuddy.Config.AlarmsInDuty,     b => GatherBuddy.Config.AlarmsInDuty = b);

        public static void DrawAlarmsOnlyWhenLoggedInToggle()
            => DrawCheckbox("僅在遊戲內啟用提醒",  "設定當你未登入任何角色時，提醒是否不應觸發。",
                GatherBuddy.Config.AlarmsOnlyWhenLoggedIn, b => GatherBuddy.Config.AlarmsOnlyWhenLoggedIn = b);

        private static void DrawAlarmPicker(string label, string description, Sounds current, Action<Sounds> setter)
        {
            var cur = (int)current;
            ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo(label, ref cur, AlarmCache.SoundIdNames))
                setter((Sounds)cur);
            ImGuiUtil.HoverTooltip(description);
        }

        public static void DrawWeatherAlarmPicker()
            => DrawAlarmPicker("天氣變化提醒音效", "選擇每 8 艾歐傑亞小時天氣變化時播放的音效。",
                GatherBuddy.Config.WeatherAlarm,       _plugin.AlarmManager.SetWeatherAlarm);

        public static void DrawHourAlarmPicker()
            => DrawAlarmPicker("艾歐傑亞時間變化提醒音效", "選擇每次艾歐傑亞時間變化時播放的音效。",
                GatherBuddy.Config.HourAlarm,              _plugin.AlarmManager.SetHourAlarm);

        // Fish Timer
        public static void DrawFishTimerBox()
            => DrawCheckbox("顯示釣魚計時器",
                "切換釣魚時是否顯示釣魚計時器視窗。",
                GatherBuddy.Config.ShowFishTimer, b => GatherBuddy.Config.ShowFishTimer = b);

        public static void DrawFishTimerEditBox()
            => DrawCheckbox("編輯釣魚計時器",
                "啟用編輯釣魚計時器視窗。",
                GatherBuddy.Config.FishTimerEdit, b => GatherBuddy.Config.FishTimerEdit = b);

        public static void DrawFishTimerClickthroughBox()
            => DrawCheckbox("啟用釣魚計時器點擊穿透",
                "允許滑鼠點擊穿透釣魚計時器視窗，並改為停用其右鍵選單。",
                GatherBuddy.Config.FishTimerClickthrough, b => GatherBuddy.Config.FishTimerClickthrough = b);

        public static void DrawFishTimerHideBox()
            => DrawCheckbox("在釣魚計時器中隱藏未捕獲的魚",
                "隱藏釣魚計時器視窗中，尚未以該擬餌鉤與魚餌組合記錄過的所有魚。",
                GatherBuddy.Config.HideUncaughtFish, b => GatherBuddy.Config.HideUncaughtFish = b);

        public static void DrawFishTimerHideBox2()
            => DrawCheckbox("在釣魚計時器中隱藏不可捕獲的魚",
                "隱藏釣魚計時器視窗中，已知需求未滿足的所有魚，例如捕魚人之識或擬餌鉤。",
                GatherBuddy.Config.HideUnavailableFish, b => GatherBuddy.Config.HideUnavailableFish = b);

        public static void DrawFishTimerUptimesBox()
            => DrawCheckbox("在釣魚計時器中顯示上線時間",
                "在釣魚計時器視窗中顯示受限制魚類的上線時間。",
                GatherBuddy.Config.ShowFishTimerUptimes, b => GatherBuddy.Config.ShowFishTimerUptimes = b);

        public static void DrawKeepRecordsBox()
            => DrawCheckbox("保留釣魚紀錄",
                "將釣魚紀錄儲存在你的電腦上。釣魚計時器視窗的咬鉤時間功能需要此設定。",
                GatherBuddy.Config.StoreFishRecords, b => GatherBuddy.Config.StoreFishRecords = b);

        public static void DrawShowLocalTimeInRecordsBox()
            => DrawCheckbox("在紀錄中使用本地時間",
                "在釣魚紀錄頁籤顯示時間戳記時，使用本地時間而非 Unix 時間。",
                GatherBuddy.Config.UseUnixTimeFishRecords, b => GatherBuddy.Config.UseUnixTimeFishRecords = b);
        
        public static void DrawFishTimerScale()
        {
            var value = GatherBuddy.Config.FishTimerScale / 1000f;
            ImGui.SetNextItemWidth(SetInputWidth);
            var ret = ImGui.DragFloat("釣魚計時器咬鉤時間縮放", ref value, 0.1f, FishRecord.MinBiteTime / 500f,
                FishRecord.MaxBiteTime / 1000f,
                "%2.3f 秒");

            ImGuiUtil.HoverTooltip("釣魚計時器視窗的咬鉤時間會依此數值縮放。\n"
              + "若你的咬鉤時間超過此數值，進度條與咬鉤區間將不會顯示。\n"
              + "建議將此值設定得盡量高於你最長的咬鉤區間，同時盡量保持較低。通常 40 秒左右已足夠。");

            if (!ret)
                return;

            var newValue = (ushort)Math.Clamp((int)(value * 1000f + 0.9), FishRecord.MinBiteTime * 2, FishRecord.MaxBiteTime);
            if (newValue == GatherBuddy.Config.FishTimerScale)
                return;

            GatherBuddy.Config.FishTimerScale = newValue;
            GatherBuddy.Config.Save();
        }

        public static void DrawFishTimerIntervals()
        {
            int value = GatherBuddy.Config.ShowSecondIntervals;
            ImGui.SetNextItemWidth(SetInputWidth);
            var ret = ImGui.DragInt("釣魚計時器間隔分隔線", ref value, 0.01f, 0, 16);
            ImGuiUtil.HoverTooltip("釣魚計時器視窗可以顯示 0 到 16 條間隔線及對應秒數。\n"
              + "設為 0 可關閉此功能。");
            if (!ret)
                return;

            var newValue = (byte)Math.Clamp(value, 0, 16);
            if (newValue == GatherBuddy.Config.ShowSecondIntervals)
                return;

            GatherBuddy.Config.ShowSecondIntervals = newValue;
            GatherBuddy.Config.Save();
        }

        public static void DrawFishTimerIntervalsRounding()
        {
            var value = GatherBuddy.Config.SecondIntervalsRounding;
            ImGui.SetNextItemWidth(SetInputWidth);
            var ret = ImGui.DragInt("釣魚計時器間隔秒數捨入位數", ref value, 0.01f, 0, 3);
            ImGuiUtil.HoverTooltip("將顯示的秒數捨入到小數點後這麼多位數。\n"
                + "設為 0 只顯示整數。");
            if (!ret)
                return;

            var newValue = (byte)Math.Clamp(value, 0, 3);
            if (newValue == GatherBuddy.Config.SecondIntervalsRounding)
                return;

            GatherBuddy.Config.SecondIntervalsRounding = newValue;
            GatherBuddy.Config.Save();
        }

        public static void DrawHideFishPopupBox()
            => DrawCheckbox("隱藏捕獲彈出視窗",
                "阻止顯示捕獲魚類及其尺寸、數量與品質的彈出視窗。",
                GatherBuddy.Config.HideFishSizePopup, b => GatherBuddy.Config.HideFishSizePopup = b);

        public static void DrawCollectableHintPopupBox()
            => DrawCheckbox("顯示收藏品提示",
                "在釣魚計時器視窗中顯示魚是否為收藏品。",
                GatherBuddy.Config.ShowCollectableHints, b => GatherBuddy.Config.ShowCollectableHints = b);

        public static void DrawDoubleHookHintPopupBox()
            => DrawCheckbox("顯示多重提竿提示",
                "顯示魚在宇宙探索中是否可雙鉤或三鉤捕獲。", // TODO: add ocean fishing when implemented.
                GatherBuddy.Config.ShowMultiHookHints, b => GatherBuddy.Config.ShowMultiHookHints = b);


        // Fish Stats Window
        public static void DrawEnableFishStats()
            => DrawCheckbox("啟用釣魚統計",
                "新增一個依本機紀錄彙整並產生釣魚統計報告的頁籤。目前為測試階段。",
                GatherBuddy.Config.EnableFishStats, b => GatherBuddy.Config.EnableFishStats = b);
        public static void DrawEnableReportTime()
            => DrawCheckbox("報告中包含時間統計",
                "產生報告時，加入最短與最長咬鉤時間。",
                GatherBuddy.Config.EnableReportTime, b => GatherBuddy.Config.EnableReportTime = b);
        public static void DrawEnableReportSize()
            => DrawCheckbox("報告中包含尺寸統計",
                "產生報告時，加入最小與最大尺寸。",
                GatherBuddy.Config.EnableReportSize, b => GatherBuddy.Config.EnableReportSize = b);
        public static void DrawEnableReportMulti()
            => DrawCheckbox("報告中包含多重提竿統計",
                "產生報告時，加入多重提竿產量的統計資料。",
                GatherBuddy.Config.EnableReportMulti, b => GatherBuddy.Config.EnableReportMulti = b);
        public static void DrawEnableGraphs()
            => DrawCheckbox("啟用圖表",
                "檢視釣點時啟用釣魚報告資料視覺化。高度測試中！",
                GatherBuddy.Config.EnableFishStatsGraphs, b => GatherBuddy.Config.EnableFishStatsGraphs = b);

        // Spearfishing Helper
        public static void DrawSpearfishHelperBox()
            => DrawCheckbox("顯示刺魚輔助工具",
                "切換刺魚時是否顯示刺魚輔助工具。",
                GatherBuddy.Config.ShowSpearfishHelper, b => GatherBuddy.Config.ShowSpearfishHelper = b);

        public static void DrawSpearfishNamesBox()
            => DrawCheckbox("顯示魚名疊層",
                "切換刺魚視窗中是否顯示已辨識魚的名稱。",
                GatherBuddy.Config.ShowSpearfishNames, b => GatherBuddy.Config.ShowSpearfishNames = b);

        public static void DrawAvailableSpearfishBox()
            => DrawCheckbox("顯示可捕獲魚類清單",
                "切換是否在刺魚視窗側邊顯示目前刺魚點可捕獲的魚類清單。",
                GatherBuddy.Config.ShowAvailableSpearfish, b => GatherBuddy.Config.ShowAvailableSpearfish = b);

        public static void DrawSpearfishSpeedBox()
            => DrawCheckbox("在疊層中顯示魚的速度",
                "切換是否在刺魚視窗中除了名稱外也顯示魚的速度。",
                GatherBuddy.Config.ShowSpearfishSpeed, b => GatherBuddy.Config.ShowSpearfishSpeed = b);

        public static void DrawSpearfishCenterLineBox()
            => DrawCheckbox("顯示中心線",
                "切換是否在刺魚視窗中從刺魚叉中心往上顯示一條直線。",
                GatherBuddy.Config.ShowSpearfishCenterLine, b => GatherBuddy.Config.ShowSpearfishCenterLine = b);

        public static void DrawSpearfishIconsAsTextBox()
            => DrawCheckbox("以文字顯示速度與尺寸",
                "切換是否以文字而非圖示顯示可捕獲魚類的速度與尺寸。",
                GatherBuddy.Config.ShowSpearfishListIconsAsText, b => GatherBuddy.Config.ShowSpearfishListIconsAsText = b);

        public static void DrawSpearfishFishNameFixed()
            => DrawCheckbox("在固定位置顯示魚名",
                "切換是否將已辨識魚的名稱顯示在移動中的魚本身上，或顯示在固定位置。",
                GatherBuddy.Config.FixNamesOnPosition, b => GatherBuddy.Config.FixNamesOnPosition = b);

        public static void DrawSpearfishFishNamePercentage()
        {
            if (!GatherBuddy.Config.FixNamesOnPosition)
                return;

            var tmp = (int)GatherBuddy.Config.FixNamesPercentage;
            ImGui.SetNextItemWidth(SetInputWidth);
            if (!ImGui.DragInt("魚名位置百分比", ref tmp, 0.1f, 0, 100, "%i%%"))
                return;

            tmp = Math.Clamp(tmp, 0, 100);
            if (tmp == GatherBuddy.Config.FixNamesPercentage)
                return;

            GatherBuddy.Config.FixNamesPercentage = (byte)tmp;
            GatherBuddy.Config.Save();
        }

        // Gather Window
        public static void DrawShowGatherWindowBox()
            => DrawCheckbox("顯示採集視窗",
                "顯示一個小視窗，內含已釘選的採集物與其上線時間。",
                GatherBuddy.Config.ShowGatherWindow, b => GatherBuddy.Config.ShowGatherWindow = b);

        public static void DrawGatherWindowAnchorBox()
            => DrawCheckbox("將採集視窗錨定於左下角",
                "讓採集視窗向上成長並從上方縮小，而非從下方。",
                GatherBuddy.Config.GatherWindowBottomAnchor, b => GatherBuddy.Config.GatherWindowBottomAnchor = b);

        public static void DrawGatherWindowTimersBox()
            => DrawCheckbox("顯示採集視窗計時器",
                "在採集視窗中顯示採集物的上線時間。",
                GatherBuddy.Config.ShowGatherWindowTimers, b => GatherBuddy.Config.ShowGatherWindowTimers = b);

        public static void DrawGatherWindowAlarmsBox()
            => DrawCheckbox("在採集視窗中顯示已啟用的提醒",
                "額外將已啟用的提醒顯示為採集視窗的最後一個預設，並遵循視窗的一般規則。",
                GatherBuddy.Config.ShowGatherWindowAlarms, b =>
                {
                    GatherBuddy.Config.ShowGatherWindowAlarms = b;
                    _plugin.GatherWindowManager.SetShowGatherWindowAlarms(b);
                });

        public static void DrawSortGatherWindowBox()
            => DrawCheckbox("依上線時間排序採集視窗",
                "將採集視窗中選取的物品依上線時間排序。",
                GatherBuddy.Config.SortGatherWindowByUptime, b => GatherBuddy.Config.SortGatherWindowByUptime = b);

        public static void DrawGatherWindowShowOnlyAvailableBox()
            => DrawCheckbox("僅顯示可用物品",
                "僅顯示採集視窗設定中目前可用的物品。",
                GatherBuddy.Config.ShowGatherWindowOnlyAvailable, b => GatherBuddy.Config.ShowGatherWindowOnlyAvailable = b);

        public static void DrawHideGatherWindowCompletedItemsBox()
            => DrawCheckbox("隱藏已完成物品",
                "隱藏物品欄中已有所需數量的物品。",
                GatherBuddy.Config.HideGatherWindowCompletedItems, b => GatherBuddy.Config.HideGatherWindowCompletedItems = b);

        public static void DrawHideGatherWindowInDutyBox()
            => DrawCheckbox("在任務中隱藏採集視窗",
                "當受任何任務束縛時隱藏採集視窗。",
                GatherBuddy.Config.HideGatherWindowInDuty, b => GatherBuddy.Config.HideGatherWindowInDuty = b);

        public static void DrawGatherWindowHoldKey()
        {
            DrawCheckbox("僅在按住按鍵時顯示採集視窗",
                "僅在你按住選定的按鍵時顯示採集視窗。",
                GatherBuddy.Config.OnlyShowGatherWindowHoldingKey, b => GatherBuddy.Config.OnlyShowGatherWindowHoldingKey = b);

            if (!GatherBuddy.Config.OnlyShowGatherWindowHoldingKey)
                return;

            ImGui.SetNextItemWidth(SetInputWidth);
            Widget.KeySelector("要按住的快捷鍵", "設定要按住以保持視窗顯示的快捷鍵。",
                GatherBuddy.Config.GatherWindowHoldKey,
                k => GatherBuddy.Config.GatherWindowHoldKey = k, Configuration.ValidKeys);
        }

        public static void DrawGatherWindowLockBox()
            => DrawCheckbox("鎖定採集視窗位置",
                "防止透過拖曳移動採集視窗。",
                GatherBuddy.Config.LockGatherWindow, b => GatherBuddy.Config.LockGatherWindow = b);


        public static void DrawGatherWindowHotkeyInput()
        {
            if (Widget.ModifiableKeySelector("開啟採集視窗快捷鍵", "設定用於開啟採集視窗的快捷鍵。", SetInputWidth,
                    GatherBuddy.Config.GatherWindowHotkey, k => GatherBuddy.Config.GatherWindowHotkey = k, Configuration.ValidKeys))
                GatherBuddy.Config.Save();
        }

        public static void DrawMainInterfaceHotkeyInput()
        {
            if (Widget.ModifiableKeySelector("開啟主介面快捷鍵", "設定用於開啟 GatherBuddy 主介面的快捷鍵。",
                    SetInputWidth,
                    GatherBuddy.Config.MainInterfaceHotkey, k => GatherBuddy.Config.MainInterfaceHotkey = k, Configuration.ValidKeys))
                GatherBuddy.Config.Save();
        }


        public static void DrawGatherWindowDeleteModifierInput()
        {
            ImGui.SetNextItemWidth(SetInputWidth);
            if (Widget.ModifierSelector("右鍵刪除物品所需修飾鍵",
                    "設定在採集視窗中右鍵點擊物品以刪除時所需的修飾鍵。",
                    GatherBuddy.Config.GatherWindowDeleteModifier, k => GatherBuddy.Config.GatherWindowDeleteModifier = k))
                GatherBuddy.Config.Save();
        }


        public static void DrawAetherytePreference()
        {
            var tmp     = GatherBuddy.Config.AetherytePreference == AetherytePreference.Cost;
            var oldPref = GatherBuddy.Config.AetherytePreference;
            if (ImGui.RadioButton("偏好較便宜的以太之光", tmp))
                GatherBuddy.Config.AetherytePreference = AetherytePreference.Cost;
            var hovered = ImGui.IsItemHovered();
            ImGui.SameLine();
            if (ImGui.RadioButton("偏好較短的移動時間", !tmp))
                GatherBuddy.Config.AetherytePreference = AetherytePreference.Distance;
            hovered |= ImGui.IsItemHovered();
            if (hovered)
                ImGui.SetTooltip(
                    "設定在掃描某物品所有可用採集點時，你偏好距離目標較近（移動時間較短）的以太之光，"
                  + "還是傳送費用較便宜的以太之光。"
                  + "僅在該物品非限時且有多個來源時有意義。");

            if (oldPref != GatherBuddy.Config.AetherytePreference)
            {
                GatherBuddy.UptimeManager.ResetLocations();
                GatherBuddy.Config.Save();
            }
        }

        public static void DrawAlarmFormatInput()
            => DrawFormatInput("提醒聊天訊息格式",
                "留空則不輸出聊天訊息。\n可替換：\n- {Alarm} 為方括號中的提醒名稱。\n- {Item} 為物品連結。\n- {Offset} 為提醒偏移秒數。\n- {DurationString} 為「將於接下來...上線」或「目前上線中，持續...」。\n- {Location} 為地圖旗標連結與地點名稱。",
                GatherBuddy.Config.AlarmFormat, Configuration.DefaultAlarmFormat, s => GatherBuddy.Config.AlarmFormat = s);

        public static void DrawIdentifiedGatherableFormatInput()
            => DrawFormatInput("已辨識採集物聊天訊息格式",
                "留空則不輸出聊天訊息。\n可替換：\n- {Input} 為輸入的搜尋文字。\n- {Item} 為物品連結。",
                GatherBuddy.Config.IdentifiedGatherableFormat, Configuration.DefaultIdentifiedGatherableFormat,
                s => GatherBuddy.Config.IdentifiedGatherableFormat = s);

        public static void DrawAlwaysMapsBox()
            => DrawCheckbox("有藏寶圖時優先採集",      "GBR 若在採集點中看到藏寶圖，會永遠優先採集它",
                GatherBuddy.Config.AutoGatherConfig.AlwaysGatherMaps, b => GatherBuddy.Config.AutoGatherConfig.AlwaysGatherMaps = b);
    }


    private void DrawConfigTab()
    {
        using var id  = ImRaii.PushId("Config");
        using var tab = ImRaii.TabItem("設定");
        ImGuiUtil.HoverTooltip("按照你的細膩要求打造專屬於你的 GatherBuddy。\n"
          + "只要好好對待他，他說不定會變成真正的男孩。");

        if (!tab)
            return;

        using var child = ImRaii.Child("ConfigTab");
        if (!child)
            return;

        if (ImGui.CollapsingHeader("自動採集"))
        {
            if (ImGui.TreeNodeEx("一般##autoGeneral"))
            {
                ConfigFunctions.DrawHonkModeBox();
                ConfigFunctions.DrawHonkVolumeSlider();
                AutoGatherUI.DrawMountSelector();
                ConfigFunctions.DrawMountUpDistance();
                ConfigFunctions.DrawMoveWhileMounting();
                ConfigFunctions.DrawSortingMethodCombo();
                ConfigFunctions.DrawUseGivingLandOnCooldown();
                ConfigFunctions.DrawGoHomeBox();
                ConfigFunctions.DrawUseSkillsForFallabckBox();
                ConfigFunctions.DrawAbandonNodesBox();
                ConfigFunctions.DrawCheckRetainersBox();
                ConfigFunctions.DrawFishCollectionBox();
                ConfigFunctions.DrawAlwaysMapsBox();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("進階"))
            {
                ConfigFunctions.DrawAutoGatherBox();
                ConfigFunctions.DrawUseFlagBox();
                ConfigFunctions.DrawUseNavigationBox();
                ConfigFunctions.DrawForceWalkingBox();
                ConfigFunctions.DrawRepairBox();
                ConfigFunctions.DrawAutoretainerBox();
                if (GatherBuddy.Config.AutoGatherConfig.DoRepair)
                {
                    ConfigFunctions.DrawRepairThreshold();
                }

                ConfigFunctions.DrawFishingSpotMinutes();
                ConfigFunctions.DrawMaterialExtraction();
                ConfigFunctions.DrawAetherialReduction();
                ConfigFunctions.DrawLifestreamCommandTextInput();
                ConfigFunctions.DrawAntiStuckCooldown();
                ConfigFunctions.DrawStuckThreshold();
                ConfigFunctions.DrawTimedNodePrecog();
                ConfigFunctions.DrawExecutionDelay();
                ImGui.TreePop();
            }
        }

        if (ImGui.CollapsingHeader("一般"))
        {
            if (ImGui.TreeNodeEx("採集指令"))
            {
                ConfigFunctions.DrawPreferredJobSelect();
                ConfigFunctions.DrawGearChangeBox();
                ConfigFunctions.DrawTeleportBox();
                ConfigFunctions.DrawMapOpenBox();
                ConfigFunctions.DrawPlaceMarkerBox();
                ConfigFunctions.DrawPlaceWaymarkBox();
                ConfigFunctions.DrawAetherytePreference();
                ConfigFunctions.DrawSkipTeleportBox();
                ConfigFunctions.DrawContextMenuBox();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("套裝名稱"))
            {
                ConfigFunctions.DrawSetInput("採礦工",    GatherBuddy.Config.MinerSetName,    s => GatherBuddy.Config.MinerSetName    = s);
                ConfigFunctions.DrawSetInput("園藝工", GatherBuddy.Config.BotanistSetName, s => GatherBuddy.Config.BotanistSetName = s);
                ConfigFunctions.DrawSetInput("捕魚人",   GatherBuddy.Config.FisherSetName,   s => GatherBuddy.Config.FisherSetName   = s);
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("提醒"))
            {
                ConfigFunctions.DrawAlarmToggle();
                ConfigFunctions.DrawAlarmsInDutyToggle();
                ConfigFunctions.DrawAlarmsOnlyWhenLoggedInToggle();
                ConfigFunctions.DrawWeatherAlarmPicker();
                ConfigFunctions.DrawHourAlarmPicker();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("訊息"))
            {
                ConfigFunctions.DrawPrintTypeSelector();
                ConfigFunctions.DrawErrorTypeSelector();
                ConfigFunctions.DrawMapMarkerPrintBox();
                ConfigFunctions.DrawPrintUptimesBox();
                ConfigFunctions.DrawPrintClipboardBox();
                ConfigFunctions.DrawAlarmFormatInput();
                ConfigFunctions.DrawIdentifiedGatherableFormatInput();
                ImGui.TreePop();
            }

            ImGui.NewLine();
        }

        if (ImGui.CollapsingHeader("介面"))
        {
            if (ImGui.TreeNodeEx("設定視窗"))
            {
                ConfigFunctions._base = this;
                ConfigFunctions.DrawOpenOnStartBox();
                ConfigFunctions.DrawRespectEscapeBox();
                ConfigFunctions.DrawLockPositionBox();
                ConfigFunctions.DrawLockResizeBox();
                ConfigFunctions.DrawWeatherTabNamesBox();
                ConfigFunctions.DrawShowStatusLineBox();
                ConfigFunctions.DrawHideClippyBox();
                ConfigFunctions.DrawMainInterfaceHotkeyInput();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("釣魚計時器"))
            {
                ConfigFunctions.DrawKeepRecordsBox();
                ConfigFunctions.DrawShowLocalTimeInRecordsBox();
                ConfigFunctions.DrawFishTimerBox();
                ConfigFunctions.DrawFishTimerEditBox();
                ConfigFunctions.DrawFishTimerClickthroughBox();
                ConfigFunctions.DrawFishTimerHideBox();
                ConfigFunctions.DrawFishTimerHideBox2();
                ConfigFunctions.DrawFishTimerUptimesBox();
                ConfigFunctions.DrawFishTimerScale();
                ConfigFunctions.DrawFishTimerIntervals();
                ConfigFunctions.DrawFishTimerIntervalsRounding();
                ConfigFunctions.DrawHideFishPopupBox();
                ConfigFunctions.DrawCollectableHintPopupBox();
                ConfigFunctions.DrawDoubleHookHintPopupBox();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("釣魚統計〔測試中〕"))
            {
                ConfigFunctions.DrawEnableFishStats();
                ConfigFunctions.DrawEnableReportTime();
                ConfigFunctions.DrawEnableReportSize();
                ConfigFunctions.DrawEnableReportMulti();
                ConfigFunctions.DrawEnableGraphs();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("採集視窗"))
            {
                ConfigFunctions.DrawShowGatherWindowBox();
                ConfigFunctions.DrawGatherWindowAnchorBox();
                ConfigFunctions.DrawGatherWindowTimersBox();
                ConfigFunctions.DrawGatherWindowAlarmsBox();
                ConfigFunctions.DrawSortGatherWindowBox();
                ConfigFunctions.DrawGatherWindowShowOnlyAvailableBox();
                ConfigFunctions.DrawHideGatherWindowCompletedItemsBox();
                ConfigFunctions.DrawHideGatherWindowInDutyBox();
                ConfigFunctions.DrawGatherWindowHoldKey();
                ConfigFunctions.DrawGatherWindowLockBox();
                ConfigFunctions.DrawGatherWindowHotkeyInput();
                ConfigFunctions.DrawGatherWindowDeleteModifierInput();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("刺魚輔助工具"))
            {
                ConfigFunctions.DrawSpearfishHelperBox();
                ConfigFunctions.DrawSpearfishNamesBox();
                ConfigFunctions.DrawSpearfishSpeedBox();
                ConfigFunctions.DrawAvailableSpearfishBox();
                ConfigFunctions.DrawSpearfishIconsAsTextBox();
                ConfigFunctions.DrawSpearfishCenterLineBox();
                ConfigFunctions.DrawSpearfishFishNameFixed();
                ConfigFunctions.DrawSpearfishFishNamePercentage();
                ImGui.TreePop();
            }

            ImGui.NewLine();
        }

        if (ImGui.CollapsingHeader("顏色"))
        {
            foreach (var color in Enum.GetValues<ColorId>())
            {
                var (defaultColor, name, description) = color.Data();
                var currentColor = GatherBuddy.Config.Colors.TryGetValue(color, out var current) ? current : defaultColor;
                if (Widget.ColorPicker(name, description, currentColor, c => GatherBuddy.Config.Colors[color] = c, defaultColor))
                    GatherBuddy.Config.Save();
            }

            ImGui.NewLine();

            if (Widget.PaletteColorPicker("聊天室中的名稱", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorNames,
                    Configuration.DefaultSeColorNames, Configuration.ForegroundColors, out var idx))
                GatherBuddy.Config.SeColorNames = idx;
            if (Widget.PaletteColorPicker("聊天室中的指令", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorCommands,
                    Configuration.DefaultSeColorCommands, Configuration.ForegroundColors, out idx))
                GatherBuddy.Config.SeColorCommands = idx;
            if (Widget.PaletteColorPicker("聊天室中的參數", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorArguments,
                    Configuration.DefaultSeColorArguments, Configuration.ForegroundColors, out idx))
                GatherBuddy.Config.SeColorArguments = idx;
            if (Widget.PaletteColorPicker("聊天室中的提醒訊息", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorAlarm,
                    Configuration.DefaultSeColorAlarm, Configuration.ForegroundColors, out idx))
                GatherBuddy.Config.SeColorAlarm = idx;

            ImGui.NewLine();
        }
    }
}
