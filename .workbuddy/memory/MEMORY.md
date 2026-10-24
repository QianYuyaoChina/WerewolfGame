# Werewolf.Server 项目长期记忆

## 项目结构（4 项目 + 1 测试宿主）
- `WerewolfGame/`：ASP.NET Core + SignalR 服务端（程序集 `Werewolf.Server`，net8.0），`http://0.0.0.0:5000`，Hub `/gamehub`
- `Werewolf.Client.Share/`：共享库 —— **协议与网络层唯一来源**，两端禁止各自定义协议
- `ClientGUI/`：WPF 客户端（net8.0-windows），手写 MVVM
- `Werewolf.Client/`：控制台客户端
- `ClientGUI.Tests/`：自带断言的最小测试宿主（纯 exe，无测试框架）

## 服务端架构（v2.0 多房间）
- 两层：**注册表 `RoomManager`**（`ConcurrentDictionary` 存 `_rooms`/`_connRoom`/`_connUser`，无后台线程，可 new 便于测试）+ **房间实例 `GameRoom`**（各自 `_sync` 锁）。**无单例房间**。
- 空房回收由 `RoomRecycleService : BackgroundService` 驱动（`EmptyGraceSeconds=60s`、扫描 10s）。
- 隔离靠「`EncryptedChannel` 按 connId 单发 + `GameRoom.Broadcast*` 只遍历本房间玩家」。
- `OnDisconnectedAsync` 顺序固定：`_sessions.Remove` → `_rooms.LeaveByConnection` → 广播。
- **房主交接**：按**加入顺序**让给下一位（优先在线），广播「👑 X 成为新房主」；局中掉线仍保留名单。
- **房间保留**：房主可选 `Persistent=true`（留列表、人数 0，**永不回收**）或 `CloseRoom()`（立即移除）；掉线空房走宽限期回收，两条路径互不干涉。
- 账号 `AccountStore`（SHA256 + 快速幂，`v2:` 前缀）；房间密码 `RoomPassword.Hash(roomId,pwd)`（带房间号盐，仅内存）。

## 网络协议铁律
- 业务**全部**走单一加密入口 `Call`（`RpcRequest{Op,Payload}` / `RpcReply{Ok,Msg,Payload}`）；新增功能 = 新增 `Ops` 常量 + Payload，**不新增 SignalR 方法**（对外只有 `Handshake`/`Confirm`/`Call`）。
- 握手：PSK 认证 DH（`HandshakeCrypto`），`Call` 前必须 `Confirm`。
- 业务异常一律 `return RpcReply.Error(...)`，**不要抛 `HubException`**。错误文案统一放 `ServerMessages`，客户端只显示服务端 `Msg`。
- `GameRules` 来自 `appsettings.json`。**坑**：配置绑定器会**追加**到集合属性 → 集合初值必须留空，缺省在 `ResolveNightOrder` 补齐。规则**不在通用 DTO**，而在游戏私有 payload。
- `leaveroom` 带 `LeaveRoomPayload{KeepRoom}`（仅「自己是房主且房间已空」时生效）。
- `roomstate`（`Ops.State`）：**界面打开时必须主动拉一次状态**（进房广播发生在新界面创建前，没人接收）。`GameClient.LastState` 供即时回填。
- `gamelist`（`Ops.GameList`）：可用游戏列表，创建房间下拉与图鉴共用。
- **`GameStateDto` = 通用信封 + 游戏私有负载**：通用字段只有 `GameId/RoomId/RoomName/HostName/Running/Status/YourId/IsAlive/Players`（+ 游戏名/图标/主题色），玩法字段进 `JsonNode? GamePayload`。`PlayerDto.Tag/TagColor` = 游戏自定义标签。
- **房主设置（v2.1）**：op `hostsettings`/`roomsettings`/`resetinvite`。游戏私有配置是**不透明 JSON**（`IGameContext.GameSettingsJson`）。`IGameContext.MaxPlayers` 是房间**实际**上限。
- **邀请码 = 邀请令牌，不是密码**（v2.1 起）：`InvitePayload{Url,RoomId,InviteToken?,RoomName?}`；令牌在房间生命周期内稳定 → **改密码后旧码仍有效**，只有 `RotateInviteToken()` 作废。`joinroom` 带令牌匹配时**跳过密码校验**。
- **身份布局 `RoleLayout`**：`Dictionary<string,int>`（键 = `Roles.*` 中文名）。`Default(n)` 按人数推导。
  **坑**：不要 `Clamp(4,12)`（管理员可配最少 3 人，clamp 会让 3 人局拿到 4 个身份被开局校验拦下）。`ValidateForGame(n)` 严格校验；`Describe()` 供开局公布。**开局前必须重读布局**。
