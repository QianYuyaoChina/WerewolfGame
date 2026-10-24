using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClientGUI.Services;
using ClientGUI.ViewModels;

namespace ClientGUI.Views.Pages;

/// <summary>
/// 邀请码页（v2.6.0 S2 由 <c>InviteCodeWindow</c> 搬迁而来）：
/// 把邀请码放在**可选中**的只读文本框里展示，由用户点按钮再复制。
///
/// 入栈方式：房间页 / 房主设置页用 <see cref="NavigationService.PushOverlay"/> 打开，
/// 参数（要展示的码）通过 <see cref="PageRequest.Argument"/> 传入 —— 页面因此
/// **没有视图模型**，也就没有「状态与字符串不同步」这类问题。
///
/// 为什么不在进场时自动复制：见 XAML 顶部注释（剪贴板是全局独占资源，会卡住页面）。
/// </summary>
public partial class InviteCodePage : UserControl, IPage
{
    private readonly AppSession _session;

    /// <summary>要展示的邀请码（构造时一次性传入，本页只读）。</summary>
    private readonly string _code;

    public InviteCodePage(AppSession session, string code)
    {
        InitializeComponent();
        _session = session;

        _code = code ?? "";
        CodeBox.Text = _code;
        SetStatus("点【复制邀请码】按钮复制；也可以在这个框里按 Ctrl+A 全选、Ctrl+C 复制。", ok: true);
    }

    // ==================== IPage ====================

    public PageId Id => PageId.InviteCode;

    public string Title => "房间邀请码";

    /// <summary>浮层可以返回（Esc / 导航栏「返回」/ 底部「关闭」都走 BackAsync）。</summary>
    public bool CanGoBack => true;

    /// <summary>进场：把焦点与全选放到文本框上，用户随时能直接 Ctrl+C。</summary>
    public void OnEnter()
    {
        // 进场时页面可能还没进视觉树，用 Background 优先级排队，等布局完成后再抢焦点。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CodeBox.Focus();
            CodeBox.SelectAll();
        }), DispatcherPriority.Background);
    }

    /// <summary>退场：本页没有任何订阅与轮询，无可退订（保留方法以满足 IPage 契约）。</summary>
    public void OnLeave()
    {
    }

    /// <summary>返回拦截：没有未保存的改动，一律放行。</summary>
    public Task<bool> OnBackRequestedAsync() => Task.FromResult(true);

    // ==================== 交互 ====================

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

    /// <summary>导航栏「返回」：统一返回入口（N9）。</summary>
    private void Back_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();

    /// <summary>底部「关闭」：与「返回」同一条路径。</summary>
    private void Close_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();

    private void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = ok ? UiBrushes.Alive : UiBrushes.Warning;
    }
}
