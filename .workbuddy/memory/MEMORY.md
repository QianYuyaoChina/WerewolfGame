# Werewolf.Server 项目长期记忆

## 项目结构（4 个项目 + 1 测试宿主）
- `WerewolfGame/`：ASP.NET Core + SignalR 服务端（程序集 `Werewolf.Server`，net8.0），`http://0.0.0.0:5000`，Hub 路径 `/gamehub`
- `Werewolf.Client.Share/`：共享库 —— **协议与网络层唯一来源**，两端禁止各自定义协议
- `ClientGUI/`：WPF 客户端（net8.0-windows），手写 MVVM
- `Werewolf.Client/`：控制台客户端
- `ClientGUI.Tests/`：自带断言的最小测试宿主（纯 exe，无测试框架）

## 服务端架构（v2.0 多房间）
- 两层：**注册表 `RoomManager`**（`ConcurrentDictionary` 存 `_rooms`/`_connRoom`/`_connUser`，无后台线程，可 new 便于测试）
  + **房间实例 `GameRoom`**（各自 `_sync` 锁）。**无单例房间**。
- 空房回收由 `RoomRecycleService : BackgroundService` 驱动（`EmptyGraceSeconds=60s`、扫描 10s）。
- 隔离靠「`EncryptedChannel` 按 connId 单发 + `GameRoom.Broadcast*` 只遍历本房间玩家」。
- `OnDisconnectedAsync` 顺序固定：`_sessions.Remove` → `_rooms.LeaveByConnection` → 广播。
- **房主交接**：按**加入顺序**让给下一位（优先在线），广播「👑 X 成为新房主」；局中掉线仍保留名单。
- **房间保留**：房主可选 `Persistent=true`（留列表、人数 0，**永不回收**）或 `CloseRoom()`（立即移除）；掉线空房走宽限期回收，两条路径互不干涉。
- 账号 `AccountStore`（SHA256 + 快速幂，`v2:` 前缀）；房间密码 `RoomPassword.Hash(roomId,pwd)`（带房间号盐，仅内存）。

## 网络协议铁律
- 业务**全部**走单一加密入口 `Call`（`RpcRequest{Op,Payload}` / `RpcReply{Ok,Msg,Payload}`）；
  新增功能 = 新增 `Ops` 常量 + Payload，**不新增 SignalR 方法**（对外只有 `Handshake`/`Confirm`/`Call`）。
- 握手：PSK 认证 DH（`HandshakeCrypto`），`Call` 前必须 `Confirm`。
- 业务异常一律 `return RpcReply.Error(...)`，**不要抛 `HubException`**。
- 错误文案统一放 `ServerMessages`，客户端只显示服务端 `Msg`。
- `GameRules`（时长/夜晚顺序/人数上下限）来自 `appsettings.json`。
  **坑**：配置绑定器会**追加**到集合属性 → 集合初值必须留空，缺省在 `ResolveNightOrder` 补齐。
  规则**不在通用 DTO**，而在游戏私有 payload（狼人杀由模块 `IGameConfig.GetSection<GameRules>("GameRules")` 自取）。
- `leaveroom` 带 `LeaveRoomPayload{KeepRoom}`（仅「自己是房主且房间已空」时生效）。
- `roomstate`（`Ops.State`）：**界面打开时必须主动拉一次状态**（进房广播发生在新界面创建前，没人接收）；
  只靠推送会出现空白界面。`GameClient.LastState` 供即时回填。
- `gamelist`（`Ops.GameList`）：服务器可用游戏列表，创建房间下拉与图鉴共用。
- **`GameStateDto` = 通用信封 + 游戏私有负载**：通用字段只有 `GameId/RoomId/RoomName/HostName/Running/Status/YourId/IsAlive/Players`（+ 游戏名/图标/主题色），玩法字段进 `JsonNode? GamePayload`。
  `PlayerDto.Tag/TagColor` = 游戏自定义标签（狼人杀 = 身份中文名 + hex）。
- **房主设置（v2.1）**：op `hostsettings` / `roomsettings` / `resetinvite`。游戏私有配置是**不透明 JSON**（`IGameContext.GameSettingsJson`），由游戏自解释（狼人杀 = `RoleLayout`）。
  `IGameContext.MaxPlayers` 是房间**实际**上限（房主可在游戏上限内调小）。
- **邀请码 = 邀请令牌，不是密码**（v2.1 起）：`InvitePayload{Url,RoomId,InviteToken?,RoomName?}`；令牌在房间生命周期内稳定 → **改密码后旧码仍有效**，只有 `RotateInviteToken()` 作废。`joinroom` 带令牌匹配时**跳过密码校验**。
- **身份布局 `RoleLayout`（v2.1/v2.2）**：`Dictionary<string,int>`（键 = `Roles.*` 中文名）。`Default(n)` 按人数推导（神职随人数递增 4人1神→12人6神2民）。
  **坑**：不要 `Clamp(4,12)`（管理员可配最少 3 人，clamp 会让 3 人局拿到 4 个身份而被开局校验拦下）。
  `ValidateForGame(n)` 严格校验；`Describe()` 供开局公布。**开局前必须重读布局**。
