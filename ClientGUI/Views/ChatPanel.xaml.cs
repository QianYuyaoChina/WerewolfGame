using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using ClientGUI.ViewModels;

namespace ClientGUI.Views;

/// <summary>中栏：房间消息列表 + 发言输入。</summary>
public partial class ChatPanel : UserControl
{
    public ChatPanel()
    {
        InitializeComponent();

        // 数据上下文由主窗口继承而来；切换时同步换绑日志的自动滚动。
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>进入房间后把焦点交给输入框，方便立刻发言。</summary>
    public void FocusChatInput() => ChatInput.Focus();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is GameViewModel old) old.Log.CollectionChanged -= OnLogChanged;
        if (e.NewValue is GameViewModel vm) vm.Log.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (e.NewItems == null || e.NewItems.Count == 0) return;

        // 等布局完成后再滚动，保证滚到最新一条。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var item = e.NewItems[^1];
            if (item != null) LogList.ScrollIntoView(item);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
}
