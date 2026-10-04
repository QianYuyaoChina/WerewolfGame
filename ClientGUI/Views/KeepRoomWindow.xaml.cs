using System.Windows;
using System.Windows.Input;

namespace ClientGUI.Views;

/// <summary>
/// 房主退出房间且房间内已无其他成员时的询问窗口：
/// 「保留房间」（<see cref="Window.DialogResult"/> = true）或「关闭房间」（false）。
/// 为了不误删房间，直接关闭窗口（X 或 Esc）按「保留房间」处理。
/// </summary>
public partial class KeepRoomWindow : Window
{
    public KeepRoomWindow()
    {
        InitializeComponent();
    }

    private void KeepRoom_OnClick(object sender, RoutedEventArgs e) => Finish(true);

    private void CloseRoom_OnClick(object sender, RoutedEventArgs e) => Finish(false);

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Finish(true);
    }

    private void Finish(bool keep)
    {
        DialogResult = keep;
        Close();
    }
}