- **反作弊铁律**：一切以**服务端校验**为准。已覆盖：未开局/不在房间/非房主/阶段与 op 不匹配/目标已出局或不存在/**不能投票给自己**/非法身份布局。新增校验要同步 `AntiCheatTests.cs` 与 `NewRolesProbe`。
- **op 路由**：框架只认识 `join/roomlist/gamelist/createroom/joinroom/leaveroom/roomstate/start/chat/ping`；其余走 `DispatchAsync` 的 **default 分支**透传给游戏实例，**框架不认识任何具体游戏 op**。
- **邀请码** `InviteCode.cs`：`WW1:` + Base64Url(AES-GCM)，密钥 `HKDF(PSK,"werewolf-invite-v1")`，含 `Url+RoomId+Password?+RoomName?`，**不含账号**。

## 多游戏可插拔架构（v2.0 核心）
**一句话**：房间只负责**承载**（房间号/密码/房主/玩家/聊天/邀请码/重连/回收），**玩法**由可插拔模块提供；依赖单向：平台 → 游戏**接口**，平台**永不** `using` 具体游戏类型。
- 已接入：`werewolf`（狼人杀）、`undercover`（谁是卧底，v2.3）。接入谁是卧底时**平台代码零改动**、`Program.cs` 未动。
- 契约在 `WerewolfGame/Games/`：`IGameModule`（元数据+工厂）、`IGameInstance`（`Start/RunAsync/HandleAction/BuildPayload/DecoratePlayers/Reset/Info/Status` + 可选 `OnPlayerJoined/Left/Reconnected/OnPlayerChat`）、`IGameContext`（玩家快照/广播/私聊/`BroadcastStateAsync`/`Config`/`MaxPlayers`）、`IGameConfig`。
- 发现：`GameCatalog` + `GameDiscovery.Discover(assembly)` **反射收集**公共无参构造的 `IGameModule`。
- 元数据真源：`Werewolf.Client.Share.GameIds` + `GameInfo{GameId,DisplayName,Emoji,Tagline,MinPlayers,MaxPlayers,RulesText,AccentHex,HasPlayerConfig}`（`ToString() => Label`）。
- **新增游戏只需 5 类接入点**（`docs/multigame/ADDING-A-GAME.md`）：①元数据模块 ②游戏逻辑实例 ③本游戏 ops 协议 ④GUI 面板 + 控制台模块（注册点）⑤可选规则说明。
  另有两个可选钩子：⑥`AccentHex/HasPlayerConfig` 房间标识 ⑦`OnPlayerChat` 接管发言。
- 客户端插槽：`GameViewModel` 是纯宿主；玩法放 `ViewModels/Games/<Game>/` + `Views/Games/<Game>Panel.xaml`，由 `GameViewModelFactory` 装载、`Themes/GamePanels.xaml` 渲染；未知游戏走 `UnknownGamePanel`。控制台同理 `ConsoleGameRegistry`。
- **锁序铁律**：恒定「房间锁 → 游戏锁」；持游戏锁期间**绝不**调用任何 `IGameContext` 方法。
- 兼容：`createroom` 缺 `GameId` → 回退 `werewolf`；未知 → 拒绝「该游戏不可用」。
- 已知烙印（非耦合）：命名空间仍叫 `Werewolf.*`、盐前缀 `werewolf::`（改盐会作废现有哈希）。

## 谁是卧底（v2.3 起）
- 词库 `words.undercover.txt`（每行 `平民词|卧底词[|分类]`，`#` 注释；路径 `Undercover:WordFile`）。
  **v2.3.3 起编译期内嵌**：`Werewolf.Server.csproj` 用 `<EmbeddedResource LogicalName="words.undercover.txt">`
  （**必须写死 LogicalName**，否则会带根命名空间前缀）+ `CopyToOutputDirectory=PreserveNewest`。
  优先级铁律：**工作目录已有文件 > 内嵌默认 > `SampleContent`**；用户改过的词库**永不被覆盖**，删了才重新生成。
  只读目录走 `LoadOrDefault()` 直接用内嵌那份、不落盘。解析统一走 `ParseLines()`（别写第二份解析）。
