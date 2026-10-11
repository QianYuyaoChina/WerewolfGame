using System.Windows;
using System.Windows.Controls;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Views;

/// <summary>
/// 「服务器设置」浮层的正文（v2.5.0 第 5 条 · 仅管理员）：资源清单 + 全文编辑 + 整份保存。
///
/// 为什么是 <see cref="UserControl"/> 而不是 Window：新增 Window 子类会扩大
/// <c>ClientGUI.Tests</c> 的窗口结构契约（S1）；浮层正文塞进 <c>OverlayDialog</c> 即可（K11）。
///
/// 职责：把「选中资源 → 载入全文 → 编辑 → 保存」的界面事件接到 <see cref="ServerSettingsViewModel"/> 上。
/// 真正的清单 / 全文 / 保存请求都由视图模型转发给服务端；服务端是唯一权威（含格式校验与条数）。
/// </summary>
public partial class ServerSettingsDialog : UserControl
{
    /// <summary>本浮层的视图模型（宿主浮层的「保存」按钮调 <see cref="ServerSettingsViewModel.SaveAsync"/>）。</summary>
    public ServerSettingsViewModel ViewModel { get; } = new();

    /// <summary>当前会话（由宿主在打开浮层时注入）。选择变化时用它去取全文。</summary>
    private GameClient? _client;

    /// <summary>载入过程中屏蔽选择变化事件，避免「设置选中项」这一步触发重复取全文。</summary>
    private bool _suppressSelection;

    public ServerSettingsDialog()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    /// <summary>
    /// 打开浮层时调用：拉取清单，并自动载入第一份资源的全文。
    /// </summary>
    /// <param name="client">已登录并（按约定）具备管理员身份的会话。</param>
    public async Task LoadAsync(GameClient client)
    {
        _client = client;

        _suppressSelection = true;
        try { await ViewModel.LoadAsync(client); }
        finally { _suppressSelection = false; }

        // 清单非空时视图模型已自动选中第一项；这里把它的全文载进来。
        if (ViewModel.SelectedItem != null) await ViewModel.LoadSelectedAsync(client);
    }

    /// <summary>整份提交当前内容（宿主浮层的「保存」按钮调用）。</summary>
    public async Task<bool> SaveAsync(GameClient client) => await ViewModel.SaveAsync(client);

    /// <summary>清空（每次打开浮层时调用，避免看到上一次的残留）。</summary>
    public void Reset() => ViewModel.Reset();

    /// <summary>切换资源：把被选中资源的全文取回来（仅用户主动切换时触发）。</summary>
    private async void ResourceList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _client == null) return;
        await ViewModel.LoadSelectedAsync(_client);
    }
}
