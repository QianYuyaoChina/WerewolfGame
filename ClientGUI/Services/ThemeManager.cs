using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClientGUI.Services;

/// <summary>主题偏好。</summary>
public enum AppTheme
{
    /// <summary>跟随 Windows 的「应用模式」（浅色 / 深色）。</summary>
    System,

    /// <summary>深色（默认）。</summary>
    Dark,

    /// <summary>浅色。</summary>
    Light
}

/// <summary>
/// 主题管理：把「颜色字典」挂在 Application 级，切换时整体替换。
///
/// 之所以能一处切换、全局立刻生效：所有界面都用 <c>{DynamicResource XxxBrush}</c> 引用颜色，
/// DynamicResource 在资源被替换后会重新求值，已打开的窗口随之换色；
/// 控件样式（Themes/Controls.xaml）里不含任何颜色字面量，所以无需重建样式。
/// </summary>
public static class ThemeManager
{
    private const string DarkUri = "pack://application:,,,/ClientGUI;component/Themes/Colors.Dark.xaml";
    private const string LightUri = "pack://application:,,,/ClientGUI;component/Themes/Colors.Light.xaml";

    private static ResourceDictionary? _colors;
    private static DispatcherTimer? _systemWatch;

    /// <summary>用户选择的偏好（可能为「跟随系统」）。</summary>
    public static AppTheme Preference { get; private set; } = AppTheme.System;

    /// <summary>当前实际生效的是否为深色。</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>主题发生变化（含「跟随系统」时的自动跟随），供界面刷新勾选状态。</summary>
    public static event Action? Changed;

    /// <summary>启动时调用一次：读偏好、挂颜色字典、开始监听系统主题。</summary>
    public static void Initialize()
    {
        Preference = ParsePreference(UiStateStore.State.Theme);
        Apply(Preference);
        StartSystemWatch();
    }

    /// <summary>切换主题（由界面调用），并持久化偏好。</summary>
    public static void SetTheme(AppTheme theme)
    {
        Preference = theme;
        UiStateStore.State.Theme = theme.ToString();
        UiStateStore.Save();
        Apply(theme);
    }

    /// <summary>重新从界面状态读取主题偏好（「恢复默认设置」后调用）。</summary>
    public static void ReloadFromState()
    {
        Preference = ParsePreference(UiStateStore.State.Theme);
        UiStateStore.State.Theme = Preference.ToString();
        UiStateStore.Save();
        Apply(Preference);
    }

    private static void Apply(AppTheme preference)
    {
        var dark = preference switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark()
        };

        if (_colors == null || dark != IsDark)
        {
            IsDark = dark;
            SwapColors(dark ? DarkUri : LightUri);
        }

        Changed?.Invoke();
    }

    /// <summary>替换颜色字典：保持在 Application 资源里的同一位置，只换实例。</summary>
    private static void SwapColors(string uri)
    {
        var app = Application.Current;
        if (app == null) return;

        var replacement = new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) };

        if (_colors != null)
        {
            var index = app.Resources.MergedDictionaries.IndexOf(_colors);
            if (index >= 0)
            {
                app.Resources.MergedDictionaries[index] = replacement;
                _colors = replacement;
                return;
            }
        }

        app.Resources.MergedDictionaries.Insert(0, replacement);
        _colors = replacement;
    }

    /// <summary>读取 Windows「应用模式」：<c>AppsUseLightTheme = 1</c> 表示浅色。</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light) return light == 0;
        }
        catch { /* 读不到就按深色处理 */ }

        return true;
    }

    /// <summary>
    /// 「跟随系统」时轮询系统主题（4 秒一次）。
    /// 没用 SystemEvents（.NET 下需额外依赖），轮询读注册表成本极低且更稳。
    /// </summary>
    private static void StartSystemWatch()
    {
        _systemWatch ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _systemWatch.Tick -= OnSystemWatchTick;
        _systemWatch.Tick += OnSystemWatchTick;
        _systemWatch.Start();
    }

    private static void OnSystemWatchTick(object? sender, EventArgs e)
    {
        if (Preference != AppTheme.System) return;
        Apply(AppTheme.System);
    }

    private static AppTheme ParsePreference(string? text)
        => Enum.TryParse<AppTheme>(text, ignoreCase: true, out var theme) ? theme : AppTheme.System;
}