- **反作弊铁律**：一切以**服务端校验**为准。已覆盖：未开局/不在房间/非房主/阶段与 op 不匹配/目标已出局或不存在/**不能投票给自己**/非法身份布局。新增校验要同步 `AntiCheatTests.cs` 与 `NewRolesProbe`。
- **op 路由**：框架只认识 `join/roomlist/gamelist/createroom/joinroom/leaveroom/roomstate/start/chat/ping`；其余走 `DispatchAsync` 的 **default 分支**透传给游戏实例，**框架不认识任何具体游戏 op**。
- `InviteCode.cs`：`WW1:` + Base64Url(AES-GCM)，密钥 `HKDF(PSK,"werewolf-invite-v1")`，含 `Url+RoomId+Password?+RoomName?`，**不含账号**。

## 多游戏可插拔架构（v2.0 核心）
**一句话**：房间只负责**承载**（房间号/密码/房主/玩家/聊天/邀请码/重连/回收），**玩法**由可插拔模块提供；依赖单向：平台 → 游戏**接口**，平台**永不** `using` 具体游戏类型。
- 已接入：`werewolf`、`undercover`（v2.3）。接入谁是卧底时**平台代码零改动**、`Program.cs` 未动。
- 契约在 `WerewolfGame/Games/`：`IGameModule`（元数据+工厂）、`IGameInstance`（`Start/RunAsync/HandleAction/BuildPayload/DecoratePlayers/Reset/Info/Status` + 可选 `OnPlayerJoined/Left/Reconnected/OnPlayerChat`）、`IGameContext`（玩家快照/广播/私聊/`BroadcastStateAsync`/`Config`/`MaxPlayers`）、`IGameConfig`。
- 发现：`GameCatalog` + `GameDiscovery.Discover(assembly)` **反射收集**公共无参构造的 `IGameModule`。
- 元数据真源：`Werewolf.Client.Share.GameIds` + `GameInfo{...}`（`ToString() => Label`）。
- **新增游戏只需 5 类接入点**（`docs/multigame/ADDING-A-GAME.md`）：①元数据模块 ②游戏逻辑实例 ③本游戏 ops 协议 ④GUI 面板 + 控制台模块（注册点）⑤可选规则说明。
- 客户端插槽：`GameViewModel` 是纯宿主；玩法放 `ViewModels/Games/<Game>/` + `Views/Games/<Game>Panel.xaml`，由 `GameViewModelFactory` 装载、`Themes/GamePanels.xaml` 渲染；未知游戏走 `UnknownGamePanel`。控制台同理 `ConsoleGameRegistry`。
- **锁序铁律**：恒定「房间锁 → 游戏锁」；持游戏锁期间**绝不**调用任何 `IGameContext` 方法。
- 兼容：`createroom` 缺 `GameId` → 回退 `werewolf`；未知 → 拒绝「该游戏不可用」。
- 已知烙印（非耦合）：命名空间仍叫 `Werewolf.*`、盐前缀 `werewolf::`（改盐会作废现有哈希）。

## 谁是卧底（v2.3 起）
- 词库 `words.undercover.txt`（每行 `平民词|卧底词[|分类]`，`#` 注释；路径 `Undercover:WordFile`）。
  **v2.3.3 起编译期内嵌**：`Werewolf.Server.csproj` 用 `<EmbeddedResource LogicalName="words.undercover.txt">`（**必须写死 LogicalName**）+ `CopyToOutputDirectory=PreserveNewest`。
  优先级铁律：**工作目录已有文件 > 内嵌默认 > `SampleContent`**；用户改过的词库**永不被覆盖**，删了才重新生成。只读目录走 `LoadOrDefault()`。解析统一走 `ParseLines()`。
