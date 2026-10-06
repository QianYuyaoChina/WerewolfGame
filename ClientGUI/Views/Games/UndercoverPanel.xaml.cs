using System.Windows.Controls;

namespace ClientGUI.Views.Games;

/// <summary>
/// 谁是卧底的操作面板（装载在宿主 ActionPanel 的插槽里）。
/// 只负责呈现：所有玩法状态与操作都来自 <c>UndercoverViewModel</c>。
/// </summary>
public partial class UndercoverPanel : UserControl
{
    public UndercoverPanel() => InitializeComponent();
}
