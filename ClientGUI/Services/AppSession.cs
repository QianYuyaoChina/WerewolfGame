using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Services;

/// <summary>
/// 应用级会话容器（架构 D5）：**唯一**一份 <see cref="GameClient"/> 与它上面挂着的
/// 长生命周期视图模型，由外壳窗口持有、活到进程退出。
///
/// 为什么必须独立出来：页面化之后「登录页 → 列表页 → 房间页」会被反复创建 / 销毁，
/// 而连接是**进程级资源** —— 一旦被页面生命周期带走，退房间就会断连（I1 立破）。
/// 所以页面只拿 <see cref="AppSession"/>，永远不自己 <c>new GameClient()</c>。
///
/// 归属表（与架构 §6.1 一一对应）：
/// <list type="bullet">
///   <item><see cref="Client"/> / <see cref="Login"/> / <see cref="Embedded"/>：全程唯一，页面切换不重建；</item>
///   <item><see cref="Navigation"/>：全程唯一（页面栈本身就是全局状态）；</item>
///   <item><c>RoomListViewModel</c> / <c>GameViewModel</c>：**页面**所有，随页面进出创建 / 销毁。</item>
/// </list>
/// </summary>
public sealed class AppSession
{
    /// <summary>
    /// 登录视图模型（全程唯一）：它内部持有唯一的 <see cref="GameClient"/>，
    /// 也持有用户填写的地址 / 凭据与「一键开服」共享状态条 —— 页面重建不能丢。
    /// </summary>
    public LoginViewModel Login { get; }

    /// <summary>内置服务器视图模型（全程唯一）：运行信息要跨页可见，3 秒轮询不能随页面停掉。</summary>
    public EmbeddedServerViewModel Embedded { get; }

    /// <summary>页面栈（全程唯一）。</summary>
    public NavigationService Navigation { get; }

    /// <summary>页面工厂（全程唯一，持有本会话）。</summary>
    public IPageFactory PageFactory { get; }

    /// <summary>唯一的连接（对外只读；没有「换一个 client」的通道）。</summary>
    public GameClient Client => Login.Session;

    public AppSession()
    {
        Login = new LoginViewModel();
        Embedded = new EmbeddedServerViewModel(Login);
        PageFactory = new PageFactory(this);
        Navigation = new NavigationService(PageFactory);
    }
}
