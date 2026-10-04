namespace Werewolf.Server;

/// <summary>
/// 房间相关的运行参数（从 <c>appsettings.json</c> 的 <c>Room</c> 节点读取，缺失时用内置默认值）。
/// </summary>
public sealed class RoomOptions
{
    /// <summary>空房宽限期（秒）：无在线玩家超过该时长后由回收服务移除。</summary>
    public int EmptyGraceSeconds { get; set; } = 60;

    /// <summary>服务器同时存在的房间上限；超限时 <c>createroom</c> 返回 <c>TooManyRooms</c>。</summary>
    public int MaxRooms { get; set; } = 100;

    /// <summary>后台回收扫描间隔（秒）。</summary>
    public int RecycleScanSeconds { get; set; } = 10;
}
