using System.Windows.Controls;
using ClientGUI.ViewModels;

namespace ClientGUI.Views;

/// <summary>左栏：身份卡 + 玩家列表 + 房主开始按钮。</summary>
public partial class PlayerPanel : UserControl
{
    public PlayerPanel()
    {
        InitializeComponent();
    }

    /// <summary>列表只作为“点击目标”的入口，不保留选中状态。</summary>
    private void PlayerList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (list.SelectedItem is not PlayerRow row) return;

        list.SelectedIndex = -1;
        (DataContext as GameViewModel)?.SelectPlayer(row);
    }
}
