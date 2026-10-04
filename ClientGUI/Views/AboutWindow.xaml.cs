using System.Windows;

namespace ClientGUI.Views;

/// <summary>「关于」窗口：产品信息、版本与技术栈。</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    /// <summary>从关于窗口直接查看更新日志（以本窗口为父，保持居中）。</summary>
    private void OpenChangelog_OnClick(object sender, RoutedEventArgs e)
        => new ChangelogWindow { Owner = Owner ?? this }.ShowDialog();

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
