using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using ClientGUI.Services;
using Werewolf.Client.Share;
using Werewolf.Server;

namespace ClientGUI.ViewModels;

/// <summary>
/// 「一键开服」的分步状态（v2.5.0 第 6 条 / 架构 D7）。
/// 任何一步失败都**不回滚已完成的步骤**：服务器起来了就保持运行，按钮恢复可点、可原地重试。
/// </summary>
public enum OneClickStep
{
    /// <summary>还没点过。</summary>
    Idle,

    /// <summary>正在启动内置服务器。</summary>
    Starting,

    /// <summary>服务器已起来，正在连接（局域网优先，失败回落本机一次）。</summary>
    Connecting,

    /// <summary>已连上，正在用管理员账号登录。</summary>
    LoggingIn,

    /// <summary>已用管理员登录且服务器运行中 → 主按钮变「进入房间列表」。</summary>
    Ready,

    /// <summary>某一步失败；<see cref="EmbeddedServerViewModel.FailedAt"/> 记录卡在哪一步。</summary>
    Failed
}

/// <summary>
/// 登录窗口 Tab1「创建内置服务器」的视图模型。
///
/// 职责边界：它只做「把 <see cref="EmbeddedServerService"/> 的状态翻译成界面文案 + 按钮可用性」，
/// 不碰任何 ASP.NET Core API、不自己判断服务器状态（状态由服务单一来源推导）。
/// 「启动并进入」复用 <see cref="LoginViewModel"/> **完全相同**的登录流程（PRD P0-4）。
///
/// v2.5.0 第 6 条：新增「一键开服」状态机 —— 一次点击完成
/// 「启动服务器 → 连接（局域网优先 / 回落本机）→ 用内置管理员登录」，
/// 并用**显式凭据**登录，绝不覆盖用户手填的用户名 / 密码（架构 K13）。
/// </summary>
public sealed class EmbeddedServerViewModel : ObservableObject
{
    private readonly EmbeddedServerService _service = EmbeddedServerService.Current;
    private readonly LoginViewModel _login;

