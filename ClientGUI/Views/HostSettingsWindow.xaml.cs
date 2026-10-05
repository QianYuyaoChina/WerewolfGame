using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ClientGUI.Services;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Views;

/// <summary>
/// 房主设置窗口：最大人数 / 房间密码 / 身份布局 / 邀请码。
///
/// 只有房主可用（服务端对每个 op 都会再校验一次房主身份）。
/// 邀请码区展示的是**邀请令牌**换来的码：修改房间密码不会让它失效，
/// 只有点「重置邀请令牌」才会作废旧码。
/// </summary>
public partial class HostSettingsWindow : Window
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly GameClient _client;
    private readonly List<RoleCountRow> _rows = new();

    private HostSettingsDto? _settings;
    private string _inviteToken = "";
    private int _playerCount;

    public HostSettingsWindow(GameClient client)
    {
        InitializeComponent();

        _client = client;

        BuildRows();
        Loaded += async (_, _) => await ReloadAsync();
    }

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

    // ==================== 读取 / 回填 ====================

    private async Task ReloadAsync()
    {
        SetStatus("正在读取房间设置…", error: false);

        var result = await _client.GetHostSettingsAsync();
        if (!result.Ok || result.Settings == null)
        {
            SetStatus(string.IsNullOrEmpty(result.Message) ? "无法读取房间设置。" : result.Message, error: true);
            SaveButton.IsEnabled = false;
            return;
        }

        _settings = result.Settings;
        _inviteToken = result.Settings.InviteToken;
        _playerCount = result.Settings.PlayerCount;

        RoomText.Text = $"房间 {result.Settings.RoomId} · {result.Settings.RoomName} · " +
                        $"当前 {result.Settings.PlayerCount} 人";

        MaxPlayersInput.Text = result.Settings.MaxPlayers.ToString();
        MaxPlayersHint.Text = result.Settings.Running
            ? "游戏进行中，人数与身份配置暂不可修改（密码与邀请码仍可改）。"
            : $"允许范围：{result.Settings.MinPlayers} - {result.Settings.MaxPlayersLimit} 人。";

        ClearPasswordBox.IsChecked = false;
        PasswordInput.Password = "";

        ApplyLayoutFromSettings(result.Settings);
        RefreshInviteCode();

        SaveButton.IsEnabled = true;
        SetStatus("已读取房间设置。修改后点「保存设置」。", error: false);
    }

    private void ApplyLayoutFromSettings(HostSettingsDto settings)
    {
        // 房主自定义过就按自定义回填；否则显示「按当前人数推导的默认布局」，方便直接微调。
        var layout = ParseLayout(settings.GameSettingsJson);
        if (layout.Total == 0) layout = RoleLayout.Default(Math.Max(2, settings.PlayerCount));

        foreach (var row in _rows) row.SetCount(layout.Get(row.Role));
        UpdateLayoutHint();
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

    private void UpdateLayoutHint()
    {
        var layout = BuildLayout();
        var shape = layout.ValidateShape();
        var game = layout.ValidateForGame(_playerCount);

        LayoutHint.Text = shape.ok && game.ok
            ? $"共 {layout.Total} 个身份（当前 {_playerCount} 人）：{layout.Describe()}\n" +
              "校验通过。如果不想自定义，把数量改回默认值即可自动跟随人数。"
            : $"共 {layout.Total} 个身份（当前 {_playerCount} 人）。\n⚠️ {(game.ok ? shape.msg : game.msg)}";
    }

    private void RefreshInviteCode()
    {
        var code = _client.CreateInviteCode(_inviteToken) ?? "";
        InviteCodeBox.Text = code;
    }

    // ==================== 交互 ====================

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;

        // 人数
        if (!int.TryParse(MaxPlayersInput.Text.Trim(), out var maxPlayers))
        {
            SetStatus("最大人数必须是数字。", error: true);
            return;
        }

        // 身份布局：与「按人数推导的默认布局」一致时发空串，表示跟随默认。
        var layout = BuildLayout();
        var isDefault = SameLayout(layout, RoleLayout.Default(Math.Max(2, _playerCount)));
        var layoutJson = isDefault ? "" : JsonSerializer.Serialize(layout, Json);

        // 密码
        var clear = ClearPasswordBox.IsChecked == true;
        var newPassword = PasswordInput.Password;

        SaveButton.IsEnabled = false;
        SetStatus("正在保存…", error: false);

        var result = await _client.UpdateRoomSettingsAsync(maxPlayers, newPassword.Length > 0 ? newPassword : null,
                                                          clear, layoutJson);

        SaveButton.IsEnabled = true;

        if (!result.Ok)
        {
            SetStatus(string.IsNullOrEmpty(result.Message) ? "保存失败。" : result.Message, error: true);
            return;
        }

        SetStatus(clear
            ? "✅ 已保存：房间已改为公开房间（邀请码仍然有效）。"
            : newPassword.Length > 0
                ? "✅ 已保存。房间密码已更新——已发出的邀请码仍然有效。"
                : "✅ 已保存房间设置。", error: false);

        await ReloadAsync();
    }

    private void ResetLayout_OnClick(object sender, RoutedEventArgs e)
    {
        var layout = RoleLayout.Default(Math.Max(2, _playerCount));
        foreach (var row in _rows) row.SetCount(layout.Get(row.Role));
        UpdateLayoutHint();
        SetStatus("已填入默认布局，点「保存设置」生效。", error: false);
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

        var result = await _client.ResetInviteAsync();
        if (!result.Ok || result.Settings == null)
        {
            SetStatus(string.IsNullOrEmpty(result.Message) ? "重置失败。" : result.Message, error: true);
            return;
        }

        _inviteToken = result.Settings.InviteToken;
        RefreshInviteCode();
        SetStatus("✅ 已重置邀请令牌，旧邀请码已失效；上方是新的邀请码。", error: false);
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? UiBrushes.Warning : UiBrushes.Muted;
    }

    // ==================== 工具 ====================

    private static bool SameLayout(RoleLayout a, RoleLayout b)
    {
        foreach (var role in Roles.DisplayOrder)
            if (a.Get(role) != b.Get(role)) return false;
        return true;
    }
}
