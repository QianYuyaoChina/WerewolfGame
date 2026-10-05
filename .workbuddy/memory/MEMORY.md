# Werewolf.Server 项目长期记忆

## 项目结构（4 个项目 + 1 测试宿主）
- `WerewolfGame/`：ASP.NET Core + SignalR 服务端（程序集名 `Werewolf.Server`，net8.0），监听 `http://0.0.0.0:5000`，Hub 路径 `/gamehub`
- `Werewolf.Client.Share/`：共享库（net8.0）——**协议与网络层的唯一来源**，两端禁止各自定义协议
- `ClientGUI/`：WPF 客户端（net8.0-windows），轻量手写 MVVM
- `Werewolf.Client/`：控制台客户端
- `ClientGUI.Tests/`：自带断言的最小测试宿主（纯 exe，无测试框架依赖）

## 服务端架构（v2.0 多房间）
- 两层结构：**注册表 `RoomManager`**（`ConcurrentDictionary` 存 `_rooms`/`_connRoom`/`_connUser`，无后台线程，可直接 new 便于测试）
  + **房间实例 `GameRoom`**（各自一把 `_sync` 锁，自持玩家/阶段/夜晚数据）。**不存在单例房间**。
- 空房回收由 `RoomRecycleService : BackgroundService` 驱动（`EmptyGraceSeconds=60s`、扫描 10s）。
- 房间隔离靠「`EncryptedChannel` 按 connId 单发 + `GameRoom.Broadcast*` 只遍历本房间玩家」，注册表不参与推送。
- `OnDisconnectedAsync` 清理顺序固定：`_sessions.Remove(connId)` → `_rooms.LeaveByConnection(connId)` → 广播。
- **房主交接**：房主离开/掉线时按**加入顺序**把房主让给下一位（优先仍在线的成员），并广播「👑 X 成为新房主」；
  对局中掉线仍保留在名单但房主照样转让。
- **房间保留**：房间已空时按房主意愿——`GameRoom.Persistent=true`（留在列表、人数 0、房主为空，**永不回收**，直到服务器退出）
  或 `RoomManager.CloseRoom(roomId)`（立即移除）。掉线留下的空房才走宽限期回收，两条路径互不干涉。
- 账号用 `AccountStore`（SHA256 + 快速幂混淆，`v2:` 前缀）；房间密码 `RoomPassword.Hash(roomId,pwd)`（带房间号盐，仅内存）。

## 网络协议铁律
- 业务**全部**走单一加密入口 `Call`（信封：`RpcRequest{Op,Payload}` / `RpcReply{Ok,Msg,Payload}`）；
  新增功能 = 新增 `Ops` 常量 + Payload，**不新增 SignalR 方法**（对外只有 `Handshake`/`Confirm`/`Call`）。
- 握手：PSK 认证的 DH（`HandshakeCrypto`），`Call` 前必须 `Confirm` 通过。
- 服务端业务异常一律 `return RpcReply.Error(...)`，**不要抛 `HubException`**（会走 SignalR 错误通道、破坏统一响应形状）。
- 错误文案统一放 `ServerMessages`（共享库），客户端只显示服务端返回的 `Msg`，不自行拼文案。
- `GameRules`（时长/夜晚顺序/人数上下限）来自 `appsettings.json` 的 `GameRules` 节点。
  **坑**：配置绑定器会**追加**到已有的集合属性，集合初值必须留空，缺省值放到 `ResolveNightOrder` 补齐。
  v2.0 起规则**不再放在通用 DTO**，而是狼人杀私有 payload 的字段（服务端由狼人模块经 `IGameConfig.GetSection<GameRules>("GameRules")` 自取）。
- `leaveroom` 带 `LeaveRoomPayload{KeepRoom}`（仅在「自己是房主且房间已空」时生效）。
- `roomstate`（`Ops.State`）：主动拉取「我视角的房间状态」。**界面必须在打开时主动拉一次状态**
  ——进房（createroom/joinroom）发生在新界面创建之前，那次广播没人接收；
  只靠推送会出现「玩家列表空白、房主标识丢失」的空白界面。`GameClient.LastState` 缓存最近一份状态供即时回填。
- `gamelist`（`Ops.GameList`）：查询「服务器当前可用游戏」（`GameListResult`），创建房间下拉与图鉴共用。
- **`GameStateDto` 是「通用信封 + 游戏私有负载」**：通用字段只有 `GameId/RoomId/RoomName/HostName/Running/Status/YourId/IsAlive/Players`，
  玩法字段一律进 `JsonNode? GamePayload`（狼人杀 = `WerewolfGameState`）。
  `PlayerDto.Tag/TagColor` 是游戏自定义标签（狼人杀 = 身份中文名 + 旧 RoleBrush 的 hex，用于保持观感）。