    /// <summary>运行信息（房间数 / 人数）的刷新周期；仅在运行中才真正查询。</summary>
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };

    private string _portText;
    private string _portError = "";
    private OneClickStep _step = OneClickStep.Idle;
    private string _stepText = "";
    private OneClickStep _failedAt = OneClickStep.Idle;

    public EmbeddedServerViewModel(LoginViewModel login)
    {
        _login = login;
        _portText = _service.Port.ToString();

        StartAndEnterCommand = new AsyncRelayCommand(_ => StartAndEnterAsync(), _ => CanStart);
        StartOnlyCommand = new AsyncRelayCommand(_ => StartAsync(), _ => CanStart);
        StopCommand = new AsyncRelayCommand(_ => StopAsync(), _ => CanStop);
        CopyCommand = new RelayCommand(p => CopyAddress(p as string ?? ""));
        OpenDirectoryCommand = new RelayCommand(_ => OpenDirectory());
        LoginToEmbeddedCommand = new AsyncRelayCommand(_ => LoginToEmbeddedAsync(), _ => CanLogin);
        OneClickCommand = new AsyncRelayCommand(_ => OneClickAsync(), _ => CanOneClick);

        _service.Changed += OnServiceChanged;
        _poll.Tick += (_, _) =>
        {
            if (!_service.IsRunning) return;
            _service.Refresh();
            OnPropertyChanged(nameof(RuntimeText));
        };
        _poll.Start();
    }

    // ==================== 输入：端口 ====================

    /// <summary>端口输入（文本形式，便于内联提示非法输入而不弹异常框，E-17）。</summary>
    public string PortText
    {
        get => _portText;
        set
        {
            if (!Set(ref _portText, value)) return;

            ValidatePort();
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanEditPort));
        }
    }

    /// <summary>端口非法时的内联提示（空表示合法）。</summary>
    public string PortError => _portError;

    /// <summary>端口输入是否合法。</summary>
    public bool IsPortValid => _portError.Length == 0;

    /// <summary>被占用时是否自动换一个空闲端口（PRD §5.2 默认勾选）。</summary>
    public bool AllowPortDrift
    {
        get => _service.AllowPortDrift;
        set
        {
            if (_service.AllowPortDrift == value) return;
            _service.AllowPortDrift = value;
            OnPropertyChanged();
        }
    }

    // ==================== 状态展示 ====================

    public EmbeddedServerState State => _service.State;

    /// <summary>状态徽章文案（未启动 / 启动中 / 运行中 / 停止中 / 已停止 / 启动失败）。</summary>
    public string StateText => State switch
    {
        EmbeddedServerState.NotStarted => "未启动",
        EmbeddedServerState.Starting => "启动中",
        EmbeddedServerState.Running => "运行中",
        EmbeddedServerState.Stopping => "停止中",
        EmbeddedServerState.Stopped => "已停止",
        EmbeddedServerState.Failed => "启动失败",
        _ => "未启动"
    };

    /// <summary>共享状态条文案（人话；启动失败时直接给出原因）。</summary>
    public string StatusText => _service.StatusText;

    /// <summary>本机地址（含 /gamehub）。</summary>
    public string LocalUrlText => _service.LocalUrl;

    /// <summary>局域网地址（含 /gamehub），发给朋友用。</summary>
    public string LanUrlText => _service.LanUrl;

    /// <summary>运行信息：「房间 N · 已连接玩家 M」。</summary>
    public string RuntimeText
    {
        get
        {
            var runtime = _service.Runtime;
            return $"房间 {runtime.RoomCount} · 已连接玩家 {runtime.PlayerCount}";
        }
    }

    /// <summary>工作目录（实际使用的那个）。</summary>
    public string WorkDirectoryText => _service.WorkDirectoryText;

    /// <summary>是否已发生端口漂移（用于在界面上高亮提示）。</summary>
    public bool HasDriftNote => _service.DriftNote.Length > 0;

    /// <summary>端口漂移提示（如「5000 已被占用，已改用 5001」）。</summary>
    public string DriftNote => _service.DriftNote;

    // ==================== 按钮可用性（一律由状态推导） ====================

    /// <summary>端口与漂移开关是否可编辑（运行中 / 启停中锁定，避免「改了没生效」的错觉）。</summary>
    public bool CanEditPort => State is EmbeddedServerState.NotStarted
                                     or EmbeddedServerState.Stopped
                                     or EmbeddedServerState.Failed;

    /// <summary>「启动并进入」/「仅启动服务器」是否可用。</summary>
    public bool CanStart => IsPortValid
                            && !_service.IsBusy
                            && !_service.IsRunning
                            && !_login.IsBusy;

    /// <summary>「停止」是否可用（启动中也能点 = 取消启动）。</summary>
    public bool CanStop => _service.IsRunning || _service.IsBusy;

    /// <summary>Tab1 的「登录」是否可用（服务器已启动且当前空闲）。</summary>
    public bool CanLogin => _service.IsRunning && !_login.IsBusy;

    /// <summary>主按钮文案（启动中显示「启动中…」）。</summary>
    public string StartButtonText => _service.State == EmbeddedServerState.Starting ? "启动中…" : "启动并进入";

    // ==================== 一键开服（v2.5.0 第 6 条） ====================

    /// <summary>当前分步状态（Idle → Starting → Connecting → LoggingIn → Ready / Failed）。</summary>
    public OneClickStep Step => _step;

    /// <summary>分步状态的**人话**文案（给状态条 / 按钮下方那行小字用）。</summary>
    public string StepText => _stepText;

    /// <summary>失败时卡在哪一步（未失败时为 <see cref="OneClickStep.Idle"/>）。</summary>
    public OneClickStep FailedAt => _failedAt;

    /// <summary>
    /// 是否已「开好服并登上管理员」：已用管理员登录 **且** 服务器运行中。
    /// 为 true 时主按钮切成「进入房间列表」。
    /// </summary>
    public bool IsOneClickDone => _login.IsLoggedIn && _service.IsRunning;

    /// <summary>「一键开服 / 进入房间列表」是否可点。</summary>
    public bool CanOneClick => IsOneClickDone
                               || (IsPortValid && !_service.IsBusy && !_login.IsBusy);

    /// <summary>主按钮文案：就绪后切「进入房间列表」，启动中显示「启动中…」，其余为「一键开服」。</summary>
    public string MainButtonText => IsOneClickDone
        ? "进入房间列表"
        : _service.State == EmbeddedServerState.Starting ? "启动中…" : "一键开服";

    /// <summary>主按钮下方那行小字：**明文**说明会用谁登录（PRD 6.6，不让用户蒙在鼓里）。</summary>
    public string OneClickHint => AdminDefaults.OneClickLoginHint;

    /// <summary>是否显示「一键开服」的分步文案（未点过时为 false，界面保持干净）。</summary>
    public bool HasStepText => _stepText.Length > 0;

    // ==================== 命令 ====================

    public AsyncRelayCommand StartAndEnterCommand { get; }
    public AsyncRelayCommand StartOnlyCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenDirectoryCommand { get; }
    public AsyncRelayCommand LoginToEmbeddedCommand { get; }

    /// <summary>「一键开服」/「进入房间列表」主按钮（v2.5.0 第 6 条）。</summary>
    public AsyncRelayCommand OneClickCommand { get; }

    // ==================== 行为 ====================

    /// <summary>
    /// 一键开服：启动服务器 → 连接（局域网优先，失败回落本机一次）→ 用内置管理员登录。
    ///
    /// 三条与普通登录不同的地方：
    /// <list type="bullet">
    ///   <item>用**显式凭据**登录（admin + 默认密码），不写用户手填的用户名 / 密码（K13）；</item>
    ///   <item>成功后**自动进入房间列表**：到达就绪态后紧接着调用 <see cref="LoginViewModel.EnterRoomList"/>，
    ///         点一次就落到房间列表；按钮仍会切成「进入房间列表」，再点一次同样能进（幂等兜底分支保留）；</item>
    ///   <item>任一步失败都**不回滚**：服务器起来了就保持运行，按钮恢复可点，可原地重试（此时**不进入**）。</item>
    /// </list>
    /// </summary>
    private async Task OneClickAsync()
    {
        // 已就绪 → 这一次点击的含义是「进入房间列表」。
        if (IsOneClickDone)
        {
            _login.EnterRoomList();
            return;
        }

        SetStep(OneClickStep.Starting, "① 正在启动内置服务器…");

        var info = await StartAsync();
        if (!info.Ok)
        {
            // 停在①：服务器没起来，等用户改端口后重试。
            SetStep(OneClickStep.Failed, "① 启动失败：" + info.ErrorMessage, OneClickStep.Starting);
            return;
        }

        // 端口漂移直接显示成状态文案（PRD E-16：界面显示的端口就是实际端口）。
        var started = _service.DriftNote.Length > 0
            ? $"已启动（{_service.DriftNote}），正在连接…"
            : $"已启动（端口 {info.Port}），正在连接…";
        SetStep(OneClickStep.Connecting, "② " + started);

        SetStep(OneClickStep.LoggingIn, "③ 正在用管理员 " + AdminDefaults.Username + " 登录…");

        var ok = await _login.ConnectWithFallbackAsync(
            info.ConnectUrl, info.LocalUrl, info.LanUrl,
            AdminDefaults.Username, AdminDefaults.DefaultAdminPassword);

        if (!ok)
        {
            // 停在② / ③：**服务器保持运行**（不 StopAsync），连接与服务器都留给用户重试。
            SetStep(OneClickStep.Failed, "登录失败（服务器仍在运行）：" + _login.Status, OneClickStep.LoggingIn);
            return;
        }

        SetStep(OneClickStep.Ready, $"已登录：{_login.Session.Username}（管理员），正在进入房间列表…");

        // 收尾轮（用户裁定）：点一次就落到房间列表 —— 到达就绪态后紧接着复用同一条进入路径
        // （EnterRoomList → LoginSucceeded → 登录窗口 OnLoginSucceeded 打开房间列表）。
        // 注意：所有失败分支在此**之前**都已 return，所以「任一步失败都不进入」的语义不受影响。
        _login.EnterRoomList();
    }

    /// <summary>启动 → 自动连接（实际端口，含 /gamehub）→ 用当前凭据登录。</summary>
    private async Task StartAndEnterAsync()
    {
        var info = await StartAsync();
        if (!info.Ok) return;

        await ConnectAndLoginAsync(info);
    }

    /// <summary>只启动服务器并展示可分享地址，自己先不进。</summary>
    private async Task<EmbeddedServerInfo> StartAsync()
    {
        if (!IsPortValid) return _service.Info;

        _service.Port = ParsePort();
        var info = await _service.StartAsync();
        return info;
    }

    private async Task StopAsync()
    {
        await _service.StopAsync();
    }

    /// <summary>
    /// 自动连接内置服务器：优先用局域网地址（这样随后生成的邀请码好友也能直接用，PRD P1-6 / E-16）；
    /// 局域网地址连不上时**回落一次**本机地址（多网卡机器可能选中不合适的那一张）。
    /// </summary>
    private async Task ConnectAndLoginAsync(EmbeddedServerInfo info)
    {
        if (await TryLoginAsync(info.ConnectUrl)) return;

        if (string.Equals(info.LocalUrl, info.ConnectUrl, StringComparison.Ordinal)) return;

        _login.SetSharedStatus("局域网地址连不上，改用本机地址重试…");
        if (!await TryLoginAsync(info.LocalUrl)) return;

        // E-18：回落只改「我怎么连自己」，分享出去的邀请码仍必须是局域网地址 ——
        // 否则好友拿到码会连到 127.0.0.1（连到他自己电脑上）。
        _login.Session.InviteUrlOverride = info.LanUrl;
        _login.SetSharedStatus("已改用本机地址连接（分享给好友的邀请码仍是局域网地址）。");
    }

    private async Task<bool> TryLoginAsync(string url)
    {
        await _login.LoginWithServerUrlAsync(url);
        return _login.IsLoggedIn;
    }

    /// <summary>已启动内置服务器后单独点「登录」（对应「仅启动服务器」之后的手动登录）。</summary>
    private async Task LoginToEmbeddedAsync()
    {
        await _login.LoginWithServerUrlAsync(_service.DisplayAddress);
    }

    /// <summary>复制地址到剪贴板（内容为完整地址，含 /gamehub）。</summary>
    private void CopyAddress(string url)
    {
        if (string.IsNullOrEmpty(url) || url == "—")
        {
            _service.SetStatusOverride("服务器还没启动，暂无可复制的地址。");
            return;
        }

        _service.SetStatusOverride(ClipboardHelper.TrySetText(url)
            ? "已复制地址：" + url
            : "复制失败，请手动选中上面的地址复制。");
    }

    /// <summary>打开内置服务器的工作目录（P1-7），便于手工改配置 / 词库。</summary>
    private void OpenDirectory()
    {
        var directory = _service.WorkDirectoryText;

        // 未启动时目录可能还不存在：先建出来再打开，保证点得开。
        if (!Directory.Exists(directory))
        {
            try { Directory.CreateDirectory(directory); }
            catch { /* 建不出来就交给下一步的提示 */ }
        }

        if (EmbeddedServerPaths.TryOpen(directory)) return;

        _service.SetStatusOverride("无法打开服务器目录：" + directory);
    }

    /// <summary>记录分步状态并通知界面（<paramref name="failedAt"/> 只在失败时才有意义）。</summary>
    private void SetStep(OneClickStep step, string text, OneClickStep failedAt = OneClickStep.Idle)
    {
        _step = step;
        _failedAt = step == OneClickStep.Failed ? failedAt : OneClickStep.Idle;
        _stepText = text ?? "";

        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(HasStepText));
        OnPropertyChanged(nameof(FailedAt));
        OnPropertyChanged(nameof(IsOneClickDone));
        OnPropertyChanged(nameof(CanOneClick));
        OnPropertyChanged(nameof(MainButtonText));
    }

    /// <summary>
    /// 刷新派生文案（主按钮「一键开服 / 进入房间列表」等）。
    ///
    /// 页面化之后需要它：登录页是根页，从房间列表「返回」回来时页面实例与视图模型都还在，
    /// 但绑定不会自动重算 <c>MainButtonText</c> —— 此时若服务器仍在跑且已登录，
    /// 主按钮必须显示「进入房间列表」（Q1=B 的兜底分支，架构 N4）。
    /// </summary>
    public void RefreshComputedState()
    {
        OnPropertyChanged(nameof(IsOneClickDone));
        OnPropertyChanged(nameof(CanOneClick));
        OnPropertyChanged(nameof(MainButtonText));
    }

    /// <summary>停止运行信息的轮询并退订服务事件（窗口关闭时调用，避免留下悬挂引用）。</summary>
    public void StopPolling()
    {
        _poll.Stop();
        _service.Changed -= OnServiceChanged;
    }

    // ==================== 内部 ====================

    private int ParsePort()
        => int.TryParse(PortText.Trim(), out var port) ? port : EmbeddedServerService.DefaultPort;

    private void ValidatePort()
    {
        var text = (PortText ?? "").Trim();
        if (text.Length == 0)
        {
            _portError = "请填写端口（1024–65535，默认 5000）。";
            return;
        }

        if (!int.TryParse(text, out var port))
        {
            _portError = "端口必须是数字（1024–65535）。";
            return;
        }

        if (port is < 1024 or > 65535)
        {
            _portError = "端口必须在 1024–65535 之间。";
            return;
        }

        _portError = "";
    }

    private void OnServiceChanged()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LocalUrlText));
        OnPropertyChanged(nameof(LanUrlText));
        OnPropertyChanged(nameof(RuntimeText));
        OnPropertyChanged(nameof(WorkDirectoryText));
        OnPropertyChanged(nameof(DriftNote));
        OnPropertyChanged(nameof(HasDriftNote));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanEditPort));
        OnPropertyChanged(nameof(CanLogin));
        OnPropertyChanged(nameof(StartButtonText));

        // 一键开服的可点性 / 文案也跟着服务器状态变（服务器停了就不算「就绪」）。
        if (_step == OneClickStep.Ready && !IsOneClickDone) SetStep(OneClickStep.Idle, "");

        OnPropertyChanged(nameof(IsOneClickDone));
        OnPropertyChanged(nameof(CanOneClick));
        OnPropertyChanged(nameof(MainButtonText));

        // 把状态同步到共享状态条（人话，无堆栈）。
        _login.SetSharedStatus(_service.StatusText, _service.State == EmbeddedServerState.Failed);
    }
}
