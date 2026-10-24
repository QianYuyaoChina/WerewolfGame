namespace ClientGUI.Services;

/// <summary>
/// 「主界面三栏显隐」变更的广播（v2.6.0 S2 抽出来的独立通知点）。
///
/// 为什么不继续挂在窗口类上（S4 之前是 <c>SettingsWindow.ViewOptionsChanged</c>）：
/// 现在的**改动来源**是页面（<c>SettingsPage</c>）、**订阅方**也是页面（<c>RoomPage</c>），
/// 中间若还隔着一个 Window 的静态事件，删窗口那天两边接线会一起断。
/// 抽成独立的小静态类后，页面只依赖它，与窗口的生死彻底解耦（架构 §6.2）。
///
/// v2.6.0 S4（T45）：旧窗口连同它那个同名事件已经删除，**全仓只剩这一处广播点** ——
/// 这正是当初抽出来的目的，现在兑现了。
/// </summary>
public static class ViewOptions
{
    /// <summary>玩家列表 / 房间消息 / 操作面板三项显隐被改动。</summary>
    public static event Action? Changed;

    /// <summary>广播一次「三栏显隐变了」（由设置页在用户改动或恢复默认后调用）。</summary>
    public static void Notify() => Changed?.Invoke();

    /// <summary>
    /// 登记订阅者。**先减后加**是幂等的写法：即使调用方因为某条路径漏了退订，
    /// 也不会把同一个处理器登记两次（两次登记会让一次改动触发两次重排）。
    /// </summary>
    public static void Subscribe(Action handler)
    {
        Changed -= handler;
        Changed += handler;
    }

    /// <summary>注销订阅者（页面 <c>OnLeave</c> 必须调用，否则页面反复进出会累积订阅）。</summary>
    public static void Unsubscribe(Action handler) => Changed -= handler;
}
