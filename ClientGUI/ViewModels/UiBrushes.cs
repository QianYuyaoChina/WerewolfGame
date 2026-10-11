using System.Windows;
using System.Windows.Media;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>
/// 界面画刷集中缓存：避免在属性 getter 里反复 new（每次布局测量都会触发），
/// 并让配色只在一个地方定义。
/// </summary>
public static class UiBrushes
{
    // v2.5.0 待办 T3（架构 U7 降级为 P1，本轮做掉其中「与主题同义」的四个）：
    // 下面这四个本来写死了 RGB，切到浅色主题时不跟着变（深色底上才看得清的那几个值，
    // 到浅色底上就是「看不见的字」）。现在改成**实时查主题**，深浅都可读。

    /// <summary>存活 / 在线色（对应主题的 OkBrush）。</summary>
    public static Brush Alive => ThemeBrush("OkBrush");

    /// <summary>出局 / 断线色（对应主题的 ErrorBrush）。</summary>
    public static Brush Dead => ThemeBrush("ErrorBrush");

    /// <summary>警示色（重连中 / 需要提醒的状态，对应主题的 WarnBrush）。</summary>
    public static Brush Warning => ThemeBrush("WarnBrush");

    /// <summary>弱化文字色（对应主题的 MutedBrush，供无颜色标签兜底）。</summary>
    public static Brush Muted => ThemeBrush("MutedBrush");

    // 下面这些是「叠在行底色上的半透明层」，颜色方向（深色主题叠白、浅色主题应叠黑）
    // 依赖主题极性，硬换一套值会让玩家行渲染断言与观感一起变；
    // 按架构 U7 本轮保留，待 P1 与玩家行样式一起改造（见 docs/TODO.md T3）。
    public static Brush RowBackgroundDead { get; } = Frozen(Color.FromArgb(20, 255, 255, 255));
    public static Brush RowBackgroundSelectable { get; } = Frozen(Color.FromArgb(28, 124, 196, 255));
    public static Brush RowBackgroundNormal { get; } = Frozen(Color.FromArgb(12, 255, 255, 255));

    public static Brush RowBorderNormal { get; } = Frozen(Color.FromArgb(38, 255, 255, 255));
    public static Brush RowBorderTarget { get; } = Frozen(Color.FromRgb(124, 196, 255));

    private static readonly Dictionary<string, Brush> PhaseBrushes = new();
    private static readonly Dictionary<string, Brush> HexBrushes = new();

    /// <summary>阶段横幅画刷（按 <see cref="Phases.Accent"/> 缓存）。</summary>
    public static Brush PhaseBrush(string phase)
    {
        if (PhaseBrushes.TryGetValue(phase, out var cached)) return cached;

        var brush = Frozen((Color)ColorConverter.ConvertFromString(Phases.Accent(phase))!);
        PhaseBrushes[phase] = brush;
        return brush;
    }

    /// <summary>
    /// 由十六进制字符串（如 "#FFA07A"）解析出缓存的画刷；解析失败回退 <paramref name="fallback"/>。
    /// 供「游戏自定义玩家标签颜色」（<see cref="PlayerDto.TagColor"/>）与状态胶囊使用。
    /// </summary>
    public static Brush FromHex(string? hex, Brush? fallback = null)
    {
        fallback ??= RowBorderNormal;
        if (string.IsNullOrWhiteSpace(hex)) return fallback;

        if (HexBrushes.TryGetValue(hex, out var cached)) return cached;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            var brush = Frozen(color);
            HexBrushes[hex] = brush;
            return brush;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// 实时查 Application 级主题色键（不缓存：缓存了就切不了主题）。
    /// 纯逻辑测试宿主里没有 Application，此时退化为中性灰，不影响断言语义。
    /// </summary>
    private static Brush ThemeBrush(string colorKey)
    {
        try
        {
            if (Application.Current?.TryFindResource(colorKey) is Brush brush) return brush;
        }
        catch
        {
            // 主题尚未初始化（例如纯逻辑校验）：走下面的兜底。
        }

        return Brushes.Gray;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