- 描述阶段用**聊天框**发言；v2.3.1 起**按顺序轮流发言**（`OnPlayerChat` 只放行当前发言人，另有 op `uc_skip` 跳过）。
- v2.3.2：进行中**不下发本人阵营**（只给词），结算才揭晓；人数达阈值加**白板**（无词，计卧底阵营）。
- 发牌配额是纯函数 `ComputeRoleCounts(n, rules) -> (卧底, 白板)`（发牌与测试共用）；铁律 `平民 > 卧底 + 白板`。
- **坑**：`_up` 里的人是 `SnapshotPlayers()` 用默认角色「平民」补出来的，所以任何「未发牌就按角色渲染」的分支
  （`DecoratePlayers` 的 Tag、`BuildPayload` 的 reveal）都会**未开局就显示「平民」**；用 `_dealt` 标志挡住，两处一起改。
- `UndercoverText`（在 `UndercoverOps.cs`）是双端 + 服务端统一文案源，客户端不自己拼。
- 卧底数按人数 1/2/3；铁律 `平民 > 卧底 + 白板`。出局者身份当场公开，词到整局结束才揭晓。

## 内置服务器（v2.4.0）：服务端宿主进客户端进程
- 客户端（GUI + 控制台）可选「创建内置服务器 / 连接现有服务器」；GUI 登录窗口是**两个标签页**。
  关窗自动停服；**在房间里时走正规「退出房间」**（复用 `GameViewModel.LeaveRoomAsync()`，
  只把 `ConfirmKeepRoom` 置成 `()=>false`）。
- 新增：`WerewolfGame/` 的 `ServerBootstrap`（**唯一组合根**，无 Console 输出）、`EmbeddedServerHost`、
  `EmbeddedServerPaths`；`ClientGUI/Services/` 的 `EmbeddedServerService`、`AppShutdown`；
  `ClientGUI/ViewModels/EmbeddedServerViewModel`；`Werewolf.Client/EmbeddedServerConsole`。
  `Program.cs` 退化为「打印自愈提示 → Build → 打印原 5 行 → Run」，**独立服务端输出逐行不变**。
- **WPF/控制台可直接 P2P 到 `Sdk.Web` 项目**（FrameworkReference 经 P2P 传递，无需手写），无 Main 冲突。
  抽 `Werewolf.Server.Hosting` 中间层实测**无效**（产物照样流过来）→ 否决。
- **客户端禁止 `using Microsoft.AspNetCore.*`**（客户端没有 ASP.NET Core 隐式 using → CS1061）。
- **端口**：固定 5000，被占时 5001–5100 漂移并告知；**先绑定失败再换**（不先探测，避免 TOCTOU）；
  绑定失败后 `app.Urls` 会抛 ObjectDisposedException → **实际端口从循环变量取**。
- **工作目录靠切 CWD**：`WebApplication` 不会自动切 CWD，而词库加载依赖它 → 切是唯一不给具体游戏
  开后门的办法。目录 `AppContext.BaseDirectory\embedded-server`（只读回退 `%LOCALAPPDATA%`）。
- **收尾顺序：退出房间 → 断自己的连接 → 停服务器**（断连接必须在停服前，否则 Kestrel 优雅停止耗满 5s）。
  唯一入口 `App.OnExit` + `Interlocked` 幂等；`Closing` 只做**可取消**的二次确认（取消必须完全无损）。
- **邀请码地址取自 `GameClient.Endpoint`** → 三处地址（自动连接/界面显示/邀请码）必须**同源**；
  自连回落 127.0.0.1 时用 `GameClient.InviteUrlOverride` 保住分享出去的码仍是局域网地址。
- **坑**：`TcpListener(IPAddress.Loopback, port)` **不能**模拟端口占用（与绑 0.0.0.0 的 Kestrel 不冲突），
  测漂移要用真实内置服务器当占用者；清理 target 必须同时挂 `Build;Publish` 且 `$(OutDir)`/`$(PublishDir)`
  各删一遍，否则 publish 仍脏。

## 构建与验证
- `dotnet build WerewolfGame.sln`，**对警告零容忍**（Nullable 开启，必须 0/0）。
- 测试宿主（`ClientGUI.Tests/bin/Debug/net8.0-windows` 下 `dotnet ClientGUI.Tests.dll`）：
  默认（逻辑/加密/规则/多房间/扩展性）· `--ui` · `--server <url>` · `--rooms` · `--lifecycle`（需小局配置）·
  `--hostinvite` · `--reconnect [秒]` · `--protocol`（需**标准**配置 40/45/45/30、MinPlayers=4）· `--newroles`（需小局）· `--undercover` · `--hold`/`--bots`。
  基线：默认 **1350** 项（v2.4.0）、UI 冒烟 **93** 项、`--rooms` 63、`--protocol` 44、`--lifecycle` 15。
  另有 `--embedded` 32、`--embeddedqa` 50（内置服务器）。

