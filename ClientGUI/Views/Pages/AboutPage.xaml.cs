using System.Windows;
using System.Windows.Controls;
using ClientGUI.Services;

namespace ClientGUI.Views.Pages;

/// <summary>
/// 「关于」页（v2.6.0 S3 由 <c>AboutWindow</c> 搬迁而来）：
/// 产品信息、版本、技术栈与免责说明。
///
/// 入栈方式 **<see cref="NavigationService.Push"/>（换页）**，不是浮层：
/// 关于 / 更新日志是「离开房间去看文档」，返回即回到原页；
/// 浮层式入栈留给「盖在当前页上做一个决策」的四类页面（设置 / 房主设置 / 邀请码 / 保留房间）。
///
/// 页里「更新日志」按钮继续 <see cref="NavigationService.Push"/> 到 <see cref="ChangelogPage"/>，
/// 因此从房间页进来会形成「房间 → 关于 → 更新日志」三层栈，连按两次返回正好逐层退回。
/// </summary>
public partial class AboutPage : UserControl, IPage
{
    private readonly AppSession _session;

    public AboutPage(AppSession session)
    {
        InitializeComponent();
        _session = session;
    }

    // ==================== IPage ====================

    public PageId Id => PageId.About;

    public string Title => "关于";

    /// <summary>可以从关于页返回（Esc / 导航栏返回 / 底部「关闭」同一个出口）。</summary>
    public bool CanGoBack => true;

    /// <summary>本页是纯静态信息，进场不需要拉数据、也不需要订阅任何事件。</summary>
    public void OnEnter()
    {
    }

    /// <summary>没有订阅，无可退订（保留方法以满足 IPage 契约）。</summary>
    public void OnLeave()
    {
    }

    /// <summary>返回拦截：纯信息页，一律放行。</summary>
    public Task<bool> OnBackRequestedAsync() => Task.FromResult(true);

    // ==================== 交互 ====================

    /// <summary>「更新日志」：换页式入栈（返回时回到本页，而不是回到房间页）。</summary>
    private void OpenChangelog_OnClick(object sender, RoutedEventArgs e)
        => _ = _session.Navigation.Push(new PageRequest(PageId.Changelog));

    /// <summary>「关闭」= 返回上一页（不是关程序：不退出房间、不断连接、不停服务器）。</summary>
    private void Close_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();

    /// <summary>导航栏「返回」：与「关闭」同一条路径（N9 单一出口）。</summary>
    private void Back_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();
}
