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

    /// <summary>
    /// 由「内置服务器」页写入共享状态条（人话一行，绝不含异常堆栈）。
    /// 两个标签页共用底部状态条，因此这里开放一个入口给内置服务器页。
    /// </summary>
    public void SetSharedStatus(string text, bool isError = false)
    {
        Status = text;
        ShowError = isError;
    }

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
        var lockText = invite.HasToken ? "，房间密码已包含在邀请码中" : "";
        InviteHint = $"✅ 邀请码有效：登录后将进入房间 {invite.RoomId}{name}{lockText}。";
    }

    // ==================== 登录 ====================

    /// <summary>
    /// 用**指定地址**走与 Tab2 完全相同的登录流程（Tab1「启动并进入」复用它，PRD P0-4）。
    /// 差别只有一处：不把内置服务器的地址写进「上次使用的服务器地址」
    /// —— Tab2 记住的地址应当保持用户自己填的那个，不该被内置地址覆盖。
    /// </summary>
    public async Task LoginWithServerUrlAsync(string url)
    {
        ServerUrl = url;
        await LoginCoreAsync(url, UsernameInput, PasswordInput,
            persistUrl: false, rememberUsername: true, raiseSucceeded: true);
    }

    /// <summary>
    /// 用**显式凭据**登录（v2.5.0 第 6 条 · 一键开服用）。
    ///
    /// 与普通登录的两点差别（架构 K13 / PRD 6.6 / E-20）：
    /// <list type="number">
    ///   <item>**不写** <see cref="UsernameInput"/> / <see cref="PasswordInput"/> ——
    ///         用户手填的凭据一字不改（用的是参数里的凭据）；</item>
    ///   <item>**不触发** <see cref="LoginSucceeded"/> —— 一键开服要停在「已就绪」态，
    ///         由用户点「进入房间列表」再进（见 <see cref="EnterRoomList"/>）。</item>
    /// </list>
    /// </summary>
    /// <returns>是否登录成功。</returns>
    public async Task<bool> LoginWithCredentialsAsync(string url, string username, string password,
                                                      bool persistUrl = false)
        => await LoginCoreAsync(url, username, password,
            persistUrl, rememberUsername: false, raiseSucceeded: false);

    /// <summary>
    /// 「局域网地址优先、失败回落本机一次」的连接流程（架构 D7）——
    /// 抽到登录视图模型里，让「一键开服」与将来的其它入口共用同一份回落逻辑。
    /// 回落成功后仍要写 <c>InviteUrlOverride</c>（v2.4.0 的 E-18 语义不能丢）。
    /// </summary>
    /// <returns>是否连接并登录成功。</returns>
    public async Task<bool> ConnectWithFallbackAsync(string connectUrl, string localUrl, string lanUrl,
                                                     string username, string password)
    {
        if (await LoginWithCredentialsAsync(connectUrl, username, password)) return true;

        // 局域网地址与本机地址相同（没有网卡 / 本机就是局域网）时没有必要再试一次。
        if (string.Equals(localUrl, connectUrl, StringComparison.Ordinal)) return false;

        SetSharedStatus("局域网地址连不上，改用本机地址重试…");
        if (!await LoginWithCredentialsAsync(localUrl, username, password)) return false;

        // E-18：回落只改「我怎么连自己」，分享出去的邀请码仍必须是局域网地址 ——
        // 否则好友拿到码会连到 127.0.0.1（连到他自己电脑上）。
        Session.InviteUrlOverride = lanUrl;
        SetSharedStatus("已改用本机地址连接（分享给好友的邀请码仍是局域网地址）。");
        return true;
    }

    /// <summary>
    /// 把「已就绪的会话」交给上层（一键开服就绪后点「进入房间列表」走的这一条）。
    /// 未登录时什么都不做，避免把半成品会话放出去。
    /// </summary>
    public bool EnterRoomList()
    {
        if (!_client.IsLoggedIn) return false;

        LoginSucceeded?.Invoke(_client);
        return true;
    }

    /// <summary>
    /// 登录的**唯一**实现：地址 + 显式凭据 + 三个开关（是否记住地址 / 用户名，是否立刻把会话交出去）。
    /// 普通登录与一键开服的显式凭据登录都走这里，行为差别只由开关表达。
    /// </summary>
    private async Task<bool> LoginCoreAsync(string url, string username, string password,
                                            bool persistUrl, bool rememberUsername, bool raiseSucceeded)
    {
        IsBusy = true;

        // 同一时刻只连一个服务器（E-7）：Share 层的 ConnectAsync 会先断开旧连接，
        // 这里把「切换」明确说出来，避免用户以为还是连着原来那个。
        var switching = _client.IsConnected;
        Status = switching ? "已断开当前服务器，正在连接新的服务器…" : "正在连接服务器…";
        try
        {
            var result = await _client.ConnectAndLoginAsync(url, username, password);
            if (!result.Ok)
            {
                // 失败原因可能来自本地校验、服务器拒绝或连接异常，统一显示。
                ShowError = true;
                Status = result.Message;
                return false;
            }

            // 使用了邀请码：登录成功后直接进入对应房间（房间密码由邀请码携带，无需再输）。
            if (_pendingInvite != null)
            {
                var rr = await _client.JoinByInviteAsync(_pendingInvite);
                if (!rr.Ok)
                {
                    ShowError = true;
                    Status = "进入房间失败：" + rr.Message;
                    return false;   // 停在登录窗口，让用户修改邀请码或稍后重试
                }
                _pendingInvite = null;
            }

            ShowError = false;
            IsLoggedIn = true;
            Status = result.Message;

            // 登录成功才记住地址与用户名（显式凭据登录不覆盖用户手填的用户名）。
            if (persistUrl) UiStateStore.State.ServerUrl = url;
            if (rememberUsername)
            {
                UiStateStore.State.Username = username;
                UiStateStore.Save();
            }

            if (raiseSucceeded) LoginSucceeded?.Invoke(_client);
            return true;
        }
        catch (Exception ex)
        {
            ShowError = true;
            Status = "无法连接：" + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>普通登录（Tab2 的按钮 / 回车）：成功即把会话交给上层。</summary>
    private async Task LoginAsync(bool persistUrl = true)
        => await LoginCoreAsync(ServerUrl, UsernameInput, PasswordInput,
            persistUrl, rememberUsername: true, raiseSucceeded: true);

    /// <summary>窗口关闭且未登录成功时，释放可能残留的连接。</summary>
    public async Task DisposeSessionAsync()
    {
        try { await _client.DisposeAsync(); } catch { /* 忽略关闭异常 */ }
    }
}
