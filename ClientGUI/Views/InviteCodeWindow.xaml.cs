using System.Windows;
using System.Windows.Input;
using ClientGUI.Services;
using ClientGUI.ViewModels;

namespace ClientGUI.Views;

/// <summary>
/// 邀请码窗口：把邀请码放在**可选中**的只读文本框里展示，并尝试自动复制到剪贴板。
/// 用独立窗口而非弹窗（MessageBox），是因为弹窗里的文字无法用鼠标选中——
/// 一旦剪贴板复制失败，用户就没法把邀请码取出去了。
/// 复制本身带重试：剪贴板常被其他程序短暂占用（CLIPBRD_E_CANT_OPEN）。
/// </summary>
public partial class InviteCodeWindow : Window
{
    private readonly string _code;

    public InviteCodeWindow(string code)
    {
        InitializeComponent();

        _code = code ?? "";
        CodeBox.Text = _code;

        Loaded += (_, _) =>
        {
            // 打开就把焦点与选中放在文本框上：即使自动复制失败，用户也能直接 Ctrl+C。
            CodeBox.Focus();
            CodeBox.SelectAll();
            TryCopy(auto: true);
        };
    }

    private void Copy_OnClick(object sender, RoutedEventArgs e)
    {
        CodeBox.SelectAll();
        TryCopy(auto: false);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    /// <summary>尽力复制到剪贴板；重试后仍失败时提示用户手动复制（文本框可选中）。</summary>
    private void TryCopy(bool auto)
    {
        if (_code.Length == 0)
        {
            SetStatus("⚠️ 邀请码为空。", ok: false);
            return;
        }

        if (ClipboardHelper.TrySetText(_code))
        {
            SetStatus(auto ? "✅ 已自动复制到剪贴板，直接粘贴给好友即可。" : "✅ 已复制到剪贴板。", ok: true);
            return;
        }

        SetStatus("⚠️ 复制失败（剪贴板被其他程序占用）。请在上方框内按 Ctrl+A 全选，再按 Ctrl+C 复制。", ok: false);
    }

    private void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = ok ? UiBrushes.Alive : UiBrushes.Warning;
    }
}
