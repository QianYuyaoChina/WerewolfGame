namespace ClientGUI.Services;

/// <summary>
/// 「创建房间」浮层页的返回值（v2.6.0 S2）。
///
/// 职责切分（与旧 <c>CreateRoomWindow</c> 的 <c>DialogResult</c> + 三个只读属性等价）：
/// <list type="bullet">
///   <item>浮层页只负责**收集输入**（游戏 / 房间名 / 可选密码），不碰任何网络调用；</item>
///   <item>真正的建房 + 进房仍由 <c>RoomListViewModel.CreateAndJoinAsync</c> 完成 ——
///         连接、列表状态、忙碌态都留在列表页，浮层页因此不需要认识连接细节。</item>
/// </list>
/// </summary>
/// <param name="RoomName">房间名（可留空 → 服务端回退默认名「用户名 的房间」）。</param>
/// <param name="Password">房间密码；<c>null</c> = 公开房间（旧窗口的「留空即公开」原样保留）。</param>
/// <param name="GameId">游戏机器名（默认狼人杀）。</param>
public sealed record CreateRoomResult(string RoomName, string? Password, string GameId);
