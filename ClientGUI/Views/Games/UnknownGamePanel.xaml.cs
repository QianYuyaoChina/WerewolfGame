using System.Windows.Controls;

namespace ClientGUI.Views.Games;

/// <summary>兜底面板（本客户端不认识的游戏）。DataContext = UnknownGameViewModel。</summary>
public partial class UnknownGamePanel : UserControl
{
    public UnknownGamePanel()
    {
        InitializeComponent();
    }
}