- 描述阶段用**聊天框**发言；v2.3.1 起**按顺序轮流发言**（`OnPlayerChat` 只放行当前发言人，另有 op `uc_skip`）。
- v2.3.2：进行中**不下发本人阵营**（只给词），结算才揭晓；人数达阈值加**白板**。
- 发牌配额是纯函数 `ComputeRoleCounts(n, rules)`（发牌与测试共用）；铁律 `平民 > 卧底 + 白板`。
- **坑**：`_up` 里的人是 `SnapshotPlayers()` 用默认角色「平民」补出来的 → 任何「未发牌就按角色渲染」的分支（`DecoratePlayers` 的 Tag、`BuildPayload` 的 reveal）都会**未开局就显示「平民」**；用 `_dealt` 标志挡住，两处一起改。
- `UndercoverText`（在 `UndercoverOps.cs`）是双端 + 服务端统一文案源。

## 内置服务器（v2.4.0）
- 客户端（GUI + 控制台）可选「创建内置服务器 / 连接现有服务器」；GUI 登录窗口是**两个标签页**。关窗自动停服；**在房间里时走正规「退出房间」**。
- 新增：`WerewolfGame/` 的 `ServerBootstrap`（**唯一组合根**，无 Console 输出）、`EmbeddedServerHost`、`EmbeddedServerPaths`；`ClientGUI/Services/` 的 `EmbeddedServerService`、`AppShutdown`；`ClientGUI/ViewModels/EmbeddedServerViewModel`；`Werewolf.Client/EmbeddedServerConsole`。
- **WPF/控制台可直接 P2P 到 `Sdk.Web` 项目**（FrameworkReference 经 P2P 传递，无需手写），无 Main 冲突。抽 `Werewolf.Server.Hosting` 中间层实测**无效** → 否决。
- **客户端禁止 `using Microsoft.AspNetCore.*`**（没有 ASP.NET Core 隐式 using → CS1061）。
- **端口**：固定 5000，被占时 5001–5100 漂移并告知；**先绑定失败再换**（不先探测，避免 TOCTOU）；绑定失败后 `app.Urls` 会抛 ObjectDisposedException → **实际端口从循环变量取**。
- **工作目录靠切 CWD**：`WebApplication` 不会自动切 CWD，而词库加载依赖它 → 切是唯一不给具体游戏开后门的办法。目录 `AppContext.BaseDirectory\embedded-server`（只读回退 `%LOCALAPPDATA%`）。
- **收尾顺序：退出房间 → 断自己的连接 → 停服务器**（断连接必须在停服前，否则 Kestrel 优雅停止耗满 5s）。唯一入口 `App.OnExit` + `Interlocked` 幂等；`Closing` 只做**可取消**的二次确认。
- **邀请码地址取自 `GameClient.Endpoint`** → 三处地址（自动连接/界面显示/邀请码）必须**同源**；自连回落 127.0.0.1 时用 `GameClient.InviteUrlOverride` 保住分享出去的码仍是局域网地址。
- **坑**：`TcpListener(IPAddress.Loopback, port)` **不能**模拟端口占用（与绑 0.0.0.0 的 Kestrel 不冲突），测漂移要用真实内置服务器当占用者；清理 target 必须同时挂 `Build;Publish` 且 `$(OutDir)`/`$(PublishDir)` 各删一遍。

