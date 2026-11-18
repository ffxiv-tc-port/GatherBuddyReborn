using Dalamud.Bindings.ImGui;

namespace GatherBuddy.Gui;

/// <summary>
/// ImGui 拖放的呼叫前條件。
/// BeginDragDropSource 遇到「上一個項目沒有 ID」（例如 EndGroup 時群組裡沒有作用中的控件）、
/// 又沒帶 SourceAllowNullID 時，只會 IM_ASSERT 然後回 false；先判掉＝結果相同、不觸發斷言。
/// </summary>
public static class DragDropGuard
{
    public static bool LastItemHasId()
        => ImGuiP.GetItemID() != 0;
}
