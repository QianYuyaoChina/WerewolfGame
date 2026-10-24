using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Views.Pages;

/// <summary>
/// 房主设置页（v2.6.0 S2 由 <c>HostSettingsWindow</c> 搬迁而来）：
/// 最大人数 / 房间密码 / 身份布局 / 邀请码。只有房主可用（服务端对每个 op 都会再校验一次）。
///
/// 身份配置有两种模式（与旧窗口逐字一致）：
/// <list type="bullet">
///   <item><b>跟随人数自动配置</b>（默认）：按当前人数套用内置布局（神职随人数递增），
///         保存时下发「未自定义」，因此房间里每进/退一人，开局时的配置都会自动跟着变；</item>
///   <item><b>手动配置</b>：用 −/+ 或直接输入数量；任何手动改动都会自动切到该模式。</item>
/// </list>
///
/// 页面化带来的两处差别：
/// <list type="number">
///   <item><b>轮询随页面生死</b>：<c>OnEnter</c> 启动 3 秒轮询、<c>OnLeave</c> **必须**停掉
///         —— 页面会被反复进出，不停的话退场后仍在发请求（架构 §6.2）；</item>
///   <item><b>保存不再关窗</b>：保存成功后重新读一次服务端状态并留在本页，
///         由用户自己点「关闭」（= 出栈）。旧窗口是 <c>DialogResult = true</c> 后 <c>Close()</c>，
///         页面没有 <c>DialogResult</c>，硬要「保存即消失」反而会让用户以为保存失败。</item>
/// </list>
///
/// 轮询只做**非破坏性刷新**（<see cref="RefreshFromServerState"/>）：绝不覆盖房主正在编辑的
/// 最大人数 / 密码 / 勾选状态（v2.3.1 的回归点：早期版本每轮完整回填，改数字必须抢在 3 秒内点保存）。
/// </summary>
public partial class HostSettingsPage : UserControl, IPage, IPageTitleSource
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly AppSession _session;
    private readonly List<RoleCountRow> _rows = new();

    /// <summary>「🐺 狼人杀」这样的游戏标识：拼进 <see cref="Title"/>（T44，拿到房间设置后才有）。</summary>
    private string _gameLabel = "";

    /// <summary>页面在场期间轮询房间设置，用于跟随人数变化（进/退人时自动重排身份）。</summary>
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };

    private HostSettingsDto? _settings;
    private string _inviteToken = "";
    private int _playerCount;

    /// <summary>本房间的游戏是否有「身份 / 玩法配置」（没有的游戏隐藏身份配置标签页）。</summary>
    private bool _hasPlayerConfig = true;

    /// <summary>程序化回填期间抑制控件事件（避免把「回填」当成「用户改动」）。</summary>
    private bool _suppress;

    /// <summary>轮询处理器提成字段：<c>OnLeave</c> 能成对退订（匿名 lambda 退不掉）。</summary>
    private EventHandler? _onPollTick;

    public HostSettingsPage(AppSession session)
    {
        InitializeComponent();
        _session = session;

        BuildRows();

        // 默认「跟随人数自动配置」。勾选状态在 InitializeComponent 之后再设置：
        // 写在 XAML 里会在控件树尚未构建完成时触发 Checked，导致空引用。
        _suppress = true;
        AutoLayoutBox.IsChecked = true;
        _suppress = false;

        _onPollTick = async (_, _) => await PollAsync();
        _poll.Tick += _onPollTick;
    }

    // ==================== IPage ====================

    public PageId Id => PageId.HostSettings;

    /// <summary>
    /// 导航标题（同时拼进外壳窗口标题）。
    /// v2.6.0 S4（待办 T44）：拿到房间所属游戏后变成「房主设置 · 🐺 狼人杀」，
    /// 与旧窗口的 <c>Title = "房主设置 · 🐺 狼人杀"</c> 对齐；
    /// 还没拿到游戏信息时只有「房主设置」（不显示半截内容）。
    /// </summary>
    public string Title => _gameLabel.Length == 0 ? "房主设置" : $"房主设置 · {_gameLabel}";

    /// <summary>标题变化通知（外壳据此重算窗口标题，见 <see cref="IPageTitleSource"/>）。</summary>
    public event Action? TitleChanged;

    /// <summary>浮层可以返回（Esc / 返回 / 底部「关闭」都走同一个入口）。</summary>
    public bool CanGoBack => true;

    /// <summary>进场：读一次房间设置并启动 3 秒轮询（人数变了要跟着重排身份）。</summary>
    public void OnEnter()
    {
        _ = ReloadAsync();
        _poll.Start();
    }

    /// <summary>退场：**必须停轮询**（页面反复进出，不停就会在退场后继续发请求）。</summary>
    public void OnLeave()
    {
        _poll.Stop();
    }

    /// <summary>返回拦截：改动未保存时不拦（与旧窗口一致 —— 关窗即丢弃），一律放行。</summary>
    public Task<bool> OnBackRequestedAsync() => Task.FromResult(true);

    /// <summary>本页唯一认识的连接（来自会话，不自己 new）。</summary>
    private GameClient Client => _session.Client;

    /// <summary>是否处于「跟随人数自动配置」模式（复选框默认勾选）。</summary>
    private bool AutoLayout => AutoLayoutBox.IsChecked != false;

    /// <summary>按统一的展示顺序创建「身份 + 数量」输入行。</summary>
    private void BuildRows()
    {
        foreach (var role in Roles.DisplayOrder)
        {
            _rows.Add(new RoleCountRow
            {
                Role = role,
                Display = $"{Roles.Emoji(role)} {role}"
            });
        }
        LayoutList.ItemsSource = _rows;
    }

    /// <summary>账号 / 昵称等会话信息（旧窗口标题栏显示游戏名，页面里放到导航栏右槽）。</summary>

    // ==================== 读取 / 回填 ====================

    private async Task ReloadAsync()
    {
        SetStatus("正在读取房间设置…", error: false);

        HostSettingsResult result;
        try
        {
            result = await Client.GetHostSettingsAsync();
        }
        catch (Exception ex)
        {
            SetStatus("无法读取房间设置：" + ex.Message, error: true);
            PrimaryAction.IsEnabled = false;
            return;
        }

        if (!result.Ok || result.Settings == null)
        {
            SetStatus(string.IsNullOrEmpty(result.Message) ? "无法读取房间设置。" : result.Message, error: true);
            PrimaryAction.IsEnabled = false;
            return;
        }

        ApplySettings(result.Settings);
        PrimaryAction.IsEnabled = true;
        SetStatus("已读取房间设置。修改后点「保存设置」。", error: false);
    }

    /// <summary>
    /// 按房间绑定的游戏刷新页面标识与标签页可见性：
    /// 没有身份配置的玩法（如谁是卧底）**隐藏**「身份配置」页，避免显示狼人杀的身份表。
    /// </summary>
    private void ApplyGameIdentity(HostSettingsDto s)
    {
        _hasPlayerConfig = s.HasPlayerConfig;

        var emoji = string.IsNullOrEmpty(s.GameEmoji) ? "🎮" : s.GameEmoji;
        var name = string.IsNullOrEmpty(s.GameName) ? s.GameId : s.GameName;

        GameEmojiText.Text = emoji;
        // 房间带了游戏主题色就按主题色描边；否则保持 XAML 里那个来自主题的兜底色。
        if (TryParseColor(s.GameAccent, out var accent)) GameBadgeBrush.Color = accent;
        GameBadge.ToolTip = $"本房间的游戏：{emoji} {name}";

        // 导航栏右槽保留游戏名（标题栏之外的第二处可见位置，窄窗口也读得到）。
        GameNameText.Text = $"{emoji} {name}";

        // T44：窗口标题跟着内容走 —— 名字变了才发通知（轮询每 3 秒来一次，别每次都惊动外壳）。
        var label = $"{emoji} {name}";
        if (_gameLabel != label)
        {
            _gameLabel = label;
            TitleChanged?.Invoke();
        }

        LayoutTab.Visibility = _hasPlayerConfig ? Visibility.Visible : Visibility.Collapsed;
        if (!_hasPlayerConfig && ReferenceEquals(Tabs.SelectedItem, LayoutTab))
            Tabs.SelectedIndex = 0;
    }

    /// <summary>
    /// 十六进制颜色串 → <see cref="System.Windows.Media.Color"/>；空或非法返回 false（此时保持控件原有的主题色）。
    /// </summary>
    private static bool TryParseColor(string? hex, out System.Windows.Media.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        try
        {
            color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 完整回填（**只用于首次加载与保存之后**）：会覆盖输入框、密码框与勾选状态。
    /// 轮询路径不要用这个 —— 见 <see cref="RefreshFromServerState"/>。
    /// </summary>
    private void ApplySettings(HostSettingsDto settings)
    {
        _settings = settings;
        _playerCount = settings.PlayerCount;

        if (_inviteToken != settings.InviteToken)
        {
            _inviteToken = settings.InviteToken;
            RefreshInviteCode();
        }

        RoomText.Text = HeaderText(settings);
        MaxPlayersInput.Text = settings.MaxPlayers.ToString();
        MaxPlayersHint.Text = MaxPlayersTip(settings);

        ApplyGameIdentity(settings);   // 游戏标识 + 是否显示「身份配置」页

        ClearPasswordBox.IsChecked = false;
        PasswordInput.Password = "";

        // 房主自定义过 → 手动模式并回填；否则跟随人数自动配置。
        _suppress = true;
        AutoLayoutBox.IsChecked = !settings.CustomGameSettings;
        _suppress = false;

        if (settings.CustomGameSettings)
        {
            var layout = ParseLayout(settings.GameSettingsJson);
            SetRows(layout.Total == 0 ? RoleLayout.Default(settings.PlayerCount) : layout);
        }
        else
        {
            SetRows(RoleLayout.Default(settings.PlayerCount));
        }
    }

    /// <summary>
    /// **只刷新只读展示**（房间信息 / 邀请码 / 人数变化引起的自动重排），
    /// 绝不覆盖房主正在编辑的输入框（最大人数、密码）与勾选状态。
    /// </summary>
    public void RefreshFromServerState(HostSettingsDto settings)
    {
        _settings = settings;

        // 游戏标识 / 标签页可见性（跟随人数变化重排时顺带刷新一次）。
        ApplyGameIdentity(settings);

        // 邀请令牌可能被别处重置：只在真的变了才刷新（避免打断输入框的选中）。
        if (_inviteToken != settings.InviteToken)
        {
            _inviteToken = settings.InviteToken;
            RefreshInviteCode();
        }

        RoomText.Text = HeaderText(settings);
        MaxPlayersHint.Text = MaxPlayersTip(settings);

        var countChanged = settings.PlayerCount != _playerCount;
        _playerCount = settings.PlayerCount;

        if (countChanged)
        {
            // 人数变化：自动模式跟着重排；手动模式只提示（不覆盖房主的自定义）。
            if (AutoLayout) SetRows(RoleLayout.Default(_playerCount));
            UpdateLayoutHint();
        }
    }

    private static string HeaderText(HostSettingsDto s)
        => $"房间 {s.RoomId} · {s.RoomName} · 当前 {s.PlayerCount} 人";

    private static string MaxPlayersTip(HostSettingsDto s)
        => s.Running
            ? "游戏进行中，人数与身份配置暂不可修改（密码与邀请码仍可改）。"
            : $"允许范围：{s.MinPlayers} - {s.MaxPlayersLimit} 人。";

    /// <summary>页面在场期间轮询：人数变化时（自动模式下）实时重排身份配置。</summary>
    private async Task PollAsync()
    {
        if (_settings == null) return;

        HostSettingsResult result;
        try { result = await Client.GetHostSettingsAsync(); }
        catch { return; }

        if (!result.Ok || result.Settings == null) return;

        var before = _playerCount;
        // 注意：这里**只做非破坏性刷新**，不碰输入框（见 RefreshFromServerState 的注释）。
        RefreshFromServerState(result.Settings);

        if (_playerCount == before) return;

        SetStatus(AutoLayout
            ? $"人数变为 {_playerCount}，已按人数自动重排身份配置。"
            : $"人数变为 {_playerCount}，身份总数需与其一致才能开局。", error: !AutoLayout);
    }

    private static RoleLayout ParseLayout(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new RoleLayout();
        try { return JsonSerializer.Deserialize<RoleLayout>(json, Json) ?? new RoleLayout(); }
        catch { return new RoleLayout(); }
    }

    private RoleLayout BuildLayout()
    {
        var layout = new RoleLayout();
        foreach (var row in _rows) layout.Set(row.Role, row.Count);
        return layout;
    }

    /// <summary>回填各身份数量（抑制事件，避免被当成手动改动）。</summary>
    private void SetRows(RoleLayout layout)
    {
        _suppress = true;
        foreach (var row in _rows) row.SetCount(layout.Get(row.Role));
        _suppress = false;
        UpdateLayoutHint();
    }

    private void UpdateLayoutHint()
    {
        if (LayoutHint == null) return;   // 控件树尚未构建完成（InitializeComponent 期间）

        var layout = BuildLayout();
        var shape = layout.ValidateShape();
        var game = layout.ValidateForGame(_playerCount);

        var mode = AutoLayout ? "跟随人数自动配置" : "手动配置";
        LayoutHint.Text = shape.ok && game.ok
            ? $"（{mode}）共 {layout.Total} 个身份，当前 {_playerCount} 人：{layout.Describe()}"
            : $"（{mode}）共 {layout.Total} 个身份，当前 {_playerCount} 人。\n⚠️ {(game.ok ? shape.msg : game.msg)}";
    }

    private void RefreshInviteCode()
    {
        var code = Client.CreateInviteCode(_inviteToken) ?? "";
        InviteCodeBox.Text = code;
    }

    // ==================== 身份配置交互 ====================

    private void AutoLayout_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;

        if (AutoLayout)
        {
            SetRows(RoleLayout.Default(_playerCount));
            SetStatus("已切换为「跟随人数自动配置」：房间里每进/退一人，开局时的身份配置会自动跟着变。", error: false);
        }
        else
        {
            UpdateLayoutHint();
            SetStatus("已切换为手动配置：可以用 −/+ 或直接输入数量。", error: false);
        }
    }

    private void Decrease_OnClick(object sender, RoutedEventArgs e) => Nudge(sender, -1);

    private void Increase_OnClick(object sender, RoutedEventArgs e) => Nudge(sender, +1);

    /// <summary>加减某个身份的数量；手动改动会切换到手动模式。</summary>
    private void Nudge(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.Tag is not RoleCountRow row) return;

        SwitchToManual();
        row.SetCount(Math.Max(0, row.Count + delta));
        UpdateLayoutHint();
    }

    /// <summary>手动输入数量：清掉自动模式（否则下次轮询会把输入覆盖掉）。</summary>
    private void Count_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        SwitchToManual();
        UpdateLayoutHint();
    }

    private void SwitchToManual()
    {
        if (!AutoLayout) return;

        _suppress = true;
        AutoLayoutBox.IsChecked = false;
        _suppress = false;
        SetStatus("已切换为手动配置（取消勾选「跟随人数自动配置」）。", error: false);
    }

    private void ResetLayout_OnClick(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        AutoLayoutBox.IsChecked = true;
        _suppress = false;

        SetRows(RoleLayout.Default(_playerCount));
        SetStatus($"已填入 {_playerCount} 人的默认布局（跟随人数自动配置），点「保存设置」生效。", error: false);
    }

    // ==================== 保存 / 邀请码 ====================

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;

        if (!int.TryParse(MaxPlayersInput.Text.Trim(), out var maxPlayers))
        {
            SetStatus("最大人数必须是数字。", error: true);
            return;
        }

        // 自动模式下发空串（= 未自定义），服务端会按开局时的实际人数推导布局，
        // 因此人数之后再变也不会失配；手动模式才下发具体的身份配比。
        // 没有身份配置的玩法（如谁是卧底）下发 null = 「不动这一项」，不会把狼人杀的身份表写进去。
        string? layoutJson;
        if (!_hasPlayerConfig) layoutJson = null;
        else layoutJson = AutoLayout ? "" : JsonSerializer.Serialize(BuildLayout(), Json);

        var clear = ClearPasswordBox.IsChecked == true;
        var newPassword = PasswordInput.Password;

        PrimaryAction.IsEnabled = false;
        SetStatus("正在保存…", error: false);

        RoomResult result;
        try
        {
            result = await Client.UpdateRoomSettingsAsync(maxPlayers,
                newPassword.Length > 0 ? newPassword : null, clear, layoutJson);
        }
        catch (Exception ex)
        {
            PrimaryAction.IsEnabled = true;
            SetStatus("保存失败：" + ex.Message, error: true);
            return;
        }

        if (!result.Ok)
        {
            PrimaryAction.IsEnabled = true;
            SetStatus(string.IsNullOrEmpty(result.Message) ? "保存失败。" : result.Message, error: true);
            return;
        }

        // 保存成功：留在本页（页面没有 DialogResult），重读一次服务端状态把输入框对齐真值。
        await ReloadAsync();
        SetStatus("✅ 已保存房间设置。", error: false);
    }

    private void CopyInvite_OnClick(object sender, RoutedEventArgs e)
    {
        InviteCodeBox.SelectAll();
        var ok = ClipboardHelper.TrySetText(InviteCodeBox.Text);
        SetStatus(ok
            ? "✅ 邀请码已复制到剪贴板。"
            : "⚠️ 复制失败（剪贴板被占用）。请在上方框内 Ctrl+A 全选、Ctrl+C 复制。", error: !ok);
    }

    private async void ResetInvite_OnClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "重置后，之前发出去的所有邀请码都会立即失效（房间密码与人数不受影响）。\n\n确定继续吗？",
            "重置邀请令牌", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        HostSettingsResult result;
        try
        {
            result = await Client.ResetInviteAsync();
        }
        catch (Exception ex)
        {
            SetStatus("重置失败：" + ex.Message, error: true);
            return;
        }

        if (!result.Ok || result.Settings == null)
        {
            SetStatus(string.IsNullOrEmpty(result.Message) ? "重置失败。" : result.Message, error: true);
            return;
        }

        _inviteToken = result.Settings.InviteToken;
        RefreshInviteCode();
        SetStatus("✅ 已重置邀请令牌，旧邀请码已失效；上方是新的邀请码。", error: false);
    }

    /// <summary>导航栏「返回」：统一返回入口（N9）。</summary>
    private void Back_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();

    /// <summary>底部「关闭」：与「返回」同一条路径（不退出房间、不停服务器）。</summary>
    private void Close_OnClick(object sender, RoutedEventArgs e) => _ = _session.Navigation.BackAsync();

    private void SetStatus(string text, bool error)
    {
        if (StatusText == null) return;   // 控件树尚未构建完成（InitializeComponent 期间）
        StatusText.Text = text;
        StatusText.Foreground = error ? UiBrushes.Warning : UiBrushes.Muted;
    }
}
