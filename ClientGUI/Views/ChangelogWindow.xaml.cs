using System.Windows;

namespace ClientGUI.Views;

/// <summary>「更新日志」窗口：按版本倒序展示改动要点。</summary>
public partial class ChangelogWindow : Window
{
    public ChangelogWindow()
    {
        InitializeComponent();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