- **房主设置（v2.1）**：op `hostsettings`（房主拉设置，含**邀请令牌**）/ `roomsettings`（改人数/密码/游戏私有配置）/ `resetinvite`。
  平台只把「游戏私有配置」当**不透明 JSON 字符串**存（`IGameContext.GameSettingsJson`），由游戏自己解释（狼人杀 = `RoleLayout`）。
  `IGameContext.MaxPlayers` 是房间**实际**上限（房主可在游戏上限内调小），房间加入门禁与游戏开局校验都用它。
- **邀请码 = 邀请令牌，不是密码**（v2.1 起）：`InvitePayload{Url,RoomId,InviteToken?,RoomName?}`；
  `GameRoom._inviteToken` 房间生命周期内稳定 → **房主改密码后旧邀请码仍有效**；只有 `RotateInviteToken()`（房主显式重置）才作废。
  `joinroom` 带 `InviteToken` 且匹配时**跳过密码校验**。客户端断线重连优先用内存里的令牌（密码可能已被房主改掉）。
- **身份布局 `RoleLayout`（v2.1/v2.2）**：`Dictionary<string,int>`（键用 `Roles.*` 中文名）。
  `Default(n)` = **按人数推导的默认布局**（也是「跟随人数自动配置」的依据），v2.2 起改为**神职随人数递增**
  （4人1神→12人6神2民，平民稳定 2-3 个）。
  **坑**：不要把它 `Clamp(4,12)`——管理员可以把最少人数配到 3，clamp 会让 3 人局拿到 4 个身份的布局，
  被开局的「总数必须等于人数」校验自己拦下（`--lifecycle` 小局回归时踩到过）。
  `ValidateForGame(n)` 严格校验（总数 == 人数、狼≥1、狼<好人、警长≤1）；`Describe()` 供开局公布。
  **开局前必须重新读一次布局**（房主可能在建房后才设置）。
- **反作弊铁律**：一切操作以**服务端校验**为准，客户端提示不算数。已覆盖：未开局 / 不在房间 / 非房主 /
  阶段与 op 不匹配 / 目标已出局或不存在 / **不能投票给自己** / 非法身份布局。新增校验时记得同步
  `ClientGUI.Tests/AntiCheatTests.cs` 与 `NewRolesProbe`。
- **op 路由**：框架只认识 `join/roomlist/gamelist/createroom/joinroom/leaveroom/roomstate/start/chat/ping`；
  其余 op（`night/vote/hunter` 等）走 `DispatchAsync` 的 **default 分支**透传给当前游戏实例，**框架不认识任何具体游戏的 op**。
- **邀请码** = `Werewolf.Client.Share/InviteCode.cs`：`WW1:` + Base64Url(AES-GCM 信封)，
  密钥由 PSK 派生（`HKDF(PSK, info="werewolf-invite-v1")`），内容含 `Url + RoomId + Password? + RoomName?`，
  **不含账号**（拿码者仍需自己登录）。服务端对此零改动。

## 多游戏可插拔架构（v2.0，本轮核心）
**一句话**：房间只负责**承载**（房间号/密码/房主/玩家/聊天/邀请码/重连/回收），**玩法**由可插拔游戏模块提供；
依赖方向单向：平台 → 游戏**接口**；平台**永不** `using` 具体游戏类型。

- 服务端契约在 `WerewolfGame/Games/`：`IGameModule`（元数据 + 工厂）、`IGameInstance`（`Start/RunAsync/HandleAction/BuildPayload/DecoratePlayers/Reset/Info/Status`）、
  `IGameContext`（房间给游戏的能力门面：玩家快照/广播/私聊/`BroadcastStateAsync`/`Config`）、`IGameConfig`。
- 发现与注册：`GameCatalog`（`ConcurrentDictionary`）+ `GameDiscovery.Discover(assembly)` **反射收集**公共无参构造的 `IGameModule`
  → **新增游戏只需新增文件，`Program.cs` 不用改**。
- 游戏落地：狼人杀全部在 `WerewolfGame/Games/Werewolf/`（`WerewolfGameInstance`/`WerewolfGameModule`/`WerewolfPlayer`/`RoleInfo`/`WerewolfOps`）；
  私有协议在 `Werewolf.Client.Share/Games/`（`WerewolfGameState`/`WerewolfOps`）。
