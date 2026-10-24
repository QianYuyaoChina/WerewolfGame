using System.Windows;
using System.Windows.Controls;

namespace ClientGUI.Controls;

/// <summary>
/// 页面内浮层宿主（遮罩 + 居中卡片 + 通栏主 / 次按钮 + 就地提示）。
///
/// 它替代 <see cref="MessageBox"/> 承载业务流程：彩蛋输入、服务器设置、修改密码等
/// 新增对话框一律用本浮层（架构约定 K11）。
/// v2.6.0 S4 起，连「保留房间 / 邀请码 / 关于 / 更新日志」这些原来是独立 Window 的流程
/// 也已经是**页面**（`Views/Pages/*`），全仓只剩 <c>ShellWindow</c> 一个 Window 子类。
///
/// 用法见 <c>OverlayDialog.xaml</c> 顶部注释：塞正文用 <see cref="DialogBody"/>，
/// 按钮文案用 <see cref="PrimaryButtonText"/> / <see cref="SecondaryButtonText"/>，
/// 结果提示用 <see cref="Message"/>（就地显示，不弹框）。
/// </summary>
public partial class OverlayDialog : UserControl
{
    public OverlayDialog()
    {
        InitializeComponent();

        // 默认两个按钮都隐藏：由使用方按需打开（避免出现「确定」却没有确定逻辑的空按钮）。
        PrimaryButton.Visibility = Visibility.Collapsed;
        SecondaryButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>卡片标题（居中）。</summary>
    public string DialogTitle
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>卡片正文（任意元素；传 null 则清空）。</summary>
    public object? DialogBody
    {
        get => BodyHost.Content;
        set => BodyHost.Content = value;
    }

    /// <summary>
    /// 就地提示文案：写在按钮上方，不弹框。空字符串表示不显示。
    /// </summary>
    public string Message
    {
        get => MessageText.Text;
        set
        {
            MessageText.Text = value;
            MessageText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>提示是否按「错误」着色（默认 true = 红色；false = 弱化说明文字）。</summary>
    public bool MessageIsError
    {
        get => _messageIsError;
        set
        {
            _messageIsError = value;
            // 用资源引用而不是直接取 Brush：主题切换后颜色会自动跟着变。
            MessageText.SetResourceReference(TextBlock.ForegroundProperty,
                value ? "ErrorBrush" : "MutedBrush");
        }
    }

    private bool _messageIsError = true;

    /// <summary>主按钮文案；设置了非空文案才会显示主按钮。</summary>
    public string PrimaryButtonText
    {
        get => (string)PrimaryButton.Content;
        set
        {
            PrimaryButton.Content = value;
            PrimaryButton.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>次按钮文案；设置了非空文案才会显示次按钮。</summary>
    public string SecondaryButtonText
    {
        get => (string)SecondaryButton.Content;
        set
        {
            SecondaryButton.Content = value;
            SecondaryButton.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>主按钮是否可用（提交中置 false，避免重复提交）。</summary>
    public bool PrimaryEnabled
    {
        get => PrimaryButton.IsEnabled;
        set => PrimaryButton.IsEnabled = value;
    }

    /// <summary>主按钮点击（业务侧挂一次即可）。</summary>
    public event RoutedEventHandler? PrimaryClick;

    /// <summary>次按钮点击（通常是「取消」）。</summary>
    public event RoutedEventHandler? SecondaryClick;

    /// <summary>显示浮层（并清空上一次的提示）。</summary>
    public void ShowDialog()
    {
        Message = "";
        Visibility = Visibility.Visible;
    }

    /// <summary>关闭浮层。</summary>
    public void CloseDialog() => Visibility = Visibility.Collapsed;

    private void PrimaryButton_OnClick(object sender, RoutedEventArgs e) => PrimaryClick?.Invoke(this, e);

    private void SecondaryButton_OnClick(object sender, RoutedEventArgs e) => SecondaryClick?.Invoke(this, e);

    /// <summary>点遮罩空白处 = 取消（与手机端一致）。</summary>
    private void Mask_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SecondaryClick?.Invoke(this, e);
        e.Handled = true;
    }

    /// <summary>点卡片内部不触发「取消」（冒泡到遮罩上会被误关）。</summary>
    private void Card_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => e.Handled = true;

    /// <summary>变为可见时把焦点给主按钮（键盘 Tab + 回车也能走完流程）。</summary>
    private void OverlayDialog_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Visibility != Visibility.Visible) return;
        if (PrimaryButton.Visibility == Visibility.Visible) PrimaryButton.Focus();
    }
}
