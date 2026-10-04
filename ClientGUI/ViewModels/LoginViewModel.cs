using ClientGUI.Services;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>
/// 登录窗口的视图模型：只负责“地址 + 账号 → 登录成功（账号校验）”；
/// 校验与网络细节都在 <see cref="GameClient.ConnectAndLoginAsync"/> 里。
/// 登录成功后把同一个 <see cref="GameClient"/> 交给房间列表窗口复用。
/// </summary>
public sealed class LoginViewModel : ObservableObject
{
    /// <summary>默认服务器地址（与服务端 Program.cs 的监听地址一致）。</summary>
    public const string DefaultServerUrl = "http://localhost:5000/gamehub";

    private readonly GameClient _client = new();

    /// <summary>UI 线程上下文：服务器回调来自后台线程，状态更新必须回到这里。</summary>
    private readonly SynchronizationContext? _ui;

    private string _serverUrl = DefaultServerUrl;
    private string _username = "";
    private string _password = "";
    private string _status = "填写服务器地址与账号后登录；登录后进入房间列表。";
    private string _inviteCode = "";
    private string _inviteHint = "";
    private InvitePayload? _pendingInvite;
    private bool _isBusy;
    private bool _isLoggedIn;
    private bool _loginFailed;

    public LoginViewModel()
    {
        _ui = SynchronizationContext.Current;

        // 回填上次填写的地址与用户名（不保存密码），省去每次重新输入。
        var saved = UiStateStore.State;
        if (!string.IsNullOrWhiteSpace(saved.ServerUrl)) _serverUrl = saved.ServerUrl;
        _username = saved.Username;

        // 连接过程中的阶段提示（“已连接服务器”“正在重连…”），来自 SignalR 后台线程。
        _client.StatusChanged += status => OnUi(() => Status = status);
        LoginCommand = new AsyncRelayCommand(_ => LoginAsync());
        UseInviteCommand = new RelayCommand(_ => ApplyInvite());
    }

    /// <summary>把回调切到 UI 线程执行。</summary>
    private void OnUi(Action action)
    {
        if (_ui == null) { action(); return; }
        _ui.Post(_ => action(), null);
    }

    /// <summary>登录成功后复用的会话（连接已建立、账号已校验）。</summary>
    public GameClient Session => _client;

    public AsyncRelayCommand LoginCommand { get; }

    /// <summary>解析邀请码（自动填好服务器地址，登录后直接进房）。</summary>
    public RelayCommand UseInviteCommand { get; }

    /// <summary>登录成功后触发，把会话交给房间列表窗口。</summary>
    public event Action<GameClient>? LoginSucceeded;

    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); }
    public string UsernameInput { get => _username; set => Set(ref _username, value); }
    public string PasswordInput { get => _password; set => Set(ref _password, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsBusy;

    public bool IsLoggedIn { get => _isLoggedIn; private set => Set(ref _isLoggedIn, value); }

    /// <summary>最近一次登录是否失败（用于在界面上把状态文案标红）。</summary>
    public bool ShowError { get => _loginFailed; private set => Set(ref _loginFailed, value); }

    // ==================== 邀请码 ====================

    /// <summary>邀请码输入（可选）：粘贴好友分享的邀请码后点「使用」。</summary>
    public string InviteCode { get => _inviteCode; set => Set(ref _inviteCode, value); }

    /// <summary>邀请码解析结果提示（识别成功 / 失败原因）。</summary>
    public string InviteHint
    {
        get => _inviteHint;
        private set
        {
            if (Set(ref _inviteHint, value)) OnPropertyChanged(nameof(HasInviteHint));
        }
    }

    public bool HasInviteHint => _inviteHint.Length > 0;

    /// <summary>已解析、待加入的房间（登录成功后自动进入，进入后清空）。</summary>
    public InvitePayload? PendingInvite => _pendingInvite;

    /// <summary>解析邀请码：自动填好服务器地址，并记住待加入的房间。</summary>
    private void ApplyInvite()
    {
        if (!GameClient.TryParseInvite(InviteCode, out var invite, out var error) || invite == null)
        {
            _pendingInvite = null;
            ShowError = true;
            InviteHint = "⚠️ " + error;
            return;
        }

        _pendingInvite = invite;
        ServerUrl = invite.Url;   // 邀请码里带了服务器地址
        ShowError = false;

        var name = string.IsNullOrWhiteSpace(invite.RoomName) ? "" : $"（{invite.RoomName}）";
        var lockText = invite.HasPassword ? "，房间密码已包含在邀请码中" : "";
        InviteHint = $"✅ 邀请码有效：登录后将进入房间 {invite.RoomId}{name}{lockText}。";
    }

    // ==================== 登录 ====================

    private async Task LoginAsync()
    {
        IsBusy = true;
        Status = "正在连接服务器…";
        try
        {
            var result = await _client.ConnectAndLoginAsync(ServerUrl, UsernameInput, PasswordInput);
            if (!result.Ok)
            {
                // 失败原因可能来自本地校验、服务器拒绝或连接异常，统一显示。
                ShowError = true;
                Status = result.Message;
                return;
            }

            // 使用了邀请码：登录成功后直接进入对应房间（房间密码由邀请码携带，无需再输）。
            if (_pendingInvite != null)
            {
                var rr = await _client.JoinByInviteAsync(_pendingInvite);
                if (!rr.Ok)
                {
                    ShowError = true;
                    Status = "进入房间失败：" + rr.Message;
                    return;   // 停在登录窗口，让用户修改邀请码或稍后重试
                }
                _pendingInvite = null;
            }

            ShowError = false;
            IsLoggedIn = true;
            Status = result.Message;

            // 登录成功才记住地址与用户名。
            UiStateStore.State.ServerUrl = ServerUrl;
            UiStateStore.State.Username = UsernameInput;
            UiStateStore.Save();

            LoginSucceeded?.Invoke(_client);
        }
        catch (Exception ex)
        {
            ShowError = true;
            Status = "无法连接：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>窗口关闭且未登录成功时，释放可能残留的连接。</summary>
    public async Task DisposeSessionAsync()
    {
        try { await _client.DisposeAsync(); } catch { /* 忽略关闭异常 */ }
    }
}
