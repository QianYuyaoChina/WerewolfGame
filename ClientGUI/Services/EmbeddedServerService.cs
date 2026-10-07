using Werewolf.Server;

namespace ClientGUI.Services;

/// <summary>
/// 内置服务器的状态机（PRD §6）：界面按钮可用性一律由它推导，
/// 状态**单一来源**在本服务，不允许各控件自行判断。
/// </summary>
public enum EmbeddedServerState
{
    /// <summary>未启动（灰）。</summary>
    NotStarted,

    /// <summary>启动中（黄）。</summary>
    Starting,

    /// <summary>运行中（绿）。</summary>
    Running,

    /// <summary>停止中（黄）。</summary>
    Stopping,

    /// <summary>已停止（灰）。</summary>
    Stopped,

    /// <summary>启动失败（红，附人话原因）。</summary>
    Failed
}

/// <summary>
/// 进程级单例：内置服务器的**唯一**入口。
/// 视图模型（<c>EmbeddedServerViewModel</c>）与退出收尾（<c>AppShutdown</c>）都只通过本类驱动宿主，
/// 保证「界面显示的状态 / 端口」与「实际监听的端口」永远同源（PRD E-16）。
/// </summary>
public sealed class EmbeddedServerService
{
    /// <summary>默认端口（PRD Q1 裁定：固定 5000）。</summary>
    public const int DefaultPort = 5000;

    /// <summary>停止内置服务器的等待预算（PRD E-9）。</summary>
    public static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(5);

    private static readonly EmbeddedServerService Instance = new();

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    private EmbeddedServerHost? _host;
    private EmbeddedServerState _state = EmbeddedServerState.NotStarted;
    private EmbeddedServerInfo _info = EmbeddedServerInfo.NotStarted;
    private EmbeddedServerRuntime _runtime = new(0, 0);
    private string _statusOverride = "";

    private EmbeddedServerService()
    {
        Port = UiStateStore.State.EmbeddedPort;
    }

    /// <summary>进程级单例（整个客户端进程只有一份内置服务器）。</summary>
    public static EmbeddedServerService Current => Instance;

    /// <summary>状态 / 地址 / 运行信息变化时触发（已切回 UI 线程）。</summary>
    public event Action? Changed;

    // ==================== 可调参数（未启动时可改） ====================

    /// <summary>首选端口（P1-5：记住上次使用的端口）。</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>端口被占用时是否自动换一个空闲端口（PRD：默认开启）。</summary>
    public bool AllowPortDrift { get; set; } = true;

    // ==================== 状态 ====================

    public EmbeddedServerState State => _state;

    /// <summary>最近一次启动结果（端口 / 地址 / 工作目录 / 人话失败原因）。</summary>
    public EmbeddedServerInfo Info => _info;

    /// <summary>进程内查询到的运行信息（房间数 / 已连接玩家数）。</summary>
    public EmbeddedServerRuntime Runtime => _runtime;

    /// <summary>正在运行的宿主（未运行时为 null）。</summary>
    public EmbeddedServerHost? Host => _host;

    /// <summary>是否已成功启动并正在监听。</summary>
    public bool IsRunning => _state == EmbeddedServerState.Running && _host != null;

    /// <summary>是否处于「启动中 / 停止中」（此时禁止再次启动）。</summary>
    public bool IsBusy => _state is EmbeddedServerState.Starting or EmbeddedServerState.Stopping;

    /// <summary>界面显示的实际地址（含 <c>/gamehub</c>）；未运行时为「—」。</summary>
    public string DisplayAddress => IsRunning ? _info.ConnectUrl : "—";

    /// <summary>本机地址（含 <c>/gamehub</c>）；未运行时为「—」。</summary>
    public string LocalUrl => IsRunning ? _info.LocalUrl : "—";

    /// <summary>局域网地址（含 <c>/gamehub</c>）；未运行时为「—」。</summary>
    public string LanUrl => IsRunning ? _info.LanUrl : "—";

    /// <summary>
    /// 工作目录（配置 / 账号 / 词库所在）。
    /// 未启动时显示「将要使用」的目录（只算路径、不创建，避免仅仅打开登录窗口就在磁盘上留下目录）；
    /// 启动后显示**实际**使用的目录（程序目录只读而回退时，这里会如实反映，E-6）。
    /// </summary>
    public string WorkDirectoryText
        => _info.WorkDirectory.Length > 0 ? _info.WorkDirectory : EmbeddedServerPaths.PreferredDirectory();