## 单窗口页面外壳（v2.6.0 S1–S7）
- `ShellWindow` 是**唯一** `Window`；10 个页面是 `Views/Pages/*.xaml` 的 `UserControl`；`PageHost` 装页面。
- `NavigationService`：`Push`（换页，旧页 `OnLeave`）vs `PushOverlay`（浮层，下层**不** `OnLeave`，只 `IsHitTestVisible=false`）；`Reset` 清栈设根页。
- 等待者挂在 **Entry** 上（P2 修法）；`PushForResultAsync<T>` / `PushForDecisionAsync` / `Complete(result)` / `DismissOverlay()`。
- **`PopTo(target)`（S8）**：结论已定时的直弹通道 —— 不逐页问 `OnBackRequestedAsync`、幂等、目标不在栈里一格不弹、根页永不弹出。
  **退完房回列表一律走它**，别用 `BackAsync`（那会重入房间的返回拦截，`_backRequested` 等瞬时标志会把出栈吞掉）。
- **保留房间三出口**：`KeepRoomChoice { Keep, Close, Cancel }`。`null` 已被 I6 占用（"没显式选" ⇒ 保留），
  取消挤不进 `bool?`；翻译只有 `KeepRoomDecision.ResolveChoice` 一处，`Resolve(bool?)` 是它的薄封装。
- **锁与语义**：`CanGoBack` = 栈深>1 且栈顶允许；返回键 `Alt+←`/`Esc` 走 `NavKeyMap.Resolve` 单一入口。
- 关闭意图：`CloseIntent.Resolve`（纯函数）+ `ResolveShellClose` / `AfterConfirm` / `ShellCloseAction`；`ShutdownMode.OnExplicitShutdown` → **窗口关闭 ≠ 程序退出**，必须 `Application.Shutdown()`（S6）。
- `AppShutdown.ExitApplication`（测试宿主置 false，避免测试里关窗把进程带走）。
- **S7 转场动画**：`PageTransition`（`Resolve` 纯函数 + `Duration`/`Parallax`/`EnterEase`/`LeaveEase`），临时 `Grid` 装新旧两页，动画结束 `FinishTransition()` 把新页放回 `PageHost`。
  **重入铁律**：`PageHost.Content` 在动画期间是**临时 Grid**，连续两次导航会把它当成「旧页」→ 必须先 `FinishTransition()` 再取 `outgoing`。
- `UiStateStore.State.PageTransitionAnimation` 是动画开关（设置 → 主界面）。

## 构建与验证
- `dotnet build WerewolfGame.sln`，**对警告零容忍**（Nullable 开启，必须 0/0）。
- 测试宿主（`ClientGUI.Tests/bin/Debug/net8.0-windows` 下 `dotnet ClientGUI.Tests.dll`）：默认 · `--ui`（STA）· `--server <url>` · `--rooms` · `--lifecycle` · `--hostinvite` · `--reconnect` · `--protocol` · `--newroles` · `--undercover` · `--embedded` · `--embeddedqa` · `--egg` · `--hold`/`--bots`。
- 基线（v2.6.0 S7）：默认 **1854**、`--ui` **940**、`--embedded` **116**、`--embeddedqa` **50**、`--egg` **11**。**断言数只增不减**。

## 本机环境坑（实测）
- `.exe`（apphost）跑不起来，一律 `dotnet <dll>`。**e2e 的 url 必须带 `/gamehub`**。
- **配置自愈**（v2.3）：`ConfigBootstrap.Ensure()` 在 `CreateBuilder` **之前**调用。**新增配置节点要同步 `ConfigBootstrap.DefaultJson` 与仓库 `appsettings.json`（两份）**。
- 访问 localhost 前先 `unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy`。
- 服务端在**临时目录**跑；停服务端只杀监听 5000 的 PID，**不要** `taskkill /IM dotnet.exe`。构建前先停服务端（否则 MSB3027）。
- **6 位数字「显示诱饵」**：本环境工具输出会把连续 6 位数字渲染成诱饵值（真实 `1 2 3 4 5 6` 曾被显示成 `9 9 0 1 4 9`）。**核对数字一律走字节级**：`od -An -c`、`tr '0-9' 'A-J'`、`sha256sum`、或逐位空格。文档/消息里记录关键数字用逐位空格写法。已字节级确认：**内置管理员默认密码 = 1 2 3 4 5 6**（`Werewolf.Client.Share/AdminDefaults.cs`）。
- **构建报错判读**：`MSB3026`/`MSB3061`/`MSB3027`/`CS0006`/`BG1002`/`MC1000` 多为文件占用或 obj 缓存噪声，**先查进程/并发、等 10–20 秒重试**，不要当代码告警、更不要因此改代码。**不要用 `-p:BaseOutputPath` 分目录**（测试宿主按固定 `bin\Debug\net8.0-windows` 找程序集）。

