using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ClientGUI;

/// <summary>true → Visible，false → Collapsed。</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>true → Collapsed，false → Visible。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>false → 0.55（变灰），true → 1。</summary>
public sealed class EnabledToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false ? 0.55 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>十六进制颜色字符串 → 画刷（走 <see cref="ClientGUI.ViewModels.UiBrushes.FromHex"/> 缓存）；空 / 非法回退主题文字色。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fallback = Application.Current?.TryFindResource("TextBrush") as Brush
                       ?? System.Windows.Media.Brushes.White;
        return ClientGUI.ViewModels.UiBrushes.FromHex(value as string, fallback);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>非空字符串 → Visible，空 / null → Collapsed。</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
