using System.Windows;
using System.Windows.Controls;
using ClientGUI.Services;

namespace ClientGUI.Views.Pages;

/// <summary>
/// 「是否保留房间」询问页（v2.6.0 S3 由 <c>KeepRoomWindow</c> 搬迁而来）。
///
/// 出口，语义与旧窗口**逐字一致**（I6，也是待办 T35 的底线）：
/// <list type="bullet">
///   <item>「保留房间」→ <see cref="NavigationService.Complete"/>(<c>true</c>)；</item>
///   <item>「关闭房间」→ <see cref="NavigationService.Complete"/>(<c>false</c>)；</item>
///   <item>导航栏「返回」→ 同样是 <c>true</c>（见 <c>KeepRoom_OnClick</c> 共用的 <c>Finish</c>）；</item>
///   <item>Esc / Alt+← / 外壳点 ×（= 没显式选）→ 直接出栈，结果为 <c>null</c>
///         → 调用方走 <see cref="KeepRoomDecision.ResolveChoice"/>，<c>null</c> ⇒ **保留**。</item>
///   <item>「取消」（v2.6.0 S8）→ <see cref="KeepRoomChoice.Cancel"/>：**连退房都不做**，回到房间里继续待着。</item>
/// </list>
/// 前四条出口落到调用方那里都是「保留」，所以「随手关掉询问不该把房间关掉」这条底线在两种实现路径上同时成立。
///
/// 判定**不在本页**做：本页只负责「交回用户的选择（或没选）」，把结果翻译成「保留 / 关闭 / 取消」的活
/// 统一留在 <see cref="KeepRoomDecision.ResolveChoice"/> 那一处 —— 页面化不允许出现第二个判定点。
///
/// 入栈方式是浮层（<see cref="NavigationService.PushForDecisionAsync"/>）：
/// 下层房间页必须一直活着，因为「退房」正是它的视图模型在发起（T41 的原始事故形态）。
/// </summary>
public partial class KeepRoomPage : UserControl, IPage
{
    private readonly AppSession _session;

    public KeepRoomPage(AppSession session)
    {
        InitializeComponent();
        _session = session;
    }

    // ==================== IPage ====================

    public PageId Id => PageId.KeepRoom;

    public string Title => "是否保留房间";

    /// <summary>
    /// 允许返回 —— 但「返回」的含义是**保留房间**（<c>null</c> ⇒ 保留）。
    /// 因此本页的 <see cref="OnBackRequestedAsync"/> 一律放行，把「没选」这个事实交给判定函数。
    /// </summary>
    public bool CanGoBack => true;

    /// <summary>纯询问页：进场不需要拉数据，也不订阅事件。</summary>
    public void OnEnter()
    {
    }

    /// <summary>没有订阅，无可退订。</summary>
    public void OnLeave()
    {
    }

    /// <summary>
    /// 返回拦截：放行。**不要**在这里 <c>Complete(true)</c> ——
    /// 「返回 = 保留」的翻译由 <see cref="KeepRoomDecision.Resolve"/> 统一负责，
    /// 本页若自己翻译一遍，就等于出现了第二个判定点。
    /// </summary>
    public Task<bool> OnBackRequestedAsync() => Task.FromResult(true);

    // ==================== 交互 ====================

    private void KeepRoom_OnClick(object sender, RoutedEventArgs e) => Finish(true);

    private void CloseRoom_OnClick(object sender, RoutedEventArgs e) => Finish(false);

    /// <summary>
    /// 「取消」（v2.6.0 S8）：**不退出房间**，回到房间里继续待着。
    ///
    /// 它不是「保留房间」的同义词：保留 = 退房 + 房间留下；取消 = 连退房都不做。
    /// 因此它必须交回 <see cref="KeepRoomChoice.Cancel"/> —— 用 <c>false</c> 会把房间关掉，
    /// 用 <c>null</c> 会被 I6 判成「保留」而照样退房，两个都是错的。
    /// </summary>
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => Finish(KeepRoomChoice.Cancel);

    /// <summary>导航栏「返回」= 保留（与底部主按钮等价）：交回 <c>true</c>。</summary>
    private void Back_OnClick(object sender, RoutedEventArgs e) => Finish(true);

    /// <summary>
    /// 交回选择并出栈。栈由 <see cref="NavigationService.Complete"/> 负责弹，本页**不自己**调 <c>BackAsync</c>
    ///（否则会把两层一起弹掉）。若没有等待者（本页被普通 Push 打开，不该发生），退回普通返回兜底。
    ///
    /// 结果的三种形态（<c>true</c> / <c>false</c> / <see cref="KeepRoomChoice.Cancel"/>）都**原样**交上去，
    /// 翻译成「保留 / 关闭 / 取消」的活留给 <see cref="KeepRoomDecision"/> —— 本页不做判定。
    /// </summary>
    private void Finish(object? result)
    {
        if (_session.Navigation.Complete(result)) return;
        _ = _session.Navigation.BackAsync();
    }
}
