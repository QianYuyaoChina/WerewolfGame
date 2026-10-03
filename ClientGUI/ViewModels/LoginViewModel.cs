using ClientGUI.Services;

namespace ClientGUI.ViewModels;

/// <summary>
/// 登录窗口的视图模型：只负责“地址 + 账号 → 登录成功”，
/// 校验与网络细节都在 <see cref="GameClient.ConnectAndJoinAsync"/> 里。
/// 登录成功后把同一个 <see cref="GameClient"/> 交给主窗口复用。
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
    private string _status = "填写服务器地址与账号后登录；首次登录即注册。";
    private bool _isBusy;
    private bool _isLoggedIn;
    private bool _loginFailed;

    public LoginViewModel()
    {
        _ui = SynchronizationContext.Current;

        // 连接过程中的阶段提示（“已连接服务器”“正在重连…”），来自 SignalR 后台线程。
        _client.StatusChanged += status => OnUi(() => Status = status);
        LoginCommand = new AsyncRelayCommand(_ => LoginAsync());
    }

    /// <summary>把回调切到 UI 线程执行。</summary>
    private void OnUi(Action action)
    {
        if (_ui == null) { action(); return; }
        _ui.Post(_ => action(), null);
    }

    /// <summary>登录成功后复用的会话（连接已建立、身份已加入房间）。</summary>
    public GameClient Session => _client;

    public AsyncRelayCommand LoginCommand { get; }

    /// <summary>登录成功并进入房间后触发。</summary>
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

    // ==================== 登录 ====================

    private async Task LoginAsync()
    {
        IsBusy = true;
        Status = "正在连接服务器…";
        try
        {
            var result = await _client.ConnectAndJoinAsync(ServerUrl, UsernameInput, PasswordInput);
            if (!result.Ok)
            {
                // 失败原因可能来自本地校验、服务器拒绝或连接异常，统一显示。
                ShowError = true;
                Status = result.Message;
                return;
            }

            ShowError = false;
            IsLoggedIn = true;
            Status = result.Message;
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
