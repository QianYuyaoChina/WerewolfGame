using System.Windows;
using System.Windows.Controls;

namespace ClientGUI.Controls;

/// <summary>
/// 称号胶囊（金色文字 + 同色系 1px 圆角框）。
///
/// 唯一职责：把「称号」渲染成小程序风格的胶囊，**无称号时整个控件 <c>Collapsed</c>**
/// （PRD 4.4 —— 不留一个空框占位）。颜色与字号全部来自 AppShell 的
/// <c>TitleChipBorder</c> / <c>TitleChipText</c> 样式，本控件不含任何颜色字面量。
/// </summary>
public partial class TitleChip : UserControl
{
    /// <summary>称号文本依赖属性（空 / 全空白 = 无称号 → 不渲染）。</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(TitleChip),
            new PropertyMetadata("", OnTextChanged));

    public TitleChip()
    {
        InitializeComponent();

        // 默认没有称号 → 收起来，避免列表里出现一排空胶囊。
        ApplyVisibility();
    }

    /// <summary>称号文本；空 / 全空白时控件自动 <c>Collapsed</c>。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((TitleChip)d).ApplyVisibility();

    /// <summary>按当前文本决定显隐（称号为空时不渲染，不留空胶囊）。</summary>
    private void ApplyVisibility()
        => Visibility = string.IsNullOrWhiteSpace(Text) ? Visibility.Collapsed : Visibility.Visible;
}
