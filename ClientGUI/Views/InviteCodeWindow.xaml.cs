using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ClientGUI.Services;
using ClientGUI.ViewModels;

namespace ClientGUI.Views;

/// <summary>
/// 邀请码窗口：把邀请码放在**可选中**的只读文本框里展示，由用户点按钮再复制。
///
/// 为什么不在打开时自动复制：Windows 剪贴板是全局独占资源，被其它程序占用时
/// <c>Clipboard.SetDataObject</c> 内部会重试（最多约 1 秒），若再叠加外层重试，
/// 窗口会卡住好几秒才显示。因此打开只负责「显示 + 全选」，
/// 复制交给用户主动触发（那时短暂等待可接受），失败时提示手动复制。
/// </summary>
public partial class InviteCodeWindow : Window
{
    private readonly string _code;

    public InviteCodeWindow(string code)
    {
        InitializeComponent();

        _code = code ?? "";
        CodeBox.Text = _code;
        SetStatus("点【复制邀请码】按钮复制；也可以在这个框里按 Ctrl+A 全选、Ctrl+C 复制。", ok: true);

        Loaded += (_, _) =>
        {
            // 打开就把焦点与选中放在文本框上：用户随时能直接 Ctrl+C。
            CodeBox.Focus();
            CodeBox.SelectAll();
        };
    }

    private void Copy_OnClick(object sender, RoutedEventArgs e)
    {
        CodeBox.SelectAll();

        if (_code.Length == 0)
        {
            SetStatus("⚠️ 邀请码为空。", ok: false);
            return;
        }

        // 先让「正在复制…」渲染出来，再做同步的剪贴板写入（避免看起来像卡死）。
        SetStatus("正在复制…", ok: true);
        Cursor = Cursors.Wait;
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        var copied = ClipboardHelper.TrySetText(_code, attempts: 2);
        Cursor = Cursors.Arrow;

        SetStatus(copied
            ? "✅ 已复制到剪贴板，直接粘贴给好友即可。"
            : "⚠️ 复制失败（剪贴板被其他程序占用）。请按 Ctrl+A 全选，再按 Ctrl+C 复制。",
            ok: copied);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = ok ? UiBrushes.Alive : UiBrushes.Warning;
    }
}