## WPF 坑
- 窗口/UserControl **根元素** Background/Foreground 必须 `DynamicResource`；每个窗口自己合并 `Themes/Panels.xaml`。
- `PasswordBox.Password` 不可绑定，用 `PasswordChanged`。不要在 `ItemsControl` 内用 `DataTrigger` 监听自身 `Count`。未 `Show()` 的窗口没有呈现源，Popup 不实例化内部项模板。
- **内置控件都要自己主题化**（`Menu/MenuItem/Separator/TabControl/TabItem/ScrollBar/ComboBox`）；`Menu.ItemContainerStyle` 只作用第一层 → 需在 `Controls.xaml` 注册隐式样式。**ComboBox 点击层**的透明 `ToggleButton` **必须给空模板**。
- **别只依赖显示模板**：自定义类型会回退 `ToString()`（`GameInfo` 因此重写为 `Label`）。
- XAML 里别给会立即触发事件的属性设初值（`IsChecked="True"` 会在 `InitializeComponent` 期间触发 → NRE），改在构造里 `_suppress` 保护下赋值。
- **周期刷新不要完整回填**：轮询只刷新只读展示，可编辑控件只在首次加载与保存后回填。
- `UserControl` 构造要求 **STA**；`ShellWindow.Loaded` 会 `RestoreWindowState()` + `Navigation.Reset(LoginPage)` → **不能拿 ShellWindow 当测试宿主**（会重置内容）。

## 主题系统（深/浅色 + 跟随系统）
- 结构：`Colors.Dark.xaml`/`Colors.Light.xaml`（**颜色唯一定义处，键名两套必须一致**）、`Converters.xaml`、`Controls.xaml`（零颜色字面量）、`Panels.xaml`。
- **颜色一律 `{DynamicResource XxxBrush}`**，禁止硬编码 `#RRGGBB`、也不用 `StaticResource`（只有样式键用 StaticResource）。
- 颜色挂在 **Application 级**，由 `ThemeManager.Initialize()`（`App.OnStartup`，早于任何窗口）插入；`SetTheme` 只换槽位。
- 「跟随系统」读 `HKCU\...\Themes\Personalize\AppsUseLightTheme` + 4 秒轮询；偏好存 `ui-state.json`。**新增颜色两套都加**（`--ui` 会扫描所有 XAML 引用的 `*Brush` 键）。
- 已知限制：菜单 Popup 投影色仍是黑色字面量；Win32 标题栏不随主题。见 `docs/ui/THEMING.md`。
- **剪贴板**：全局独占，被占用抛 `CLIPBRD_E_CANT_OPEN`；走 `ClipboardHelper.TrySetText`。**不要窗口打开时自动复制**；**凡要用户拿走文本一律用可选中文本的独立窗口**。

## 团队协作（software-company SOP）
- 判定：**小需求/明确 Bug → 快速模式/BugFix 路径**（工程师 → QA）；中大型 → PM PRD → 架构师设计+任务分解 → 工程师 → QA。
- 主理人只做编排与汇编，**不代写成员产出**；跨成员信息流必须经主理人中转。
- 成员 Agent 名固定：`software-product-manager` / `software-architect` / `software-engineer` / `software-qa-engineer`。
- 文档落 `docs/<专题>/PRD.md`、`docs/<专题>/ARCHITECTURE.md`；裁定与验收追加到架构文档末尾。
- **用户会并行自行修改项目**：团队**禁止任何还原/回滚操作**（`git checkout` / `git restore` / `git stash`）；改文件前先重读；发现意料之外的改动绕开并上报，不要覆盖（2026-10-11 用户明确交代「除了你，我也会对项目进行修改，不要改回来」）。
- **禁止 git commit / push**（用户自行把控版本与变更日志）。
