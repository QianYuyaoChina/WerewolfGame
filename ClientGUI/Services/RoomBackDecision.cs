namespace ClientGUI.Services;

/// <summary>
/// 「房间页能不能出栈」的唯一判定（架构 N3）。
///
/// 房间页的返回**不是**纯导航：它等价于按下「退出房间」 —— 真的发了 <c>leaveroom</c>
/// 才允许出栈，失败就留在房间里（否则会出现「界面已经回到列表，服务端却还在房间里」的错位）。
///
/// 抽成纯函数（而不是写在页面的事件处理器里）有两个好处：
/// <list type="bullet">
///   <item>三种情形（未进房 / 退出成功 / 退出失败）都能被单测直接断言，不需要真实网络；</item>
///   <item>「未进房就不必调 leaveroom」这条短路也被钉死 —— 否则空房间会白发一次请求。</item>
/// </list>
/// </summary>
public static class RoomBackDecision
{
    /// <summary>
    /// 返回 <c>true</c> = 允许页面出栈。
    /// <paramref name="isJoined"/> 为 false 时**不调用** <paramref name="leaveAsync"/>（没房间可退，纯导航）。
    /// </summary>
    public static async Task<bool> ResolveAsync(bool isJoined, Func<Task<bool>> leaveAsync)
    {
        if (!isJoined) return true;
        if (leaveAsync == null) return false;

        return await leaveAsync().ConfigureAwait(true);
    }
}