    /// <summary>状态条文案（人话；失败原因直接可见，绝不含异常堆栈）。</summary>
    public string StatusText
    {
        get
        {
            if (_statusOverride.Length > 0) return _statusOverride;

            return _state switch
            {
                EmbeddedServerState.Starting => "正在启动内置服务器…",
                EmbeddedServerState.Running => DriftNote.Length == 0
                    ? $"内置服务器已启动（端口 {_info.Port}），可以进入房间了。"
                    : $"内置服务器已启动（{DriftNote}），可以进入房间了。",
                EmbeddedServerState.Stopping => "正在停止内置服务器…",
                EmbeddedServerState.Stopped => "内置服务器已停止。",
                EmbeddedServerState.Failed => _info.ErrorMessage,
                _ => "内置服务器未启动。点「启动并进入」即可在这台电脑上开一个服务器。"
            };
        }
    }

    /// <summary>端口漂移提示（如「5000 已被占用，已改用 5001」）；未漂移时为空。</summary>
    public string DriftNote
        => IsRunning && _info.Port != 0 && _info.Port != Port
            ? $"端口 {Port} 已被占用，已改用 {_info.Port}"
            : "";

    /// <summary>覆盖状态条文案（供「复制成功」这类瞬时提示用）；传空字符串恢复自动文案。</summary>
    public void SetStatusOverride(string text)
    {
        _statusOverride = text ?? "";
        Notify();
    }

    // ==================== 启停 ====================

    /// <summary>
    /// 启动内置服务器。失败时**不抛异常**：以 <c>Info.Ok=false</c> + 人话 <c>ErrorMessage</c> 返回。
    /// 已在运行 / 正在启停时直接返回当前结果（PRD E-2：绝不出现两个服务器）。
    /// </summary>
    public async Task<EmbeddedServerInfo> StartAsync()
    {
        if (IsRunning || IsBusy) return _info;

        SetState(EmbeddedServerState.Starting);

        // 记住本次端口（P1-5），下次启动自动带入。
        UiStateStore.State.EmbeddedPort = Port;
        UiStateStore.Save();

        var options = new EmbeddedServerOptions
        {
            PreferredPort = Port,
            AllowPortDrift = AllowPortDrift
        };

        var host = new EmbeddedServerHost(options);

        EmbeddedServerInfo info;
        try
        {
            // 放到线程池：Builder + 端口绑定是同步耗时操作，不能卡住 UI 线程。
            info = await Task.Run(() => host.StartAsync()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 兜底：宿主契约上不抛异常，这里只是防止意外（仍然不把堆栈交给用户）。
            info = EmbeddedServerInfo.Fail("内置服务器启动失败：" + EmbeddedServerHost.DescribeBindFailure(ex));
        }

        _info = info;
        _statusOverride = "";

        if (!info.Ok)
        {
            await SafeDisposeAsync(host).ConfigureAwait(true);
            SetState(EmbeddedServerState.Failed);
            return info;
        }

        _host = host;
        _runtime = host.Snapshot();
        SetState(EmbeddedServerState.Running);
        return info;
    }

    /// <summary>停止内置服务器（带 5s 预算，超时即放弃等待）。</summary>
    public async Task StopAsync()
    {
        var host = _host;
        if (host == null)
        {
            if (_state != EmbeddedServerState.NotStarted) SetState(EmbeddedServerState.Stopped);
            return;
        }

        if (_state == EmbeddedServerState.Stopping) return;
        SetState(EmbeddedServerState.Stopping);
        _host = null;
        _statusOverride = "";

        try
        {
            await host.StopAsync(StopBudget).ConfigureAwait(true);
        }
        catch
        {
            // 停止失败不阻塞：进程退出即视为服务器停止（E-15）。
        }

        _runtime = new EmbeddedServerRuntime(0, 0);
        SetState(EmbeddedServerState.Stopped);
    }

    /// <summary>进程内重新查询运行信息（房间数 / 已连接玩家数）。</summary>
    public EmbeddedServerRuntime Refresh()
    {
        var host = _host;
        if (host != null)
        {
            try { _runtime = host.Snapshot(); }
            catch { /* 查询失败保持旧值 */ }
        }

        return _runtime;
    }

    // ==================== 内部 ====================

    private static async Task SafeDisposeAsync(EmbeddedServerHost host)
    {
        try { await host.DisposeAsync().ConfigureAwait(true); }
        catch { /* 忽略 */ }
    }

    private void SetState(EmbeddedServerState state)
    {
        _state = state;
        Notify();
    }

    private void Notify()
    {
        var handler = Changed;
        if (handler == null) return;

        if (_ui == null) { handler(); return; }
        _ui.Post(_ => handler(), null);
    }
}
