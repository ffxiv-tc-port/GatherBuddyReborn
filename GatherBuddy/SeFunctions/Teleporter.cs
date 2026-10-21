using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Plugin;

namespace GatherBuddy.SeFunctions;

public static unsafe class Teleporter
{
    private static bool _unavailableLogged;

    /// <summary>傳送清單（Telepo）的位址與 <c>UpdateAetheryteList</c> 都解得出來；否則記一次 Warning 後回 false。</summary>
    /// <remarks>碰 <c>Telepo.Instance()</c> 之前一律先問這裡：位址解不出時 Instance() 會擲例外，載入路徑上＝整個插件載入失敗。</remarks>
    internal static bool IsTelepoAvailable()
    {
        if (Telepo.Addresses.Instance.Value != 0 && Telepo.MemberFunctionPointers.UpdateAetheryteList != null)
            return true;

        if (!_unavailableLogged)
        {
            _unavailableLogged = true;
            GatherBuddy.Log.Warning("遊戲改版後傳送清單（Telepo）的位址對不上：所有乙太之光一律當成沒開通，找最近的乙太之光與自動傳送停用；其餘功能照常。");
        }

        return false;
    }

    public static bool IsAttuned(uint aetheryte)
    {
        // 載入時就會走到（鬧鐘找最近的乙太之光）。
        if (!IsTelepoAvailable())
            return false;

        var teleport = Telepo.Instance();
        if (teleport == null)
        {
            GatherBuddy.Log.Error("Could not check attunement: Telepo is missing.");
            return false;
        }

        if (Dalamud.Objects.LocalPlayer == null)
            return true;
        teleport->UpdateAetheryteList();

        var endPtr = teleport->TeleportList.Last;
        for (var it = teleport->TeleportList.First; it != endPtr; ++it)
        {
            if (it->AetheryteId == aetheryte)
                return true;
        }

        return false;
    }

    public static bool Teleport(uint aetheryte)
    {
        if (IsAttuned(aetheryte))
        {
            Telepo.Instance()->Teleport(aetheryte, 0);
            return true;
        }

        Communicator.PrintError("Could not teleport to ",
            GatherBuddy.GameData.Aetherytes.TryGetValue(aetheryte, out var a) ? a.Name : "Unknown Aetheryte", GatherBuddy.Config.SeColorNames,
            " not attuned.");
        return false;
    }

    // Teleport without checking for attunement. Use at own risk.
    public static void TeleportUnchecked(uint aetheryte)
    {
        Telepo.Instance()->Teleport(aetheryte, 0);
    }
}
