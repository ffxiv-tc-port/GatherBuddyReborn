using System;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace GatherBuddy.Utility;

public readonly struct MultiString(string en, string de, string fr, string jp)
{
    public static string ParseSeStringLumina(ReadOnlySeString? luminaString)
        => luminaString?.ExtractText() ?? string.Empty;

    public readonly string English  = en;
    public readonly string German   = de;
    public readonly string French   = fr;
    public readonly string Japanese = jp;

    public string this[ClientLanguage lang]
        => Name(lang);

    public override string ToString()
        => Name(ClientLanguage.English);

    public string ToWholeString()
        => $"{English}|{German}|{French}|{Japanese}";


    // 台服(TC)注意:我們這份 Dalamud fork 的 Lumina 把 GetExcelSheet 的語言參數作廢了,一律回
    // DefaultExcelLanguage(＝client 語言)那份 ⇒ 下面四次不同語言的請求在台服拿到的是**同一份繁中資料**,
    // 四個欄位逐字相同(不是例外、不是空字串)。驗證工具:`~/.claude/tools/sqpack/langparam/`。
    // ⇒ 這裡**刻意不加台服專用分支**:國際服四語言仍各取各的,台服則四欄同為繁中,兩邊都正確。
    public static MultiString FromPlaceName(IDataManager gameData, uint id)
    {
        var en = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.English).GetRowOrDefault(id)?.Name);
        var de = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.German).GetRowOrDefault(id)?.Name);
        var fr = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.French).GetRowOrDefault(id)?.Name);
        var jp = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.Japanese).GetRowOrDefault(id)?.Name);
        return new MultiString(en, de, fr, jp);
    }

    public static MultiString FromItem(IDataManager gameData, uint id)
    {
        var en = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.English).GetRowOrDefault(id)?.Name);
        var de = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.German).GetRowOrDefault(id)?.Name);
        var fr = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.French).GetRowOrDefault(id)?.Name);
        var jp = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.Japanese).GetRowOrDefault(id)?.Name);
        return new MultiString(en, de, fr, jp);
    }

    private string Name(ClientLanguage lang)
        => lang switch
        {
            ClientLanguage.English  => English,
            ClientLanguage.German   => German,
            ClientLanguage.Japanese => Japanese,
            ClientLanguage.French   => French,

            // 台服(ClientLanguage.TraditionalChinese,值 7)在本結構沒有專屬欄位,但上面四個欄位
            // 在台服本來就都是繁中(見 FromPlaceName 上方的說明),取 English 欄位得到的
            // 就是繁中名稱。明寫這一條是為了不再靠 `_ =>` 兜底 —— 兜底路徑哪天被改動,
            // 台服會**靜默**取到別的東西,而不是報錯。
            ClientLanguage.TraditionalChinese => English,

            _ => English,
        };

    public static readonly MultiString Empty = new(string.Empty, string.Empty, string.Empty, string.Empty);
}
