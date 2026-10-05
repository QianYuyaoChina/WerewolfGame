using System.Windows.Controls;

namespace ClientGUI.Views.Games;

/// <summary>狼人杀操作面板（纯绑定，无交互逻辑）。DataContext = WerewolfViewModel。</summary>
public partial class WerewolfPanel : UserControl
{
    public WerewolfPanel()
    {
        InitializeComponent();
    }
}
