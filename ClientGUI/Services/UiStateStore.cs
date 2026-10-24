using System.IO;
using System.Text.Json;

namespace ClientGUI.Services;

/// <summary>
/// 需要跨进程保存的界面偏好（窗口几何 + 视图显隐 + 上次登录信息）。
/// 全部为可选字段，缺失时回退到内置默认值。
/// </summary>
public sealed class UiState
{
    // ---- 主窗口几何 ----
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1220;
    public double Height { get; set; } = 780;
    public bool Maximized { get; set; }

    // ---- 视图菜单：三栏显隐 ----
    public bool ShowPlayers { get; set; } = true;
    public bool ShowChat { get; set; } = true;
    public bool ShowActions { get; set; } = true;

    // ---- 页面切换动效（v2.6.0 S7；默认开，设置 → 主界面可关） ----
    /// <summary>
    /// 切页是否播放滑动动画（像手机系统那样）。关掉后立即切换、无动画；
    /// 不影响任何功能，只影响观感。
    /// </summary>
    public bool PageTransitionAnimation { get; set; } = true;

    // ---- 登录窗口：上次填过的地址与用户名（不保存密码） ----
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";

    // ---- 登录窗口：内置服务器页记住上次的选择（P1-5） ----
    /// <summary>内置服务器端口（默认 5000；被占用时会自动漂移，这里记的是「首选端口」）。</summary>
    public int EmbeddedPort { get; set; } = 5000;

    /// <summary>上次停留的登录标签页：<c>embedded</c> = 创建内置服务器，<c>external</c> = 连接现有服务器。</summary>
    public string LastLoginTab { get; set; } = "embedded";

    // ---- 房间列表：上次进入过的房间号（便于回填 / 提示） ----
    public string LastRoomId { get; set; } = "";

    // ---- 外观：System / Dark / Light（见 ThemeManager） ----
    public string Theme { get; set; } = "System";
}

/// <summary>
/// 界面状态的读写（JSON 文件，默认位于 <c>%APPDATA%\WerewolfClient\ui-state.json</c>）。
/// 读取失败或文件损坏时静默回退到默认值，绝不因为“记不住上次状态”而影响启动。
/// </summary>
public static class UiStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static UiState? _state;
    private static UiState? _memoryOnly;

    /// <summary>
    /// 是否读写默认路径。测试（界面冒烟）会把它置为 false，
    /// 以免覆盖用户真实的界面偏好；此时 <see cref="State"/> 退化为进程内默认值。
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>默认存储路径（便于测试改用临时文件）。</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WerewolfClient", "ui-state.json");

    /// <summary>当前状态（首次访问时从磁盘加载）。</summary>
    public static UiState State => Enabled
        ? _state ??= Load(DefaultPath)
        : _memoryOnly ??= new UiState();

    /// <summary>把当前状态写回默认路径。</summary>
    public static void Save()
    {
        if (!Enabled) return;
        Save(State, DefaultPath);
    }

    /// <summary>恢复默认设置（同时覆盖内存与磁盘）。</summary>
    public static void Reset()
    {
        if (Enabled) _state = new UiState();
        else _memoryOnly = new UiState();

        Save();
    }

    /// <summary>从指定文件读取状态；文件缺失或损坏时返回默认值。</summary>
    public static UiState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new UiState();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UiState>(json, Options) ?? new UiState();
        }
        catch
        {
            return new UiState();
        }
    }

    /// <summary>把状态写入指定文件（写失败不影响运行）。</summary>
    public static void Save(UiState state, string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(state, Options));
        }
        catch { /* 忽略写盘失败：状态持久化不是关键路径 */ }
    }
}
