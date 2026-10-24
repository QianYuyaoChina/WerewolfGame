using System.Windows;
using System.Windows.Controls;
using ClientGUI.Services;

namespace ClientGUI.Views.Pages;

/// <summary>
/// 「更新日志」页（v2.6.0 S3 由 <c>ChangelogWindow</c> 搬迁而来）。
///
/// 内容全部来自 <c>ClientGUI.Models.Changelog.All</c>（XAML 里 <c>{x:Static}</c> 直接绑静态列表）：
/// 顺序即「版本倒序」，最新一条带强调色徽章与「最新」标记。
///
/// 入栈方式 **<see cref="NavigationService.Push"/>（换页）**：从房间页或关于页进来，
/// 「返回」逐层退回，不会破坏下层页的生命周期契约。
/// </summary>
public partial class ChangelogPage : UserControl, IPage
{
    private readonly AppSession _session;

    public ChangelogPage(AppSession session)
    {
        InitializeComponent();
        _session = session;
    }

    // ==================== IPage ====================

    public PageId Id => PageId.Changelog;

    public string Title => "更新日志";

    public bool CanGoBack => true;

    /// <summary>纯静态内容，进场不需要拉数据或订阅事件。</summary>
    public void OnEnter()
    {
    }

    /// <summary>没有订阅，无可退订。</summary>
    public void OnLeave()
    {
    }

    /// <summary>返回拦截：纯信息页，一律放行。</summary>
    public Task<bool> OnBackRequestedAsync() => Task.FromResult(true);

    // ==================== 交互 ====================

    /// <summary>「关闭」= 返回上一页（不退出房间、不断连接、不停服务器）。</summary>
    private void Close_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();

    /// <summary>导航栏「返回」：与「关闭」同一条路径（N9 单一出口）。</summary>
    private void Back_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();
}