## 本机环境坑（实测）
- `.exe`（apphost）跑不起来，一律 `dotnet <dll>`。
- **e2e 的 url 必须带 `/gamehub`**（只写 `http://localhost:5000` 会 404）。
- **配置自愈**（v2.3）：`ConfigBootstrap.Ensure()` 在 `CreateBuilder` **之前**调用 —— 缺失即生成默认配置，损坏先备份 `appsettings.json.bad-时间戳` 再重建。
  **新增配置节点要同步 `ConfigBootstrap.DefaultJson` 与仓库 `appsettings.json`（两份）**。
- 访问 localhost 前先 `unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy`。
- 服务端在**临时目录**跑（避免污染 `WerewolfGame/accounts.json`）。停服务端只杀监听 5000 的 PID，**不要** `taskkill /IM dotnet.exe`。
- 构建时若后台服务端还开着，会因 dll 被占报 MSB3027 —— 先停服务端再构建。

## WPF 坑
- 窗口/UserControl **根元素** Background/Foreground 必须 `DynamicResource`。
- 每个窗口自己合并 `pack://application:,,,/ClientGUI;component/Themes/Panels.xaml`。
- `PasswordBox.Password` 不可绑定，用 `PasswordChanged` 同步。
- 不要在 `ItemsControl` 内用 `DataTrigger` 监听自身 `Count`（无限测量递归）。
- 未 `Show()` 的窗口没有呈现源，Popup 不实例化内部项模板。
- **菜单**：`Menu.ItemContainerStyle` 只作用第一层 → 下拉项会落回浅色系统外观；解法是在 `Controls.xaml` 注册 `MenuItem`/`Menu`/`Separator` **隐式样式**。`--ui` 会遍历菜单树断言每层 Template。
- **设置入口**：`Views/SettingsWindow`（标签页：外观/主界面/上次使用），`Ctrl + ,` 全局打开（`App` 用 `RegisterClassHandler` + PreviewKeyDown）；判定抽成 `App.IsSettingsShortcut` 纯函数。
- **内置控件都要自己主题化**：`Menu/MenuItem/Separator/TabControl/TabItem/ScrollBar/ComboBox` 默认是浅色系统外观。
- **ComboBox 点击层**：覆盖全区的透明 `ToggleButton` **必须给空模板**，否则系统浅色外观盖在内容上。
- **别只依赖显示模板**：自定义类型会回退 `ToString()`（`GameInfo` 因此重写为 `Label`）。
- **XAML 里别给会立即触发事件的属性设初值**（`IsChecked="True"` 会在 `InitializeComponent` 期间触发 → NRE），改在构造里 `_suppress` 保护下赋值。
- **周期刷新不要完整回填**：`HostSettingsWindow` 每 3 秒轮询；轮询只刷新只读展示（`RefreshFromServerState`），可编辑控件只在首次加载与保存后回填（`ApplySettings`）。

## 主题系统（深/浅色 + 跟随系统）
- 结构：`Colors.Dark.xaml`/`Colors.Light.xaml`（**颜色唯一定义处，键名两套必须一致**）、`Converters.xaml`、`Controls.xaml`（零颜色字面量）、`Panels.xaml`。
- **颜色一律 `{DynamicResource XxxBrush}`**，禁止硬编码 `#RRGGBB`、也不用 `StaticResource`（换主题不生效；只有样式键用 StaticResource）。
- 颜色挂在 **Application 级**，由 `ThemeManager.Initialize()`（`App.OnStartup`，早于任何窗口）插入；`SetTheme` 只换槽位 → 已开窗口立刻换色。
- 「跟随系统」读 `HKCU\...\Themes\Personalize\AppsUseLightTheme` + 4 秒轮询；偏好存 `ui-state.json`。
- **新增颜色两套都加**：`--ui` 会扫描所有 XAML 引用的 `*Brush` 键，漏加即失败。
- 已知限制：菜单 Popup 投影色仍是黑色字面量；Win32 标题栏不随主题。见 `docs/ui/THEMING.md`。
- **剪贴板**：全局独占，被占用抛 `CLIPBRD_E_CANT_OPEN`；走 `ClipboardHelper.TrySetText`（外层只重试 2 次，否则卡几秒）。
  **不要窗口打开时自动复制**；**凡要用户拿走文本一律用可选中文本的独立窗口，不用 MessageBox**。

## 团队协作（software-company SOP）
- 判定：**小需求/明确 Bug → 快速模式/BugFix 路径**（TeamCreate → 工程师 → QA）；中大型 → PM PRD → 架构师设计+任务分解 → 工程师 → QA。
- 主理人只做编排与汇编，**不代写成员产出**；跨成员信息流必须经主理人中转。
- 成员 Agent 名固定：`software-product-manager` / `software-architect` / `software-engineer` / `software-qa-engineer`。
- 文档落 `docs/<专题>/PRD.md`、`docs/<专题>/ARCHITECTURE.md`；裁定与验收追加到架构文档末尾。
