# GatherBuddyReborn

[GatherBuddyReborn](https://github.com/FFXIV-CombatReborn/GatherBuddyReborn) 的台服移植版，協助玩家進行採集、挖礦與釣魚。搭配 [vnavmesh](https://github.com/ffxiv-tc-port/vnavmesh) 插件可實現全自動採集路徑導航。

## 功能

- **自動採集**：自動導航至採集點進行採集／挖礦／釣魚並收滿指定物品，過程中可自動換裝、精選、精製魔晶石、修理裝備、使用強心劑與食物，並具備防卡住機制（需要 vnavmesh）
- **自動採集清單**：排入多項目標物品依序自動採集，可從 TeamCraft 匯入、複製到剪貼簿分享，也可從採集群組直接建立
- **採集視窗**：自訂多組採集清單預設，快速切換目標
- **提醒**：依採集點出現時間與天氣條件提醒
- **釣魚計時器**：顯示咬鉤時機與魚類判定，支援手動編輯模式
- **刺魚輔助工具**：輔助刺魚時的判定
- **天氣**：查詢各地天氣時段
- **採集群組**：依時間輪替的採集清單
- **地點**：可查詢的採集點與釣點資料
- **釣魚紀錄與統計**：記錄每次釣獲細節，並提供依釣點、咬鉤時間、宇宙探索釣魚任務分類的統計頁
- **右鍵選單整合**：直接將物品加入提醒、採集群組或自動採集清單，或在 TeamCraft 開啟
- **指令**：`/gbr`、`/gatherbuddy`（開啟主視窗）、`/gather`、`/gatherbtn`、`/gathermin`、`/gatherfish`（標記最近節點並自動傳送、換裝）、`/gathergroup`、`/gbc`（快捷開關）

## 台服版的修改

- 介面繁體中文化（依台服官方譯名），釣魚筆記訊息比對改用台服的繁中字串；TeamCraft 連結使用繁中語言代碼。
- 修正台服用戶端採集視窗資料欄位位移與型別不同造成的自動採集不選物品、關窗不採、崩潰等問題。
- 開放其他插件透過 IPC 把道具加進自動採集清單、設定目標數量、開關自動採集（Artisan 的「缺料交給 GatherBuddy」就是用這個），加進來的道具收在「來自其他插件」清單；也能查詢限時採集點素材的預估採集時間。
- 自動採集清單裡未知／傳說採集點的道具，列尾以灰字顯示預估還要幾輪、多久才採得完（滑鼠移上去看算法與下次出現時間）；算不出來顯示「?」。
- 加入台服 7.3 的新魚（含行星法恩娜的宇宙探索任務魚），魚類清單可依 7.3 篩選；法恩娜任務釣場內建站位紀錄，自動釣魚不必先手動釣過一次。
- 自動釣魚不需要勾選設定裡的「加入釣魚資料收集」；沒勾時不會上傳任何釣魚紀錄，本機紀錄照常保存。
- 自動採集要傳送時，如果你正在用市場佈告板、商店、傳喚鈴、與 NPC 對話、看過場動畫或交易，會等你忙完再傳送。
- 自動採集進行中插件被重新載入（停用再啟用、更新）後，會提示「繼續自動採集」，點了才開始；另有「插件重新載入後自動繼續自動採集」選項（預設關）。關閉遊戲再開不會提示、也不會自動繼續。
- 自動採集期間暫停 AutoRetainer；借用 AutoHook 的開關時記下原狀態，停用時還原。
- 自動採集自己停下時發出通知，並可請塔塔露喊一句（需要 TataruPraise）。
- 沒裝或中途卸載 vnavmesh、Lifestream、Allagan Tools 時不再跳錯誤面板，缺 Lifestream 時會說明要手動傳送。
- 修正按遊戲視窗時重按正在關閉的視窗、雲冠群島排隊空轉等問題。
- 遊戲改版後有特徵碼對不上時，插件照常載入，只停用對應的釣魚相關功能。
- 提醒、採集群組、採集視窗、自動採集清單等存檔讀不進來時，先備份原檔（檔名加 `.bad-`）再處理；存檔中斷不會留下半截檔。
- 多處降低記憶體用量。

## 安裝

1. 在遊戲內輸入 `/xlsettings` 開啟「Dalamud 設定」，到「試驗性功能」分頁的「第三方插件倉庫」加入
   `https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json`
   勾選啟用後儲存。
2. 輸入 `/xlplugins` 開啟「插件管理員」，搜尋「GatherBuddy Reborn」安裝。

同一個插件只裝這裡的版本，不要同時安裝上游（國際服）版本。

## 需要搭配的插件

- 必要（自動採集）：[vnavmesh](https://github.com/ffxiv-tc-port/vnavmesh)
- 選用：[Lifestream](https://github.com/ffxiv-tc-port/Lifestream)（跨區移動）、[AutoHook](https://github.com/ffxiv-tc-port/AutoHook)（釣魚）、[AutoRetainer](https://github.com/ffxiv-tc-port/AutoRetainer)、[Allagan Tools](https://github.com/ffxiv-tc-port/InventoryTools)、[TataruPraise](https://github.com/ffxiv-tc-port/TataruPraise)

授權：Apache-2.0（見 LICENSE）
