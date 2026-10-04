using System.Windows.Media;
using Werewolf.Client.Share;

namespace ClientGUI.ViewModels;

/// <summary>
/// 界面画刷集中缓存：避免在属性 getter 里反复 new（每次布局测量都会触发），
/// 并让配色只在一个地方定义。
/// </summary>
public static class UiBrushes
{
    public static Brush Alive { get; } = Frozen(Color.FromRgb(84, 214, 154));
    public static Brush Dead { get; } = Frozen(Color.FromRgb(230, 96, 96));

    /// <summary>警示色（重连中 / 需要提醒的状态）。</summary>
    public static Brush Warning { get; } = Frozen(Color.FromRgb(240, 190, 90));

    public static Brush RowBackgroundDead { get; } = Frozen(Color.FromArgb(20, 255, 255, 255));
    public static Brush RowBackgroundSelectable { get; } = Frozen(Color.FromArgb(28, 124, 196, 255));
    public static Brush RowBackgroundNormal { get; } = Frozen(Color.FromArgb(12, 255, 255, 255));

    public static Brush RowBorderNormal { get; } = Frozen(Color.FromArgb(38, 255, 255, 255));
    public static Brush RowBorderTarget { get; } = Frozen(Color.FromRgb(124, 196, 255));

    private static readonly Dictionary<string, Brush> PhaseBrushes = new();

    /// <summary>阶段横幅画刷（按 <see cref="Phases.Accent"/> 缓存）。</summary>
    public static Brush PhaseBrush(string phase)
    {
        if (PhaseBrushes.TryGetValue(phase, out var cached)) return cached;

        var brush = Frozen((Color)ColorConverter.ConvertFromString(Phases.Accent(phase))!);
        PhaseBrushes[phase] = brush;
        return brush;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
