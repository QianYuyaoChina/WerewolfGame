using System.Windows.Media.Animation;

namespace ClientGUI.Services;

/// <summary>页面切换的滑动方向（v2.6.0 S7）。</summary>
public enum PageSlide
{
    /// <summary>不播动画（首屏没有旧页、用户关掉了动画、或目标不是控件）。</summary>
    None,

    /// <summary>前进（入栈）：新页从右侧整幅滑入，旧页向左 25% 视差并淡出（Android 前进转场）。</summary>
    Forward,

    /// <summary>后退（出栈）：旧页整幅滑出右侧，新页从左侧 25% 回到原位（Android 返回手势）。</summary>
    Backward
}

/// <summary>
/// 页面切换动画的**方向判定与参数**（纯函数 + 常量，可被自动化断言直接钉死）。
///
/// 为什么方向要单独抽出来：动画本身是视觉的、没法断言，但「往哪边滑」完全由
/// 栈深变化决定 —— 收成纯函数后，「入栈一定前进、出栈一定后退、关掉动画一定不动」
/// 这三条就成了回归防线。
///
/// ⚠ 架构 D6 原写着「不做页面转场动画（会让几何实测断言从稳定变成抖动）」，
/// S7 按用户明确要求**显式推翻**该条（见 SHELL-ARCHITECTURE §26）。推翻的前提是
/// 静止态不变式不被破坏：动画期间两页装在临时 Grid 里，动画一结束 `PageHost.Content`
/// 立刻回到「就是那一页」——因此所有「外壳只有一个页面宿主」的既有断言一条都没改。
/// </summary>
public static class PageTransition
{
    /// <summary>动画时长（Material「标准」档 300ms 取整，实测 280ms 更利落）。</summary>
    public static TimeSpan Duration { get; } = TimeSpan.FromMilliseconds(280);

    /// <summary>
    /// 「退到背后那一页」的视差系数：前进时旧页只挪 25% 并淡出，
    /// 后退时新页从 25% 处回来 —— 这是 Android 转场最显著的体感特征。
    /// </summary>
    public const double Parallax = 0.25;

    /// <summary>进入的那一页用的缓动（快出慢住，Material 的 decelerate）。</summary>
    public static IEasingFunction EnterEase { get; } = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>离开的那一页用的缓动（先慢后快，Material 的 accelerate）。</summary>
    public static IEasingFunction LeaveEase { get; } = new CubicEase { EasingMode = EasingMode.EaseIn };

    /// <summary>
    /// 这一次切页该往哪边滑。
    /// </summary>
    /// <param name="enabled">用户是否开着页面切换动画（设置 → 主界面）。</param>
    /// <param name="hasOutgoing">有没有「被替换掉的旧页」（首屏没有 → 不该播）。</param>
    /// <param name="depth">切页后的栈深。</param>
    /// <param name="lastDepth">切页前的栈深。</param>
    public static PageSlide Resolve(bool enabled, bool hasOutgoing, int depth, int lastDepth)
        => !enabled || !hasOutgoing ? PageSlide.None
         : depth >= lastDepth ? PageSlide.Forward
         : PageSlide.Backward;
}