- 元数据唯一真源：`Werewolf.Client.Share.GameIds`（`werewolf`/`undercover`/`turtle_soup`）+ `GameInfo{GameId,DisplayName,Emoji,Tagline,MinPlayers,MaxPlayers,RulesText,Label}`。
- **新增一个游戏只需 5 类接入点**（见 `docs/multigame/ADDING-A-GAME.md`）：①登记元数据 ②实现游戏逻辑模块 ③声明本游戏操作协议
  ④提供本游戏操作面板（GUI 面板 + 控制台模块，**属注册点非平台模块**）⑤可选规则说明。
  房间管理 / 账号 / 邀请码 / 重连 / 主题 / 设置 / 大厅宿主逻辑**一律不改**（QA 已用自写第三个游戏 + 平台文件 md5 未变证实）。
- 客户端插槽：GUI `GameViewModel` 是纯宿主（房间/玩家/聊天/重连），玩法放在 `ViewModels/Games/<Game>/` + `Views/Games/<Game>Panel.xaml`，
  由 `GameViewModelFactory` 按 `GameId` 装载、`Themes/GamePanels.xaml` 的 DataTemplate 渲染；未知游戏走 `UnknownGamePanel`（不崩溃）。
  控制台同理：`ConsoleGameRegistry` + `Games/<Game>ConsoleModule.cs`。
- **锁序铁律**：恒定「房间锁 → 游戏锁」；持游戏锁期间**绝不**调用任何 `IGameContext` 方法（否则与后台主循环互锁）。
- 兼容性：`createroom` 缺失 `GameId` → 回退 `werewolf`；未知 → 拒绝并提示「该游戏不可用」。**本轮为破坏性协议变更，两端必须同步升级**。
- 已知烙印（非逻辑耦合）：程序集/命名空间仍叫 `Werewolf.*`、账号与房间密码盐前缀 `werewolf::`（改盐会作废现有哈希）。

## 构建与验证
- 编译：`dotnet build WerewolfGame.sln`，本项目**对警告零容忍**（`Nullable` 开启，必须 0 警告 0 错误）。
- 测试宿主（在 `ClientGUI.Tests/bin/Debug/net8.0-windows` 下用 `dotnet ClientGUI.Tests.dll`）：
  - 默认 → 纯逻辑/加密/规则/多房间断言
  - `--ui` → WPF 界面冒烟
  - `--server <url>` → 端到端联调
  - `--rooms <url>` → 多房间并行与隔离
  - `--lifecycle <url>` → 结束重置 / 空房回收 / 房间上限（需小局临时配置）
  - `--hostinvite <url>` → 房主交接 / 保留房间 / 邀请码免密进房
  - `--reconnect <url> [秒]` → 断线重连
  - `--protocol <url>` → 协议边界（未知 GameId / 视角隔离 / 等待中不泄露身份 / 夜间顺序门禁）
  - `--newroles <url>` → 新身份端到端（自定义布局 / 开局公布 / 警长公开 / 丘比特牵线 / 情侣殉情，**需小局临时配置**）
  - `--hold` / `--bots` → 凑人数 / 机器人自动对局
  - 默认模式还含：`FakeGameProbe`（工程师的假游戏）与 `IndependentGameProbe`（QA 自写的第三个游戏）——**扩展性回归**

## 本机环境坑（实测）
- `.exe`（apphost）跑不起来，一律用 `dotnet <dll>`。
- 访问 localhost 前先 `unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy`。
- 服务端要在**临时目录**里跑（避免污染仓库 `WerewolfGame/accounts.json`）：拷 `appsettings.json` 到临时目录再 `cd` 过去启动。
- 停服务端只杀监听 5000 的 PID（`netstat -ano | grep :5000`），**不要** `taskkill /IM dotnet.exe`。
- 中文控制台输出可能乱码但可读。

## WPF 坑
- 窗口/UserControl **根元素** Background/Foreground 必须 `DynamicResource`（解析时看不到自身 Resources）。
- 每个窗口都要自己用 `pack://application:,,,/ClientGUI;component/Themes/Panels.xaml` 合并主题字典。
- `PasswordBox.Password` 不可绑定，用 `PasswordChanged` 手动同步。
- 不要在 `ItemsControl` 内用 `DataTrigger` 监听自身 `Count`（无限测量递归）。
- 未 `Show()` 的窗口没有呈现源，Popup 不实例化内部项模板。
- **菜单外观**：`Menu.ItemContainerStyle` **只作用于菜单栏第一层**，不会传播到下拉项与嵌套子菜单
  → 那些项会落回 WPF 默认的**浅色**系统外观（深色主题下背景是白的）。
  解法：在 `Controls.xaml` 末尾注册 `MenuItem` / `Menu` / `Separator` 的**隐式样式**（`BasedOn` 键控样式），
  让任意层级都走主题模板。`--ui` 冒烟会遍历菜单树断言每一层的 `Template` 都是主题模板。
- **设置入口**：`Views/SettingsWindow` 是唯一设置入口（**标签页**：外观 / 主界面 / 上次使用），
  可在任意界面用 **`Ctrl + ,`** 打开（`App` 用 `EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, …)`
  统一接管；用 Preview 才不会被输入框吞掉）。快捷键判定抽成 `App.IsSettingsShortcut(key, modifiers)` 纯函数以便测试。
- **内置控件模板都要自己主题化**：`Menu` / `MenuItem` / `Separator` / `TabControl` / `TabItem` / `ScrollBar` / `ComboBox`
  的 WPF 默认模板都是浅色系统外观，深色主题下会刺眼（菜单项还会因 `ItemContainerStyle` 不传播而只覆盖第一层）。
  新用到的控件记得在 `Controls.xaml` 里补模板，配色走 `DynamicResource`。
- **ComboBox 的点击层**：模板里覆盖全区的透明 `ToggleButton` **必须给空模板**，
  否则它的默认（系统浅色）外观会盖在内容上 → 「下拉框点击时颜色异常 / 文字被遮住」。
- **别只依赖显示模板**：自定义类型被 WPF 直接渲染时会回退到 `ToString()`。
  `GameInfo` 因此重写了 `ToString() => Label`，任何遗漏 ItemTemplate 的场合也不会露出类型名。
- **XAML 里别给会立即触发事件的属性设初值**：`IsChecked="True"` 会在 `InitializeComponent` 期间触发
  `Checked`，此时后面的控件还没创建 → NullReferenceException。改为在构造里（`_suppress` 保护下）赋值。
- **周期刷新的界面不要"完整回填"**：`HostSettingsWindow` 每 3 秒轮询房间设置，
  早期每轮都做完整回填 → 房主改「最大人数/密码」必须抢在刷新前点保存，否则被冲回旧值。
  正确做法：**轮询只刷新只读展示**（`RefreshFromServerState`），可编辑控件只在
  「首次加载」与「保存之后」回填（`ApplySettings`）。`--ui` 有对应回归断言。

## 主题系统（深/浅色 + 跟随系统）
- 结构：`Themes/Colors.Dark.xaml` / `Colors.Light.xaml`（**颜色唯一定义处，键名必须两套一致**）、
  `Converters.xaml`（转换器）、`Controls.xaml`（控件样式，零颜色字面量）、`Panels.xaml`（面板模板）。
- **颜色一律 `{DynamicResource XxxBrush}`，禁止硬编码 `#RRGGBB`，也不要用 `{StaticResource ...Brush}`**
  （StaticResource 只求值一次，换主题不生效；只有 `FlatButton` 这类**样式键**才用 StaticResource）。
- 颜色挂在 **Application 级**（不是各窗口），由 `ThemeManager.Initialize()`（`App.OnStartup` 里，早于任何窗口）
  插入；`SetTheme` 只替换那个资源槽位 → DynamicResource 重新求值 → 已打开的窗口立刻换色。
- 「跟随系统」= 读 `HKCU\...\Themes\Personalize\AppsUseLightTheme` + `DispatcherTimer` 4 秒轮询；
  偏好存 `ui-state.json` 的 `Theme` 字段。**主题入口在设置窗口里**（原「视图 → 主题」子菜单已并入设置窗口）。
- 新增颜色：两套都加同名键。`--ui` 冒烟会**扫描所有 XAML 引用的 `*Brush` 键并校验覆盖**，漏加即断言失败。
- 已知限制：菜单 Popup 投影色仍是黑色字面量；Win32 标题栏不随应用主题。详见 `docs/ui/THEMING.md`。
- **剪贴板**：Windows 剪贴板是全局独占资源，被占用时抛 `CLIPBRD_E_CANT_OPEN (0x800401D0)`；
  统一走 `Services/ClipboardHelper.TrySetText`（**外层只重试 2 次**：WPF 的 `Clipboard.SetDataObject` 自己就会重试约 10×100ms，
  外层再叠加会让窗口卡好几秒）。**不要在窗口打开时自动复制**，改为用户主动点按钮，并在复制前先渲染「正在复制…」。
  **凡是需要用户拿走一段文本的场合，一律用可选中文本的独立窗口，不要用 MessageBox**（弹窗里的文字无法用鼠标选中）。

## 团队协作（software-company SOP）
中大型需求走「产品经理 PRD → 架构师设计+任务分解 → 工程师实现 → QA 独立验证」；
文档落 `docs/<专题>/PRD.md`、`docs/<专题>/ARCHITECTURE.md`；裁定与验收记录追加到架构文档末尾（§11 模式）。
