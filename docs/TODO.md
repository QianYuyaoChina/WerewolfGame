# 待办事项（优化建议）

> 开始任何任务前先读本文件；做完任务后回写本文件。流程见 [§闭环流程](#闭环流程)。

---

## ⛔ 顶部警示：改动不自动同步远端

**本仓库的改动不自动同步远端：禁止 `git commit` / `git push`。**
一切改动留在工作区，由用户显式提出、并单独确认后才执行提交 / 推送。
每一轮任务的交付说明里必须写明「本轮未执行任何提交 / 推送」。

（来源：用户原话「不要每次作出修改后立刻同步至 github」，已两次强调。）

---

## 闭环流程

每次任务照做，五步一环，缺一步不算完成：

| 步骤 | 动作 | 判据 |
|---|---|---|
| ① **读** | 任务开始时先打开本文件，通读 P0 / P1 两组 | 在动手写代码之前完成 |
| ② **挑** | 从中挑出「本轮顺手可做」的项，与本次需求合并执行 | 至少挑 1 项；一条都不做需在交付说明里写明原因 |
| ③ **记** | 开工前把挑中的条目标记为进行中（`- [ ]` → `- [~]`） | 避免多轮任务重复挑同一条 |
| ④ **销** | 做完后把该条移入「已完成」，加完成日期与一句话结论 | 完成判定栏打勾 |
| ⑤ **回写** | 把任务过程中**新发现的**优化建议按模板追加进对应优先级分组 | 至少 1 条；确实没有则写「本轮无新增」 |

补充约定：

- **完成判定必须可观测**，禁止写「优化一下 XX」这种无法判定的条目。
- 条目被做完或确认不再需要才能删除；**不得静默删除**未在交付说明里解释的条目。
- 每次交付说明里必须有一节「**本轮待办闭环：挑了 X / 做了 Y / 新增 Z**」。
- 两个人（或两轮任务）同时想做同一条：开工前标记进行中，发现已被标记则换一条。

---

## 条目模板

```markdown
- [ ] **做什么**（一句话）
    - 为什么：<价值 / 不做的代价>
    - 落点：<建议的模块或文件（可写"建议"，架构师可改）>
    - 优先级：P0 / P1 / P2
    - 完成判定：<怎么算做完，可观测>
    - 来源：第 N 条需求 / 现状扫描
```

状态标记：`- [ ]` 待办 → `- [~]` 进行中 → 移入「已完成」（`- [x] 日期 结论`）。

---

## 待办

### P0 尽快做

> ✅ 本组已在 **B4** 全部闭环：**T6 / T8** 见下方「已完成」。当前 P0 无待办。

### P1 该做

- [~] **T3 把界面画刷集中缓存里写死的一组 RGB 颜色迁到主题色键**（2026-10-11 部分完成）
    - 为什么：它们不随深浅主题切换，是「深色底上看不见的字」的隐患来源
    - 落点：`ClientGUI/ViewModels/UiBrushes.cs`
    - 优先级：P1（架构 U7 判定：半透明行底色依赖主题极性，强行全量改造会触碰玩家行渲染断言，故降级）
    - 完成判定：该文件中不再出现颜色字面量
    - 来源：现状扫描
    - **进度（2026-10-11，B1）**：与主题同义的 4 个（Alive / Dead / Warning / Muted）已改为**实时查主题键**（OkBrush / ErrorBrush / WarnBrush / MutedBrush），深浅主题都可读；剩余 5 个半透明行底色（RowBackground* / RowBorder*）按架构 U7 保留并加了注释，待 P1 与玩家行样式一起改。

- [~] **T9 统一「关闭意图」入口（关主窗口 / 关房间列表 / 退出程序）**（2026-10-11 B2 已做完，待销号）
    - 为什么：第 8 条后三者行为不同且分散在三处，改一处容易忘另外两处
    - 落点：GUI 应用生命周期（`ClientGUI/Services/CloseIntent.cs`）
    - 优先级：P1
    - 完成判定：三种关闭来源走同一个意图分派，行为表可一眼看全
    - 来源：第 8 条需求
    - **进度（2026-10-11，B2）**：`Services/CloseIntent.cs` 已建（枚举四种来源 + `RequiresFullShutdown` / `Describe` 两个纯函数）；`MainWindow.OnClosing` 改为「`e.Cancel = true` → 走 `DisconnectCommand`」，并删掉了 `AppShutdown.ConfirmClose` 调用；四种来源的行为由 `AppShellGuard` 的 6 条断言守住。

- [ ] **T14 下拉框（FlatComboBox）与列表容器还没跟上热区与圆角阶梯**
    - 为什么：B1 把输入框升到 44 / 圆角 8 之后，下拉框仍是 MinHeight 34 / 圆角 6，同一表单里两种高度不齐
    - 落点：`ClientGUI/Themes/Controls.xaml`（FlatComboBox / PlayerListBox）
    - 优先级：P1
    - 完成判定：下拉框高 ≥44、圆角 8；列表项间距落在 4/8/12/16/24 阶梯内
    - 来源：B1 风格迁移时新发现

- [ ] **T15 玩家行 / 日志行的字号仍是 13、13.5、12.5 这类「半档」字号**
    - 为什么：Style 阶梯只允许 17/15/14/12/11，半档字号会让同一页面出现三种正文大小，移植到移动端时无法映射到设计变量
    - 落点：`ClientGUI/Themes/Panels.xaml`（PlayerRowTemplate / LogEntryTemplate）
    - 优先级：P1
    - 完成判定：两处模板的字号全部落在五档阶梯内（自动化：扫描 XAML 的 FontSize 取值集合）
    - 来源：B1 风格迁移时新发现

- [ ] **T16 CheckBox / RadioButton 仍是 WPF 默认外观（浅色系统样式）**
    - 为什么：设置窗口与房主设置里用得很多，深色主题下是「白底黑字」的系统控件，与页面割裂；也是目前唯一没被主题模板覆盖的控件族
    - 落点：`ClientGUI/Themes/Controls.xaml`
    - 优先级：P1
    - 完成判定：两者都有主题模板，深色 / 浅色下均可读，勾选标记用文本符号而非位图
    - 来源：B1 风格迁移时新发现

- [~] **T12 客户端本地偏好存储不要塞进服务端权威数据**（2026-10-11 B3 已做完，待销号）
    - 为什么：称号 / 管理员标记是服务端权威，本地缓存只用于展示，混进去会出现「改了不生效」
    - 落点：`ClientGUI/Services/UiStateStore.cs`
    - 优先级：P1
    - 完成判定：本地状态里不含称号 / 角色字段
    - 来源：第 4 / 5 条需求
    - **进度（2026-10-11，B3）**：`UiState` 复核确认不含 Title / IsAdmin / Role 字段（称号只存在于 `GameClient` 的内存只读属性，随服务端回执写入）；`AppShellGuard.CheckLocalStatePurity` 用反射守住这一点，将来谁往里加都会被断言拦下。

### P2 有空做

- [ ] **T24 架构文档 U5 的称号长度口径与实际实现不一致，需回写**
    - 为什么：U5 写的是「UTF-16 码元数 ≤ 16」，但汉字在 UTF-16 里只占 1 个码元 —— 按它实现会允许 16 个汉字，与用户文案「最多 8 个汉字或 16 个字符」不符；现按**加权长度**（汉字 / 全角算 2）实现
    - 落点：`docs/v25/ARCHITECTURE.md` §11 U5 + `Werewolf.Client.Share/AccountProtocol.cs`（`TitleRules`）
    - 优先级：P2（实现已正确，只差文档口径同步）
    - 完成判定：架构 U5 的表述与 `TitleRules.WeightedLength` 一致
    - 来源：B3 施工时发现

- [ ] **T25 AppShell.xaml 的键数被 QA 断言锁死为 29 个，新增骨架样式要另找落点**
    - 为什么：`AppShellQaGuard.CheckAppShell` 断言 `keys.Count == 29`，往 AppShell 里加第 30 个键会直接变红；称号胶囊的样式因此定义在控件自己的 `TitleChip.xaml` 里
    - 落点：`ClientGUI/Themes/AppShell.xaml` / `ClientGUI.Tests/AppShellQaGuard.cs`
    - 优先级：P2
    - 完成判定：要么 AppShell 加键时同步更新 QA 的 `RequiredShellKeys`，要么在 AppShell 顶部注释里写明这条约束（二选一，写进文档即可）
    - 来源：B3 施工时发现

- [ ] **T26 聊天消息发送者名后显示称号**
    - 为什么：PRD 4.10 列的是 P1，本轮只做了「房间列表房主列」与「房间内玩家行」两处
    - 落点：`ClientGUI/Themes/Panels.xaml`（LogEntryTemplate）
    - 优先级：P1
    - 完成判定：聊天行在发送者名后渲染金色称号胶囊
    - 来源：第 4 条需求（4.10）

- [ ] **T27 控制台能查看 / 设置自己的称号**
    - 为什么：PRD 4.11 列的是 P2，协议 `Ops.Session` / `Ops.SetTitle` 已具备，只差控制台入口
    - 落点：`Werewolf.Client`
    - 优先级：P2
    - 完成判定：控制台有一条命令能显示并修改自己的称号
    - 来源：第 4 条需求（4.11）

- [ ] **T28 称号敏感词 / 特殊字符过滤**
    - 为什么：PRD 4.12 列的是 P1，当前只校验了长度与控制字符
    - 落点：`Werewolf.Client.Share/AccountProtocol.cs`（`TitleRules`）
    - 优先级：P1
    - 完成判定：命中敏感词的称号被服务端拒绝并给出文案
    - 来源：第 4 条需求（4.12）

- [ ] **T29 房间列表导航栏左右槽位不等宽，标题相对整条导航栏偏移 42px**
    - 为什么：QA 记录 Q-B1-01（P3）已量到 42.0px；小程序导航栏的标题是死居中的，这是该风格的显著特征
    - 落点：`ClientGUI/Views/RoomListWindow.xaml`（B3 已把服务器胶囊放左槽位，实测让偏差不扩大）
    - 优先级：P2
    - 完成判定：三个窗口的标题偏移都 ≤ 2px（现有实测断言阈值是「≤ 导航栏宽 5%」，收紧到 2px 才算做完）
    - 来源：QA Q-B1-01 / B3 施工时发现

- [ ] **T17 列表「最后一行不画分隔线」尚未实现**
    - 为什么：PRD §4.2.2 要求最后一行不画；当前行模板写在 DataTemplate 里，无法知道自己是不是最后一行
    - 落点：房间列表 / 玩家列表的行模板
    - 优先级：P2
    - 完成判定：列表最后一行末尾没有分隔线（可用 AlternationIndex + 转换器，或改用容器模板画上分隔线）
    - 来源：B1 风格迁移时新发现

- [ ] **T11 关于 / 更新日志窗口排进风格迁移队列**
    - 为什么：第 2 条允许暂缓，但不能忘了它们
    - 落点：`ClientGUI/Views/AboutWindow.xaml` / `ChangelogWindow.xaml`
    - 优先级：P2
    - 完成判定：已在迁移清单中登记并给出预计批次（见 PRD §4.4.2）
    - 来源：第 2 条需求

- [ ] **T30 一键开服提示里的默认密码在管理员改过之后会「过时」**
    - 为什么：`AdminDefaults.OneClickLoginHint` 把 `123456` 明文写死；管理员按建议改了密码后，登录页那行提示仍写着默认密码，会误导（「我明明改了，怎么还提示用 123456」）
    - 落点：`Werewolf.Client.Share/AdminDefaults.cs`（`OneClickLoginHint`）+ 登录窗口内置页
    - 优先级：P2
    - 完成判定：改过密码后不再显示默认密码明文（或文案改为「首次默认密码 123456」并说明改过就用新密码）
    - 来源：B4 收尾施工时发现

- [ ] **T31 「修改密码 / 服务器设置」入口依赖打开设置窗口时能拿到会话**
    - 为什么：`SettingsWindow.ResolveSession` 靠 owner 窗口**类型**（LoginWindow / RoomListWindow / MainWindow）取会话；将来若有别的窗口承载会话，忘同步这里就会「设置里看不到改密码入口」
    - 落点：`ClientGUI/Views/SettingsWindow.xaml.cs`
    - 优先级：P2
    - 完成判定：会话由一处统一提供（如复用 `AppShutdown.AttachSession` 的那个只读登记），而不是靠窗口类型分派
    - 来源：B5 收尾施工时发现

- [ ] **T32 可编辑资源保存时的冲突检测（Digest）只是「已下发、未比对」**
    - 为什么：`DataResourceSavePayload.Digest` 注释写着「留空表示不做冲突检查」，但 `GameHub.HandleDataResourceSave` 目前**没有**用 `req.Digest` 和服务器当前内容比对；两个管理员同时编辑会「后写覆盖先写」且无提示
    - 落点：`WerewolfGame/GameHub.cs`（`HandleDataResourceSave`）+ `EditableDataRegistry`
    - 优先级：P2
    - 完成判定：回带的 Digest 与服务器当前不一致时拒绝保存并给出「服务器上的内容已被改过，请刷新后重试」
    - 来源：B4 收尾施工时发现

- [ ] **T33 设置窗口的标签页契约（4 页固定）被 UiSmokeCheck 硬断言**
    - 为什么：收尾追加轮把「账号设置」做成第 4 个标签页后，`UiSmokeCheck.CheckSettingsWindow` 断言页头恰为 `{外观, 主界面, 上次使用, 账号设置}`；将来增删或重排标签页会直接变红（与 T25 的 AppShell 键数同类）
    - 落点：`ClientGUI/Views/SettingsWindow.xaml` / `ClientGUI.Tests/UiSmokeCheck.cs`
    - 优先级：P2
    - 完成判定：改动标签页时同步更新该断言；或在 `SettingsWindow.xaml` 顶部注释里写明这条契约（本轮已写）
    - 来源：收尾追加轮施工时发现

- [ ] **T34 一键开服自动进入依赖「登录窗口以模态显示」**
    - 为什么：收尾追加轮让 `OneClickAsync` 到达就绪态后直接 `EnterRoomList()` → `LoginSucceeded` → `LoginWindow.OnLoginSucceeded` 设 `DialogResult = true` 并 `Close()`；`DialogResult` 只能在 `ShowDialog()` 打开的窗口上赋值，若将来把登录窗口改为非模态 `Show()`，这一步会抛异常
    - 落点：`ClientGUI/Views/LoginWindow.xaml.cs`（`OnLoginSucceeded`）/ `ClientGUI/ViewModels/EmbeddedServerViewModel.cs`
    - 优先级：P2
    - 完成判定：登录窗口改为非模态时同步调整「进入房间列表」的落地方式（如改为直接打开房间列表窗口，而非设置 DialogResult）
    - 来源：收尾追加轮施工时发现

- [ ] **T35 「单窗口页面化」重构不得丢失「关闭保留房间窗口 = 保留」**
    - 为什么：当前「关闭保留房间窗口 = 保留」由 `KeepRoomDecision.Resolve`（null → 保留）+ `KeepRoomWindow.OnClosing` 兜底共同保证；后续若把多个 Window 合并成单窗口页面（导航切换），这套 X / Alt+F4 兜底极易被漏掉，重新出现「关窗误删房间」
    - 落点：`ClientGUI/Services/KeepRoomDecision.cs` / `ClientGUI/Views/KeepRoomWindow.xaml.cs` /（将来）单窗口页面化相关视图
    - 优先级：P2
    - 完成判定：页面化后「直接关闭 / 取消保留房间」仍等于「保留房间」，且 `AppShellGuard.CheckKeepRoomDecision` 源码级守护随重构更新并保持绿
    - 来源：保留房间 BugFix 施工时发现

- [x] **T36 `PageFactory` 已认 7 页，其余 3 个 `PageId`（保留房间 / 关于 / 更新日志）仍抛 `NotSupportedException`**
    - 为什么：`PageId` 十个值一次性列全（枚举稳定，后续批次的 diff 只在 switch），`PageFactory.Create` 对其余 3 个值抛异常。**现在没人会走到那里**（这三项仍以旧窗口弹出），但 S3 若漏补一个分支，点那个入口就是一次崩溃
    - 落点：`ClientGUI/Services/NavigationService.cs`（`PageFactory`）
    - 优先级：P1（S3 开工即闭环）
    - 进度：S2 补了 `CreateRoom` / `HostSettings` / `Settings` / `InviteCode` 四个分支（+ 邀请码页从 `PageRequest.Argument` 取码）
    - **已闭环（2026-10-18，S3）**：十个 `PageId` 全部有页面（`KeepRoom` / `About` / `Changelog` 三个分支补齐），`PageCatalog.All.Count == 10`。**原定完成判定里的 `SkeletonGuarded.Count == 8` 按实际页面数修正为 10**：架构 §8.3 P11 写「8」时按 S1 的三页 + 五个批次的估计，而 S1+S2+S3 实际一共迁了 10 页，`SkeletonGuarded` 的定义是「已迁移页面全部受骨架守护」，所以是 10 而不是 8。新增两条自检把这件事钉死：`All.Count == 10`、`All.Count == Enum.GetValues<PageId>().Length`（不留未迁的枚举值）。兜底分支 `_ => throw new NotSupportedException` 保留 —— 它从此只为「将来新增枚举值却忘了加页面」而存在
    - 来源：S1 施工时按架构 §9.1 只做三页

- [x] **T37 旧窗口与页面**并存**期间，两套断言表各管一半，S4 删窗时必须同时切换**
    - 为什么：`AppShellGuard.MigratedWindows`（8 项）、`AppShellQaGuard.MigratedWindowFiles`（8 项）、`V25BatchProbe` 的 8 项导航居中表**仍在断言旧 Window**；`PageCatalog` 只断言新页面。两套都绿说明「页面不比窗口差」，但 S4 删掉窗口类时这三张表会一起失效 → 断言数会掉
    - 落点：`ClientGUI.Tests/AppShellGuard.cs` / `AppShellQaGuard.cs` / `V25BatchProbe.cs` / `PageCatalog.cs`
    - 优先级：P1（S4 的前置条件）
    - 完成判定：S4 把三张表改为 `foreach (var probe in PageCatalog.SkeletonGuarded)`，并且 `--ui` 条数不减（当前 3 7 0）
    - **已闭环（2026-10-19，S4）**：三张表全部切到页面，且**换成 `PageCatalog.SkeletonGuarded` 之后条数反而变多**（因为 `SkeletonGuarded == All` 是 10 页，比原来 8 个窗口多 2 页）：
      - `AppShellGuard.MigratedWindows`（8 项窗口元组）→ `MigratedPages`（`PageCatalog.SkeletonGuarded` 投影，10 项）；`CheckWindowSkeletons` / `CheckNavBar` / `CheckBlockButton` / `Layout` 四个私有方法改成页面形态（导航栏判据 `MinHeight >= 56 || ActualHeight >= 56` 覆盖页面「还没量过」的情形）；`CheckTapTargets` 从 `new RoomListWindow(new GameClient())` 换成 `new RoomListPage(new AppSession())`（不再需要 `try/finally window.Close()`）
      - `V25BatchProbe` 的 8 项导航居中表 → 直接吃 `PageCatalog.All`，**8 条变 10 条**（多测关于 / 更新日志两页），判据仍是 ≤1px 像素级
      - `AppShellQaGuard.MigratedWindowFiles` → `RetiredWindowFiles`（8 个必须**不存在**）+ `MigratedPageFiles`（10 个必须存在且带骨架）—— 由 QA 独立完成，**没有复用 `PageCatalog.All`**，两份表互为对照（我漏一页它会红）
      - `PageCatalog` 侧新增三段全仓归零守卫（`new XxxWindow` / 20 个旧文件名 / `Views` 下 `*Window.xaml`）
    - 完成判定的「`--ui` 条数不减」按**页数**结算：窗口实例断言 10 → 页面实例断言 10 + 外壳 1，骨架/居中类断言 8 组 → 10 组。基线由 QA 在标准目录复跑后签字
    - 来源：S1 施工时选定「先加后删」的迁移策略

- [x] **T38 `App.ShowLogin()` / `App.RunRoomLoop()` 已不再被调用，但按架构 §10.3 有意保留**
    - 为什么：S1 的入口改成了 `new ShellWindow().Show()`；旧的多窗口流程留在 `App.xaml.cs` 里作为**唯一允许的开关式回退点**（换回一行即回退）。留着不是死代码，但读代码的人会以为它还在生效
    - 落点：`ClientGUI/App.xaml.cs`
    - 优先级：P2
    - 完成判定：S4 收口时删除这两个方法，并把「回退点」的说法从 `App.xaml.cs` 注释里去掉
    - **已闭环（2026-10-19，S4）**：`ShowLogin()` 与 `RunRoomLoop(GameClient)` 两个方法整体删除；`App.xaml.cs` 里的 `using ClientGUI.Views;` / `using Werewolf.Client.Share;` 随之删掉（否则是「只为已删代码存在的 using」）。**为什么不只是删方法**：这两个方法的存在本身就是「将来还可能退回多窗口」的承诺，而 S4 把 10 个窗口类删干净之后，那个回退分支的**载体已经不存在**了 —— 留着就是一段必然抛 `NotSupportedException` 的死代码。所以「回退点」这个说法从 `App.xaml.cs` 注释里一并移除
    - 顺带闭环的部分：`App.OnAnyWindowPreviewKeyDown` 里 `SettingsWindow.Open(sender as Window)` 这条**按窗口类型分派**的兜底也删了，统一成 `shell.Session.Navigation.PushOverlay(new PageRequest(PageId.Settings))`（一份实现，不再有「按宿主类型选实现」的第二条路）
    - 来源：S1 施工时按架构 §10.3 保留

- [x] **T39 `SettingsWindow.ResolveSession(Window)` 仍在按 owner 窗口**类型**分派**
    - 为什么：S1 已给它加了 `ShellWindow => shell.Client` 分支（否则单窗口下「账号设置」页会取不到会话、改密码入口消失）；但根因是 T31 —— 会话应当由一处统一提供
    - 落点：`ClientGUI/Views/SettingsWindow.xaml.cs`
    - 优先级：P2
    - 进度：S2 新增的 `SettingsPage` **不再**走这条路（直接拿 `AppSession.Client`，页面里没有一处 `switch (owner)`）；只剩旧 `SettingsWindow` 自己还在用 —— 随 S4 删窗口一起消失
    - 完成判定：与 T31 合并闭环：`ResolveSession` 改为从 `AppSession`（或一处只读登记）取，不再 `switch` 窗口类型
    - **已闭环（2026-10-19，S4）**：「按宿主窗口类型反推会话」这条路**在类型层面已经不可能存在** —— `SettingsWindow` 已删除，`SettingsPage(AppSession session)` 在构造时就拿到会话，`private GameClient Client => _session.Client;` 是唯一取值路径，全仓 0 处 `ResolveSession`、0 处 `switch (owner)`。
    - ⚠ **与 T31 的关系**：T39 只是「取会话的方式」这一半，已随载体消失而闭环；T31 的另一半（「修改密码 / 服务器设置」入口的权限口径、服务端二次校验）**没有随本次删除而变化**，T31 保持 `[ ]`，由 S5 裁定
    - 来源：S1 施工时为了不丢「账号设置」入口临时加的分支

- [ ] **T40 「退出房间」其实没有「取消退房」这条出口**
    - 为什么：`RoomBackDecision.ResolveAsync` 的 `false` 分支目前**只会**在 `leaveroom` 请求失败时触发；房主的「是否保留房间」询问只有「保留 / 关闭」两个出路，没有「那我不退了」。因此「点 × 被询问后改变主意」目前做不到 —— 关窗即等于退房（与旧 `MainWindow` 行为一致，不是回归，但值得记一笔）
    - 落点：`ClientGUI/Services/RoomBackDecision.cs` /（将来）`KeepRoomPage`
    - 优先级：P2
    - 完成判定：若给「保留房间」页加「取消（不退房）」按钮，判定必须回到 `RoomBackDecision` 这一处，并补一条 `Resolve` 断言
    - 来源：S1 写返回拦截时发现

- [x] **T41 `PushForResultAsync` 目前复用 `Push`，会把下面那页 `OnLeave` 掉 —— S3 做「保留房间」页前必须先改**
    - 为什么：`NavigationService.PushForResultAsync` 原来内部调 `Push`，而 `Push` 会给旧栈顶调 `OnLeave`。若把 `KeepRoomPage` 压在 `RoomPage` 之上，就会触发 `RoomPage.OnLeave` → `GameViewModel.Detach()` + `ClearLeaveRoomHandler()`，而「是否保留房间」这个询问正是 `GameViewModel.ConfirmKeepRoom` 在回答 —— 页面还没问完，视图模型先被退订了
    - 落点：`ClientGUI/Services/NavigationService.cs`
    - 优先级：P1（S3 的前置条件，跟 T36 同批处理）
    - **已闭环（2026-10-18，S2）**：`NavigationService` 分出两种入栈语义 —— `Push`（换页：旧栈顶 `OnLeave`）/ `PushOverlay`（浮层：下层**不退场**，只把 `IsHitTestVisible` 置 false；出栈时**也不给下层补 `OnEnter`**，避免订阅翻倍）。`PushForResultAsync` 改走 `PushOverlay`；另加 `DismissOverlay()`（× 收起一层）与 `Complete()` 无等待者时返回 false。断言：`PageCatalog` 的 **P12**（18 条：进出次数 / 栈深 +1 / 不重入 / DismissOverlay / 无等待者时不动栈）+ `--ui` 的 **S2 浮层闭环**（真页面 `IsHitTestVisible`、`DataContext` 仍在、同一实例、进出 5 轮订阅者 `1 → 1`）
    - 施工顺序（QA 独立验收后补充，见 `docs/v25/SHELL-ARCHITECTURE.md` §15.5 第 1 条）：**先改实现，再补断言** —— 只补断言不改实现，等于把缺陷锁进测试里
    - 来源：S1 实现导航栈时发现

- [x] **T42 导航标题居中的判据有两套口径（`max(2px, 5%)` vs ≤1px），S5 统一收紧**
    - 为什么：`PageCatalog` / `UiSmokeCheck` / `AppShellQaGuard` 现在判「偏移 ≤ `max(2px, 导航栏宽 5%)`」，而 I5 的目标是 **≤1px**；实测 0.33 / 0.00 / 0.00px 三页都过，但这条宽判据会放过 2px 级回归
    - 落点：`ClientGUI.Tests/PageCatalog.cs`（页面版）/ `AppShellQaGuard.cs`（窗口版）/ `UiSmokeCheck.cs`
    - 优先级：P2（S5 打磨批次；**不在 S2/S3 动**，避免把当时的基线搅乱）
    - 完成判定：两套判据统一为 ≤1px，且四档复跑不掉条数；收紧那一刻与 QA 同步、两边一起改
    - **已闭环（2026-10-19，S5）**：判据收敛成**全工程唯一一个函数** `PageCatalog.VerifyTitleCentered(root, bar, title, label)`，阈值 `offset <= 1.0`。旧的 `≤ max(2px, 导航栏宽×5%)` **公式删除** —— 它对任何导航栏都恒 ≥2px，比 1px **松**；两套并存的真实后果是"同一个页面，A 文件说合格、B 文件说不够"，谁都能挑对自己有利的那把尺子。
      - `PageCatalog` 的 `CheckCentered`（private，旧公式）→ `VerifyTitleCentered`（public，≤1px）；
      - `V25BatchProbe` 里原本**自己又算一遍**坐标数学（虽然已是 ≤1px），改成调同一个函数 —— 消灭第二份实现才是 T42 要收的口子；
      - `UiSmokeCheck` 核过：它对居中只有注释、没有像素断言（真正的断言在 `AppShellGuard.CheckNavBar`，判 `HorizontalAlignment.Center`，等价零偏移），无可收紧对象；
      - 新增**防漂守卫** `CheckSingleCenteringCriterion()`（挂默认组）：扫我负责的三个文件，禁止旧公式复活。
    - 实测：十页偏移 `0.00 / 0.00 / 0.00 / 0.00 / 0.00 / 0.33 / 0.00 / 0.00 / 0.00 / 0.33`，最差 **0.33px**；真客户区 + 760×520 临界组两组同样 ≤0.33px。
    - ⚠ **收尾插曲（值得记）**：防漂守卫第一版把 `<Max>(2.0,` 写成**字面量**，于是它**扫到了自己** —— 默认组直接红（期望 0、实际 2，命中 `PageCatalog.cs` 两处）。改成用 `+` 拼令牌才修好。这类"守卫检测到自己"与"静默返回"是同一类只能靠**真跑**才暴露的问题。
    - 遗留（不属本条目）：`AppShellQaGuard.cs` 里 QA 那份仍用旧公式，**阈值同为 ≤1px 但独立实现**，由 QA 侧并入；我的防漂守卫只扫我这三个文件，不会因她那份误红。
    - 来源：QA 独立验收 S1 时发现（2026-10-17）

- [x] **T43 「已进房时点 × → 保留房间询问」缺一条真端到端断言（P3 覆盖缺口）**
    - 为什么：模态 `KeepRoomWindow` 需要人点，自动化点不了；S1 只能用三段闭合（`Resolve` 落格 + `ShellWindow` 接线 + `RoomPage.OnBackRequestedAsync` 调同一函数）间接覆盖
    - 落点：`ClientGUI.Tests/PageCatalog.cs`（S3 之后）
    - 优先级：P2
    - **已闭环（2026-10-18，S3）**：`PageCatalog.RunKeepRoomEndToEndChecks()`（挂在 `--ui` 最后一批，46 条）。**真**走完两条路径 —— 进程内起服 → 连接 → 登录 → 建房（房主独自在房）→ 页面栈「登录 / 列表 / 房间」→ 触发退房 → `KeepRoomPage` 浮层入栈 →：
      ① 在浮层上按「返回」/ Esc（= 没显式选）→ `null` ⇒ 保留 → 退房成功 → 出栈回 `RoomListPage`、栈深 2、`EmbeddedServerService.Current.IsRunning == true`、**房间仍在服务器列表里**；
      ② 重新进房 → 浮层 → 点「关闭房间」→ `false` ⇒ 关闭 → 退房成功 → 出栈回 `RoomListPage`、栈深 2、服务器仍在跑、**房间已从服务器列表移除**。
      两条都是「行为级硬证据」（`ListRoomsAsync` 的成员变化），不是"看它有没有抛异常"
    - 实现注记（踩过的坑，留给以后）：测试宿主没有 `Application.Run()`，而方法体是在多次 `Dispatcher.PushFrame` **之间**执行的，此时 `SynchronizationContext.Current` 为 `null` —— 于是所有 `await` 续体落到线程池，`Players.Clear()` 这类 `ObservableCollection` 改动会在非 UI 线程抛 `NotSupportedException`。真实宿主的整条消息循环都在 `PushFrame` 里（`PushFrame` 会装上 `DispatcherSynchronizationContext`），不会发生。修法是**在测试里显式补上同一个上下文**（`SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(...))`，跑完还原），而不是去改产品代码 —— 产品侧一行未动
    - 来源：QA 独立验收 S1 时提出（2026-10-17）

- [x] **T44 页面内标题不再随内容变化（旧窗口是 `Title = "房主设置 · 🐺 狼人杀"`）**
    - 为什么：`ShellWindow.OnNavigated` 只在**换页那一刻**读一次 `IPage.Title`，此后页面自己再改也没人通知外壳。`HostSettingsPage` 因此把游戏名从窗口标题挪到了导航栏右槽（`GameNameText`）——信息没丢，但窗口标题退化成「狼人杀 · 房主设置」
    - 落点：`ClientGUI/Services/NavigationService.cs`（`IPage` 可加一个 `TitleChanged` 事件或让 `Title` 变可变 + 通知）/ `ShellWindow.xaml.cs`
    - 优先级：P2（S5 打磨）
    - 完成判定：加「页面标题变更」通知后，`HostSettingsPage` 的窗口标题恢复带游戏名，并补一条断言
    - **已闭环（2026-10-19，S4）**：没有让 `IPage.Title` 变成可变属性（那会逼所有页面实现一套 `INotifyPropertyChanged`），而是**旁挂一个可选接口**：
      ```csharp
      public interface IPageTitleSource { event Action? TitleChanged; }
      ```
      只有「标题会在存活期变化」的页面才实现它（当前只有 `HostSettingsPage`）。`ShellWindow` 侧加 `_titleSource` 字段，`OnNavigated` 里 `AttachTitleSource(page)` —— **换页先退订上一页、再订阅新页**（不退订的话页面反复进出会累积订阅，这正是 T41 那类事故的形态）。`HostSettingsPage.Title` 现在是
      ```csharp
      public string Title => _gameLabel.Length == 0 ? "房主设置" : $"房主设置 · {_gameLabel}";
      ```
      `ApplyGameIdentity` 里只要 `_gameLabel` 真的变了就 `TitleChanged?.Invoke()`。外壳 `ComposeTitle(page)` 统一拼成 `狼人杀 · <页面标题>`。
    - 断言：`UiSmokeCheck.CheckShellTitleFollowsPage`（3 条）—— 用**真外壳窗口 + 真页面栈**走一遍：换到房主设置页 → 窗口标题含「房主设置」→ 把游戏改成狼人杀 → 窗口标题变成含「狼人杀」。这条替换的正是旧窗口那条 `window.Title.Contains("谁是卧底")`，**用户可见结果没丢，只是中间多了一层**
    - 来源：S2 迁移房主设置页时发现

- [x] **T45 「三栏显隐」的通知点有两套并存（旧 `SettingsWindow.ViewOptionsChanged` + 新 `Services.ViewOptions`）**
    - 为什么：S2 把页面侧的来源与订阅方都改成 `ViewOptions`（独立静态类，与窗口生死解耦），但旧 `SettingsWindow` 自己的静态事件还在（S2 规定旧窗口一行不改）。两套都在时不会出错（房间页只订阅新的那套），但读代码的人会困惑
    - 落点：`ClientGUI/Views/SettingsWindow.xaml.cs`
    - 优先级：P2（S4 删窗口时一并清理）
    - 完成判定：删掉 `SettingsWindow.ViewOptionsChanged`，全仓只剩 `Services.ViewOptions` 一处广播
    - **已闭环（2026-10-19，S4）**：`SettingsWindow` 整个类已删除，它那条静态事件随之消失；`ClientGUI/Services/ViewOptions.cs` 里加了「S4（T45）之后全仓只剩这一处广播点」的注记。`--ui` 侧的 `设置：取消「显示房间消息」会写回界面状态` 等三条断言未改（它们驱动的是 `SettingsPage` 的勾选框 → `ViewOptions` → 状态，链路完整）
    - 来源：S2 迁移设置页时发现

- [ ] **T46 浮层的「模态感」目前只靠 `IsHitTestVisible`，没有真叠层渲染**
    - 为什么：外壳的 `PageHost` 是 `ContentControl`，换页即换 `Content` —— 浮层入栈时下层页**已经不在视觉树里**，所以 `IsHitTestVisible = false` 只是语义保险（它同时是「下层没退场」的可断言信号，N8 的实现观测点）。若将来要做「半透明浮层盖在房间页上」的视觉效果，需要改成两层 `ContentPresenter` 或 `Panel`
    - 落点：`ClientGUI/ShellWindow.xaml` / `Services/NavigationService.cs`
    - 优先级：P3（纯观感，不影响任何行为）
    - 完成判定：若做视觉叠层，`PageCatalog` 的 P12 与 S2 浮层闭环断言必须全部继续绿
    - 来源：S2 实现浮层式入栈时确认

- [x] **T47（= QA S3 验收 Q-S3-01）`GameViewModel` 上仍有三条「无参同步询问」入口**
    - 为什么：S3 把询问搬到 `KeepRoomPage` 浮层之后，视图模型上还留着三条**不接收显式选择**的出口 —— 私有 `LeaveRoomAsync()`（无参）、`LeaveRoomForPageAsync()`（无参）、以及 `DisconnectCommand`。它们内部都会退回到同步询问（`ConfirmKeepRoom?.Invoke() ?? false`），而页面化之后**没有人再应答那个询问** → 调用方会静默拿到 `false`，表现是「点了退出房间，房间被关掉了」。留着就是留给下一个人踩的坑
    - 落点：`ClientGUI/ViewModels/GameViewModel.cs`（三条入口 + `ConfirmKeepRoom` / `ResolveKeepRoom()`）
    - 优先级：P2（S3 验收由 QA 提出，并入 S4 直接销号，不另开工单）
    - **已闭环（2026-10-19，S4）**：`DisconnectCommand` / `ResolveKeepRoom()` / `ConfirmKeepRoom`（`Func<bool>` 属性）/ 私有 `LeaveRoomAsync()`（无参）/ `LeaveRoomForPageAsync()`（无参）/ `LeaveRoomForPageCoreAsync(bool?)` 全部删除。退房出口收敛成**三个只接受显式 `keepRoom` 的**方法：
      ```csharp
      public Task LeaveRoomForShutdownAsync() => LeaveRoomCoreAsync(keepRoom: false);  // 收尾 = 不保留，且从不询问
      public Task LeaveRoomAsync(bool keepRoom) => LeaveRoomCoreAsync(keepRoom);
      public Task<bool> LeaveRoomForPageAsync(bool keepRoom) => LeaveRoomForPageCoreAsync(keepRoom);
      ```
      `NeedsKeepRoomDecision` **保留** —— 它只回答「要不要问」，不做「问出什么」的判定，不是询问入口
    - 完成判定（可执行）：
      ① `ActionComputationTests.KeepRoomPrompt` 里用反射列出 `GameViewModel` 所有 `LeaveRoom*` 方法，**必须恰好**是上表三条（多一条无参版本复活就红）；
      ② `AppShellGuard.CheckKeepRoomDecision` 全仓扫 `ResolveKeepRoom(`（带左括号）与 `ConfirmKeepRoom`，剥行注释后必须 **0 处**；
      ③ `PageCatalog.CheckS3PushWiring` 全仓扫 `DisconnectCommand`，XAML 侧剥 `<!-- -->`、代码侧剥 `//`，必须 **0 处**；
      ④ `RoomPage.xaml` 的「退出房间」只剩 `Click="Menu_LeaveRoom_OnClick"` 两处（菜单 + 导航栏），没有任何 `Command=` 绑定
    - 来源：QA 独立验收 S3 时提出（2026-10-18，P3 → S4 并入）

- [x] **T48（= QA S3 验收 Q-S3-02）`ResolveKeepRoom()` 的 `?? false` 与 I6 反向**
    - 为什么：I6 的底线是「**没选 ⇒ 保留**」（用户随手关掉询问不该把房间关掉）。旧实现是 `ConfirmKeepRoom?.Invoke() ?? false` —— 没人应答就落到 `false`，恰好是「不保留」，与 I6 **反向**。S3 时这条路径已经没人在走（页面走浮层），但它是活的代码，一旦哪天有人接回去，事故会以「按 Esc 就关房间」的形态复现
    - 落点：`ClientGUI/ViewModels/GameViewModel.cs` / `ClientGUI/Services/KeepRoomDecision.cs`
    - 优先级：P2（并入 S4 直接销号）
    - **已闭环（2026-10-19，S4）**：`?? false` 那行随 `ResolveKeepRoom()` 一起删除；三态翻译现在**只有** `KeepRoomDecision.Resolve(bool?)` 一处，语义是 `null ⇒ true`：
      ```csharp
      public static bool Resolve(bool? dialogResult) => dialogResult ?? true;
      ```
      调用点全仓 **1 处**：`RoomPage.xaml.cs` 的 `return KeepRoomDecision.Resolve(choice);`
    - 完成判定（可执行）：
      ① `AppShellGuard.CheckKeepRoomDecision` 全仓 `KeepRoomDecision.Resolve(` 的**文件清单必须恰好是 `["RoomPage.xaml.cs"]`**（`SequenceEqual`，两处就红 —— 判据从「≤2」收成「=1」）；
      ② 同方法里四条**真页面栈**出口逐条断言（外壳 × / 返回 / 「保留房间」/「关闭房间」，走真 `Click` 事件），每条都验「交回的 `bool?`」与「`Resolve` 翻译出的布尔」；
      ③ `ActionComputationTests.KeepRoomPrompt` 三条纯函数断言，其中「没拿到选择 ⇒ 保留」是**翻面后**的新口径
    - 来源：QA 独立验收 S3 时提出（2026-10-18，P3 → S4 并入）

- [x] **T49 页面几何只在「设计尺寸」上量过，没在**真显示**的外壳客户区上量过（S5）**
    - 为什么：S4 之后所有页面都装在 `ShellWindow` 的 `PageHost`（`ContentControl`，撑满客户区）里，但现有的三段几何断言量的都不是这个视口 ——
      ① `PageCatalog.CheckPageSkeletons` / `V25BatchProbe.CheckNavCentering` 用的是 `PageProbe.DesignWidth/DesignHeight`（沿用旧窗口 `ClientSize`，房间页 **1220×780**、登录页 **470×792**，**比外壳 1020×720 还宽/高**，运行时并不存在）；
      ② `AppShellQaGuard` 那组用的是**硬编码** 1020×720，是"外壳尺寸"的静态复刻，不是实际客户区（真实客户区 = 窗口尺寸 − 非工作区边框，且会被用户拖动改变、被最小尺寸约束）；
      ③ 于是「导航栏 ≥56 / 标题居中 ≤1px / 主按钮通栏 ≥80%」这三类判据在**真实视口**下从未被验证过：窗口被拖到最小宽度（`MinWidth=760`）时主按钮还够不够 80%、标题还居不居中，目前无人值守
    - 落点：`ClientGUI.Tests/PageCatalog.cs`（新增一组「真视口」测量）/ `ClientGUI.Tests/AppShellQaGuard.cs`（那组 1020×720 改读 `PageHost.ActualWidth/ActualHeight`）/ `ClientGUI/ShellWindow.xaml.cs`（若需要，暴露一个只读的 `PageHost` 客户区尺寸）
    - 优先级：P2（S5 打磨；S4 不动，避免把已签字的 `--ui` 基线再次搅动）
    - 完成判定：
      ① `ShellWindow` 真 `Show()`（离屏）后，从 `PageHost.ActualWidth/ActualHeight` 取**实测客户区**，十页逐页按该尺寸布局并复测三类判据；
      ② 补一组 **`MinWidth`/`MinHeight` 临界值**（760×520）的复测 —— 这是主按钮通栏最可能失守的点；
      ③ 设计尺寸那组**保留**（它守的是"内容宽度不跳"这条独立契约），两组并存且各自说明用途；
      ④ 不得为迁就新视口放宽任何阈值（≤1px / ≥80% / ≥56 一个都不能松）
    - **已闭环（2026-10-19，S5）**：新增 `PageCatalog.RunRealViewportChecks()`（挂在 `--ui`），做**两组**真视口复测，各 10 页：
      ① **真客户区组**：离屏 `Show()` 外壳 → 读 `ShellWindow.PageHostSize`（本轮为它新加的唯一只读属性，零行为变更）→ 十页逐页塞进 `PageHost` 让 WPF 真布局 → 复测三条判据；
      ② **760×520 临界组**：把外壳压到 `MinWidth/MinHeight` 再跑同一套判据 —— 这才是 T49 真正的缺口（"窗口缩到最小还能不能用"此前无人验过）。
    - **四组视口各自验什么**（现在并存，互不替代）：
      | 视口 | 尺寸 | 验的是 |
      |---|---|---|
      | 设计尺寸组 | 房间 1220×780 / 登录 470×792 … | **内容宽度契约**（换回窗口形态也不跳） |
      | 1020×720 组（QA） | 外壳窗口尺寸的静态复刻 | 外壳尺度下的三条判据（独立实现，第二把尺子） |
      | **真客户区组（T49）** | **1005×683**（窗口 1020×720 − 非工作区 15/37） | 装进 `PageHost` 后的**运行时几何** |
      | **760×520 临界组（T49）** | 客户区 **745×483** | **最小窗口**下还能不能用 |
    - 实测（阈值一条未放宽）：
      - 真客户区组：导航栏 ≥56 全过；标题偏移 `0.00 / 0.00 / 0.00 / 0.00 / 0.00 / 0.33 / 0.00 / 0.33 / 0.00 / 0.33`（最差 **0.33px**）；主按钮 48px 高、通栏 **93%～97%**（登录页两标签页 93%/94%，其余 97%）；
      - 760×520 临界组：标题同样 ≤0.33px；主按钮 48px 高、通栏 **91%～96%**（登录页两标签页均 **91%**，离 80% 仍有 11 点余量）——结论：**缩到最小仍然可用**。
    - **Q-T49-01（QA 提 P3，本轮已修）**：第一版"真客户区"组跨机器不可复现，量到 **1205×743**。
      根因**不是**读磁盘（`UiSmokeCheck` 开头已 `UiStateStore.Enabled = false`），而是 `UiState` 的 **C# 默认值是 `Width=1220 / Height=780`**（沿用旧 `MainWindow` 设计尺寸），`ShellWindow.Loaded → RestoreWindowState()` 把它套到窗口上。
      修法两道保险（都不动阈值）：① T49 组内 `UiStateStore.Enabled = false` 并 `finally` 还原（不读磁盘）；② `Show()` **之前**抓下 XAML 声明的 `Width/Height`，`Show()` **之后**显式压回去 —— 于是无论 `RestoreWindowState` 拿到什么，量到的都是外壳设计视口。
      并补一条可执行判据：`|shell.Width − designWidth| < 0.5`，"量到的窗口必须等于外壳声明尺寸"，被带跑了就红。
      修后实测：外壳设计尺寸 **1020×720** → 真客户区 **1005×683**（非工作区 15/37，与 760→745、520→483 完全一致，可交叉验证）。
    - 实现注记（顺序坑）：必须 `Show()` → `UpdateLayout()` → 再设尺寸 / 塞页面。反序会被 `Loaded`（`RestoreWindowState` + `Navigation.Reset(LoginPage)`）覆盖 —— S4 在菜单用例上正是栽在这。
    - 来源：QA 独立验收 S4 时点名"未登记编号"（2026-10-19）
    - 来源：QA 独立验收 S4 时点名"未登记编号"（2026-10-19）；`PageCatalog.Arrange` 的 XML 注释里已写明「设计尺寸 ≠ 运行时视口」的区别，本条是把那半句话补成可执行断言

---

## 已完成

- [x] 2026-10-19 **T47 `GameViewModel` 三条「无参同步询问」入口（= QA Q-S3-01）** —— `DisconnectCommand` / `ResolveKeepRoom()` / `ConfirmKeepRoom` / 两条无参 `LeaveRoom*` 全删；退房出口收敛成 `LeaveRoomForShutdownAsync()` / `LeaveRoomAsync(bool)` / `LeaveRoomForPageAsync(bool)` 三条**只接受显式 keepRoom** 的方法，反射断言钉死。
- [x] 2026-10-19 **T48 `ResolveKeepRoom()` 的 `?? false` 与 I6 反向（= QA Q-S3-02）** —— 随载体删除；`KeepRoomDecision.Resolve(bool?)`（`null ⇒ true`）成为全仓唯一三态判定点，调用点 **1 处**（`RoomPage`），四条真页面栈出口逐条断言。
- [x] 2026-10-19 **T37 两套断言表各管一半** —— 三张窗口表全部切到页面（`AppShellGuard.MigratedPages` 8→10 项、`V25BatchProbe` 导航居中 8→10 组、`AppShellQaGuard` 窗口存在表 → 「8 个必须不存在 + 10 个必须存在」）。
- [x] 2026-10-19 **T38 `App.ShowLogin()` / `App.RunRoomLoop()` 有意保留** —— 两个方法与 `SettingsWindow.Open(Window)` 兜底一并删除，「回退点」说法从注释移除。
- [x] 2026-10-19 **T44 页面内标题不随内容变化** —— 新增可选接口 `IPageTitleSource`，`ShellWindow` 换页时退订/订阅；房主设置页改标题后窗口标题恢复带游戏名（`狼人杀 · 房主设置 · 🐺 狼人杀`）。
- [x] 2026-10-19 **T45 三栏显隐两套通知点** —— `SettingsWindow.ViewOptionsChanged` 随类删除，全仓只剩 `Services.ViewOptions` 一处广播。
- [x] 2026-10-19 **T39 `SettingsWindow.ResolveSession(Window)` 按窗口类型分派** —— 载体已删，「按宿主类型反推会话」在类型层面不可能存在（T31 的另一半权限口径仍开）。
- [x] 2026-10-18 **T41 `PushForResultAsync` 会把下层页面 `OnLeave` 掉** —— 详见上方 T41 条目的「已闭环」注记（`Push` / `PushOverlay` 两种语义 + `DismissOverlay` + P12 18 条断言 + `--ui` 浮层闭环）。
- [x] 2026-10-18 **T36 `PageFactory` 只认 7 页** —— 十个 `PageId` 全部有页面（S3 补齐 保留房间 / 关于 / 更新日志），`PageCatalog.All.Count == 10` 且 `== PageId 枚举值个数`。
- [x] 2026-10-18 **T43 保留房间询问缺真端到端断言** —— `--ui` 的 `RunKeepRoomEndToEndChecks()` 真起服真进房，两条路径（返回 = 保留 / 关闭房间 = 关闭）都验到「出栈回房间列表 + 服务器仍在跑 + 服务器上的房间成员变化」。
- [x] 2026-10-11 **T1 抽掉各窗口重复的「标题行 + 底部状态条」XAML 样板** —— 新建 `ClientGUI/Themes/AppShell.xaml`（导航栏 / 卡片 / 列表行 / 分隔线 / 通栏按钮 / 胶囊 / 表单 / 浮层 八组样式），由既有 `Panels.xaml` 合并它；8 个迁移窗口的标题行与底部条全部改为引用这些样式键，无重复块。
- [x] 2026-10-11 **T2 列表行高 ≥56、点击热区 ≥44** —— `ListRow` 行高 56（房间列表实测 56）、`BlockPrimaryButton` 高 48、`NavIconButton` 44×44、`FlatTextBox` / `FieldInput` 高 44；由 `AppShellGuard` A4 实测守护。
- [x] 2026-10-11 **T4 房主设置窗口游戏徽章的写死色 `#7C8AA5`** —— 改为 `Color="{DynamicResource GameBadgeFallbackColor}"`，兜底色移入两套 `Colors.*.xaml`；XAML 里不再出现该色值，代码里改为「解析失败就保留主题色」。
- [x] 2026-10-11 **T5 菜单下拉项模板的隐式样式覆盖加守护断言** —— 既有 93 项已覆盖菜单树模板；本轮新增 `ClientGUI.Tests/AppShellGuard.cs`（A1–A6 共 80 条）挂在 `--ui`，与既有断言同批次跑。
- [x] 2026-10-11 **T13 明确「改动不自动同步远端」的执行约定** —— `docs/TODO.md` 顶部警示 + `docs/v25/PRD.md` §9.6 均已写明；B0/B1 全程未执行任何 `git commit` / `git push`。
- [x] 2026-10-11 **T9 统一「关闭意图」入口** —— 新建 `ClientGUI/Services/CloseIntent.cs`（四种来源 + `RequiresFullShutdown` / `Describe` 纯函数）；`MainWindow.OnClosing` 改为「`e.Cancel = true` → 走 `DisconnectCommand`」，并删掉 `AppShutdown.ConfirmClose`（关主窗口不停服务器，PRD 8.7）；`AppShutdown` 三步编排与 8s 预算一行未改；`AppShellGuard` 新增 6 条行为表断言。
- [x] 2026-10-11 **T12 本地偏好不含服务端权威数据** —— 复核 `UiState` 无 Title / IsAdmin / Role 字段；新增 `AppShellGuard.CheckLocalStatePurity` 反射断言守住，将来误加会被拦。

- [x] 2026-10-11 **T6 把「是否管理员」收敛为服务端一处判定** —— `AccountStore.IsAdmin(string)`（`OrdinalIgnoreCase`、去首尾空格）成为**唯一**判定入口；`GameHub.IsAdminCaller()` 统一取当前会话判定，管理员 op（数据资源）经它把关；全局不再有按用户名硬编码判断权限的代码。
- [x] 2026-10-11 **T7 密码长度下限这类规则统一到共享文案常量** —— 新增 `Werewolf.Client.Share/Protocol.cs` 的 `PasswordRules`（`MinLength=3` / `MaxLength=64` / `Validate`）与 `ServerMessages` 文案常量，服务端校验与客户端提示引用同一处；B5 改密码闭环到这条。
- [x] 2026-10-11 **T8 词库远程编辑抽象成「服务器可编辑数据资源清单」** —— 新增 `IEditableDataResource` / `EditableDataRegistry`（资源键 `GameId:ResourceId`）+ `UndercoverWordResource`；平台层（`GameHub` / 注册表）不含 `words.undercover.txt` 或 `|` 分隔符等游戏专属知识；`V25BatchProbe` 用纯假游戏模块证明其通用性。
- [x] 2026-10-11 **T10 内置服务器页按钮数量复核** —— 登录窗口内置页由「启动并进入 / 仅启动 / 登录」收敛为**单个「一键开服」按钮**（成功后就地变为「进入房间列表」）+ 停止服务器，语义不再重复；`V25BatchProbe` 断言不再存在内嵌用户名 / 密码输入。
- [x] 2026-10-11 **T18 导航栏标题几何居中** —— 8 个窗口导航栏内层 Grid 由 `Auto|*|Auto` 改为 `*|Auto|*`，左右两侧控件分别 `Left` / `Right` 对齐；`AppShellGuard` / `AppShellQaGuard` 实测标题偏移 ≤0.33px（≤1px 达标），**未改动**两处守卫的居中阈值。
- [x] 2026-10-11 **T19 更新日志 2.5.0 正文按实际交付范围复核** —— `ClientGUI/Models/Changelog.cs` 的 2.5.0 `Items` 重写为 7 条大白话，逐条对应已交付能力（手机化外观 / 导航居中 / 房间列表彩蛋 / 金色称号 / 一键开服自动登管理员 / 设置里远程改数据资源 / 设置里改密码 / 关窗语义），删掉占位文案，做到「写的都是真的做了的」。

## 本轮闭环记录（B0 + B1 · 2026-10-11）

| 步骤 | 内容 |
|---|---|
| ① 读 | 开工前通读 P0 / P1 两组（13 条种子条目） |
| ② 挑 | T1 / T2 / T4 / T5（架构 D9 指定 B1 认领）+ 顺手做掉 T13；T3 按架构 U7 降级为 P1 并做掉其中一半 |
| ③ 记 | T3 标记为 `- [~]` 进行中 |
| ④ 销 | T1 / T2 / T4 / T5 / T13 移入「已完成」（见上） |
| ⑤ 回写 | 新增 T14（下拉框热区）/ T15（半档字号）/ T16（CheckBox 默认外观）/ T17（最后一行分隔线）四条 |

---

## 本轮闭环记录（B2 + B3 · 2026-10-11）

| 步骤 | 内容 |
|---|---|
| ① 读 | 开工前通读 P0 / P1 / P2 三组（含 B1 回写的 T14–T17） |
| ② 挑 | **T9**（架构 D9 指定 B2 认领）+ **T12**（B3 称号落地后正好能复核并加断言守住）；T6 因 B3 顺手把判定入口建好了，已补进度批注留给 B4 销号 |
| ③ 记 | T9 / T12 标记为 `- [~]` 进行中（见上） |
| ④ 销 | T9 / T12 移入「已完成」（见上） |
| ⑤ 回写 | 新增 **T24**（U5 称号长度口径与实际实现不一致）/ **T25**（AppShell 键数被 QA 断言锁死 29 个）/ **T26**（聊天行称号）/ **T27**（控制台称号）/ **T28**（敏感词过滤）/ **T29**（导航标题居中偏差）六条（原拟用 T18–T23，与 QA 回修轮已占用的 T18 / T19 撞号，故顺延为 T24 起） |

**本轮未执行任何 `git commit` / `git push`**（顶部警示）。

## 第二轮闭环记录（QA 回修 · 2026-10-11）

| 步骤 | 内容 |
|---|---|
| ① 读 | QA 验收结论：14 项声明 12 通过，1 个 P1（Q-B1-02）+ 1 个 P2（Q-B1-03）需回修 |
| ② 挑 | 回修 Q-B1-02（AboutWindow 内容被删）+ Q-B1-03（slnx 少了测试项目）；QA 的 P3 观察按机制登记为待办，不擅自扩大改动面 |
| ③ 记 | T18 / T19 入册 |
| ④ 销 | Q-B1-02：`git checkout HEAD -- ClientGUI/Views/AboutWindow.xaml` 整份还原（信息表三行 / 技术栈徽章列表 / 🐺 图标 / 560×470 全部回来），与 HEAD 零差异；Q-B1-03：`WerewolfGame.slnx` 补回 `<Project Path="ClientGUI.Tests/ClientGUI.Tests.csproj" />` |
| ⑤ 回写 | 新增 T18（导航栏标题几何居中）/ T19（更新日志正文按实际交付范围复核）两条 |

### 教训（写给下一轮开工的人）

- **范围外文件不要顺手改**：AboutWindow 属 PRD §4.4.2 的 P2 暂缓项，本轮不该动。上一轮把它连同去 emoji 一起删了信息区块，既有 93 项断言只实例化不校验内容，于是静默通过 —— 是 QA 的实测断言才抓出来。
- **`git checkout -- <file>` 前先 `git diff`**：越权改动往往夹在合理改动里，只有逐文件看 diff 才看得见。

---

## 本轮闭环记录（B4 + B5 · 2026-10-11）

| 步骤 | 内容 |
|---|---|
| ① 读 | 开工前通读 P0 / P1 / P2 三组；架构 §9.5 批次认领表（B4 = T6/T8/T10、B5 = T7）+ QA 回修轮登记的 T18 / T19 |
| ② 挑 | **T6 / T8 / T10**（B4 认领）+ **T7**（B5 认领）+ **T18 / T19**（QA 回修轮登记，本轮闭环）—— 全部与本次「内置管理员 + 一键开服 + 改密码 + 导航居中 + 更新日志」需求同源，合并执行 |
| ③ 记 | T6 / T7 / T8 / T10 / T18 / T19 本轮开工即纳入交付范围并逐条落地 |
| ④ 销 | 六条全部移入「已完成」（见上：T18 实测标题偏移 ≤0.33px 且未改守卫阈值；T19 更新日志重写为 7 条大白话）|
| ⑤ 回写 | 新增 **T30**（一键开服提示里的默认密码在管理员改过后会过时）/ **T31**（设置入口按 owner 窗口类型取会话）/ **T32**（数据资源保存的 Digest 冲突比对缺失）三条 |

**本轮未执行任何 `git commit` / `git push`**（顶部警示）。

---

## 收尾追加闭环记录（用户 2 条 GUI 修改 · 2026-10-11）

| 步骤 | 内容 |
|---|---|
| ① 读 | 先通读 P0 / P1 / P2；并逐条核对 P2 的 T30 / T31 / T32 是否与本次改动相关 |
| ② 挑 | 本次为**用户直接追加的 2 条 GUI 微调**（【改动1】设置里「账号设置」独立成第 4 个标签页；【改动2】点「一键开服」直接进入房间列表）。经核对 **T30 / T31 / T32 均未被本次改动触及**（默认密码提示未变、设置入口仍按 owner 窗口类型取会话、可编辑资源的 Digest 冲突比对仍未做），故不改其状态，另按设计变更新增 T33 / T34 |
| ③ 记 | 两条改动即本交付范围，开工即落地，无「进行中」滞留 |
| ④ 销 | 无 P0 / P1 / P2 条目被本次改动闭环（见②）；两条改动本身记入本记录 |
| ⑤ 回写 | 新增 **T33**（设置窗口 4 页标签契约被 UiSmokeCheck 硬断言）/ **T34**（一键开服自动进入依赖登录窗口模态显示）两条 |

### 本次断言同步（只增不减）

- **规格变更（非放宽）**：`UiSmokeCheck.CheckSettingsWindow` 的页头契约由 `{外观, 主界面, 上次使用}`（3 页）改为 `{外观, 主界面, 上次使用, 账号设置}`（4 页）—— 对应用户要求「账号设置独立成页」，是契约更新不是放宽阈值。
- **新增断言**：账号设置页存在 `ChangePasswordButton` / `ServerSettingsButton`；未登录时改密码不可用、非管理员时服务器设置不可见；`SettingsWindow.AccountEntryVisibility` 纯函数三态（未登录 / 普通用户 / 管理员）；`V25BatchProbe.CheckOneClickAutoEnter` 源码级守护「到达就绪态后紧接着调用 `EnterRoomList()`」。
- 四档实测：默认 **1 5 3 5**、`--ui` **3 0 8**、`--embedded` **3 7**、`--embeddedqa` **5 0**，均 ≥ 上一轮基线 1 5 3 5 / 2 9 7 / 3 7 / 5 0。

**本轮未执行任何 `git commit` / `git push`**（顶部警示）。

---

## BugFix 闭环记录（关闭「保留房间」窗口被误判为「不保留」 · 2026-10-11）

| 步骤 | 内容 |
|---|---|
| ① 读 | 通读 P0 / P1 / P2；定位到「保留房间」询问窗口（`KeepRoomWindow`）与主窗口 `AskKeepRoom` 的判定链 |
| ② 挑 | 本次为用户**明确上报的 Bug**（点标题栏 X / Alt+F4 关「保留房间」窗口被当成「关闭房间」）。核对 T30–T34 均与之无关，故不改其状态 |
| ③ 记 | 根因：`KeepRoomWindow` 只有「保留 / 关闭」按钮与 Esc 会设 `DialogResult`，X / Alt+F4 关闭时为 `null`；`AskKeepRoom` 判 `ShowDialog() == true`，`null == true` → false → 按「不保留」走、服务端把房间关了 |
| ④ 销 | 修复：新增纯函数 `KeepRoomDecision.Resolve(bool?)`（null → 保留）+ `KeepRoomWindow.OnClosing` 兜底把 null 置 true + `AskKeepRoom` 改用 `Resolve`；语义写入窗口与主窗口注释；新增默认组断言（纯函数三态 + 源码级守护） |
| ⑤ 回写 | 新增 **T35**（「单窗口页面化」重构不得丢失「关闭保留房间窗口 = 保留」）|

### 本轮断言同步（只增不减）

- 默认组新增 9 条：`KeepRoomDecision.Resolve(null/true/false)` 三态 + 「null 绝不退化为不保留」；源码级守护（`OnClosing` 存在 / 判 `DialogResult is null` / 置 `DialogResult = true`）+ 仓库根 / 文件存在。
- 四档实测：默认 **1 5 4 4**、`--ui` **3 0 8**、`--embedded` **3 7**、`--embeddedqa` **5 0**，均 ≥ 上一轮基线 1 5 3 5 / 3 0 8 / 3 7 / 5 0。

**本轮未执行任何 `git commit` / `git push`**（顶部警示）。

---

## S1 闭环记录（单窗口外壳 + 登录 / 房间列表 / 房间三页 · 2026-10-17）

| 步骤 | 内容 |
|---|---|
| ① 读 | 通读 P0 / P1 / P2；重点复核 **T35**（页面化不得丢失「关闭保留房间 = 保留」）与 T31 / T34（都与页面化直接相关，均已登记） |
| ② 挑 | 本轮为架构文档 `docs/v25/SHELL-ARCHITECTURE.md` 的 **S1 阶段**。按主理人指示先核对 T35：**页面化后「关闭保留房间窗口 = 保留」未被削弱** —— `KeepRoomWindow` 仍是唯一询问载体，`RoomPage.AskKeepRoom` 依旧只经 `KeepRoomDecision.Resolve(dialog.ShowDialog())` 一处判定（S3 升级成 `KeepRoomPage` 时按架构 §3 复用同一个函数）。故 T35 **不销项**，只确认仍然成立 |
| ③ 记 | 本批落地范围：`ShellWindow`（唯一顶层窗口）+ `NavigationService` / `PageId` / `PageRequest` / `IPage` / `NavKeyMap` / `PageFactory` + `AppSession` + `RoomBackDecision` + 三个页面（`LoginPage` / `RoomListPage` / `RoomPage`）；`App` 入口改一行；`CloseIntent` 增 `ShellCloseIntent` + `Resolve`（**未动既有 4 值枚举**）；`GameViewModel` / `RoomListViewModel` 增 `Detach()`；**旧 10 个 Window 类一行没删**（S4 才删，期间继续被原断言实例化） |
| ④ 销 | 无 P0 / P1 / P2 条目被本批闭环（S1 不删除任何旧窗口、不改玩法与服务端）；T9（关闭意图统一分派）此前已闭环，本批把它的「外壳关闭」维度补齐 |
| ⑤ 回写 | 新增 **T36**（`PageFactory` 只认三页，其余 7 个 `PageId` 抛异常）/ **T37**（旧窗口与页面并存期间两套断言表各管一半，S4 必须同时切换）/ **T38**（`App.ShowLogin` / `RunRoomLoop` 有意保留为唯一回退点，S4 删除）/ **T39**（`SettingsWindow.ResolveSession` 仍按 owner 窗口类型分派，与 T31 合并闭环）/ **T40**（「退出房间」其实没有「取消退房」这条出口）/ **T41**（`PushForResultAsync` 会把下层页面 `OnLeave` 掉，S3 前必须改成浮层式入栈）六条；QA 独立验收通过后再补 **T42**（居中判据两套口径，S5 收紧到 ≤1px）/ **T43**（模态保留房间询问缺一条真端到端断言，S3 补） |

### 本轮断言同步（只增不减：一条未删、一条未替换）

- **默认组 +23**（1 5 4 4 → 1 5 6 7）：`PageCatalog.RunLogicChecks()` —— P5 带结果返回 3 条、P6 关闭意图映射（`ShellCloseIntent` 三值 + `Resolve` 六种组合 + 枚举仍为 3 值）7 条、P7 房间页返回拦截 4 条、P8 / P9 退订（含**来回 5 轮订阅者不增加**的反射计数）6 条、P10 返回键位 3 条。
- **`--ui` +62**（3 0 8 → 3 7 0）：`PageCatalog.RunUiChecks()` —— 页面表自检 3 条、导航栈（深度 / 防重复入栈 / 栈底不出栈 / `OnEnter`·`OnLeave` 成对）13 条、三页骨架实测（能布局 + 导航栏 ≥56 + 标题居中 + 主按钮 ≥44 高、≥80% 通栏）24 条、**真实三页闭环**（登录 → 列表 → 房间 → 返回列表 → 返回登录）13 条、外壳窗口（唯一宿主 + 启动后根内容是登录页 + 标题随页变化）5 条、外壳关闭接线守卫 4 条。
- **阈值未放宽**：页面形态的「导航标题居中」判定与窗口形态**逐字相同**（偏移 ≤ `max(2px, 导航栏宽 5%)`），实测 0.0–0.3px；`CloseIntentSource` 仍是 4 个值、仍只有 `RoomWindow` 不需要完整收尾（既有硬锁一条未动）。
- **既有断言的唯一文案变更**：`CloseIntent.Describe(RoomListWindow)` 由「关房间列表：退出程序，完整收尾」改为「关外壳（登录页／房间列表）：退出程序，完整收尾」—— 既有断言只校验非空、不校验内容，故条数不变（属**沿用**，不是放宽）。
- 四档实测：默认 **1 5 6 7**、`--ui` **3 7 0**、`--embedded` **3 7**、`--embeddedqa` **5 0**；均 ≥ 上一轮基线 1 5 4 4 / 3 0 8 / 3 7 / 5 0。

### 与用户并行改动的冲突

- 无。开工前逐个 `Read` 过将要修改的文件（`CloseIntent.cs` / `App.xaml.cs` / `GameViewModel.cs` / `RoomListViewModel.cs` / `EmbeddedServerViewModel.cs` / `SettingsWindow.xaml.cs` / 三个旧窗口的 XAML 与代码隐藏）；本轮所有改动均为**追加**或**单行入口替换**，未覆盖任何既有逻辑；未发现他人并行修改的痕迹。

**本轮未执行任何 `git commit` / `git push`**（顶部警示）。

---

## S2 闭环记录（四个表单/设置页 · 2026-10-18）

| 步骤 | 内容 |
|---|---|
| ① 读 | 通读 P0 / P1 / P2；本批的硬前置是 **T41**（QA 已明确「先改实现、再补断言」），并顺带核对 T36 / T39 的进度 |
| ② 挑 | 架构文档 `docs/v25/SHELL-ARCHITECTURE.md` 的 **S2 阶段**：`CreateRoomPage` / `HostSettingsPage` / `SettingsPage` / `InviteCodePage` 四页 + 四条入口改走页面栈。**先修 T41**（`NavigationService` 分出 `Push` / `PushOverlay`），再迁页面 —— 否则迁完第一件事就是把缺陷锁进测试 |
| ③ 记 | 新建：`Pages/CreateRoomPage.xaml(.cs)`、`Pages/HostSettingsPage.xaml(.cs)`、`Pages/SettingsPage.xaml(.cs)`、`Pages/InviteCodePage.xaml(.cs)`、`Services/ViewOptions.cs`（三栏显隐的独立广播点）、`Services/CreateRoomResult.cs`（浮层返回值）。修改：`NavigationService.cs`（`Push` / `PushOverlay` / `PushForResultAsync` 改浮层 / `DismissOverlay` / `PageFactory` 认 7 页）、`ShellWindow.xaml.cs`（× 在浮层上 = 收起一层）、`RoomListPage.xaml.cs`（创建房间 → 带结果返回；设置 → 浮层）、`RoomPage.xaml.cs`（设置 / 房主设置 / 邀请码 → 浮层；三栏联动改订阅 `ViewOptions`）、`App.xaml.cs`（`Ctrl+,` → 浮层）、`ClientGUI.Tests/PageCatalog.cs`（页面表 3 → 7 页 + P12 + S2 结构契约 + 浮层闭环 + 入口接线守卫）。**旧 10 个 Window 类一行没删**（S4 才删） |
| ④ 销 | **T41 闭环**（实现 + 断言双侧补齐）；T36 进度推进到 7/10 页、T39 在页面侧已无 `switch (owner)`（待 S4 删旧窗口收尾） |
| ⑤ 回写 | 新增 **T44**（页面标题不再随内容变化：`IPage.Title` 只在换页那一刻被读一次）/ **T45**（三栏显隐存在两套通知点，S4 删窗口时合一）/ **T46**（浮层的模态感只靠 `IsHitTestVisible`，真叠层渲染留作观感项） |

### 本轮断言同步（只增不减：一条未删、一条未替换）

- **默认组 +18**（1 6 1 8 → 1 6 3 6）：`PageCatalog.CheckOverlaySemantics()`（P12）—— 换页 vs 浮层的分界 2 条、浮层入栈（栈深 +1 / `CurrentIsOverlay` / 不调下层 `OnLeave` / 不调下层 `OnEnter`）4 条、重复浮层不加深 1 条、浮层出栈（返回 / 栈深 / 不补 `OnLeave` / 不重入 / `CurrentIsOverlay` 复位）5 条、`DismissOverlay` 3 条、无等待者时 `Complete` 2 条、`PushForResultAsync` 本身 1 条（原有 P5 未动）。
- **`--ui` +101**（4 3 7 → 5 3 8）：四页骨架实测（能布局 + 导航栏 ≥56 + 标题居中 + 主按钮 ≥44 高、≥80% 通栏）28 条、四页结构契约 42 条（创建房间 6 / 房主设置 14 / 设置 17 / 邀请码 5，含 I4 的「4 页且顺序固定」）、**S2 浮层闭环**（真页面 `IsHitTestVisible` / `DataContext` 仍在 / 同一实例 / 进出 5 轮订阅者 `1 → 1` / `DismissOverlay`）15 条、入口接线守卫（源码级）12 条、页面表自检 +1 条（3 → 7 页）、其余 3 条为骨架循环随页面数自然增长。
- **阈值未放宽**：四页的「导航标题居中」判定沿用 `PageCatalog` 原判据（与窗口形态逐字相同），实测偏移 0.0 / 0.0 / 0.0 / 0.3px；`PageId`、`CloseIntentSource`（4 值）、`ShellCloseIntent`（3 值）三个枚举一个也没动；`ShellWindow` 的 `Navigation.BackAsync()` 出口数仍为 **2**（× 收起浮层走 `DismissOverlay`，没有绕过 N9 的单一出口纪律）。
- 四档实测（临时输出目录复核，见下条「构建冲突」）：默认 **1 6 3 6**（其中 1 条为 QA 的 T41 旧守卫，需 QA 侧翻转）、`--ui` **5 3 8**、`--embedded` **5 4**、`--embeddedqa` **5 0**；条数均 ≥ 上一轮基线 1 6 1 8 / 4 3 7 / 5 4 / 5 0。
- **唯一一条变红的是 QA 自己的守卫**：`ClientGUI.Tests/S1QaGuard.cs` 里「产品代码里没有 `PushForResultAsync` 调用方」这条断言（原意是「S1 内 T41 不可达」）。S2 修好 T41 并让 `RoomListPage` 真正用上「带结果返回」后，它按设计不再成立 —— 由 QA 翻转为「恰好 1 个调用方且走浮层」。**这条不由我改**（QA 的文件，避免覆盖并行改动）。

### 构建冲突（按约定处理，未报红）

- 第一次 `dotnet build` 撞上 `MSB3026 / MSB3027`：`ClientGUI/bin/Debug/net8.0-windows/Werewolf.Server.dll` 被运行中的 `ClientGUI.exe`（PID 52344，Console 会话）锁定。按与 QA 约定的处理顺序：**先看进程 → 该进程是正在运行的客户端（不是编译残留）→ 不杀它**，改用 `-p:OutputPath=bin/_s2check/` 做了一次**临时输出目录**的编译 + 四档复跑（0 警告 0 错误），等该进程退出后再按标准输出目录复跑一遍。`-p:BaseOutputPath` 的长期拆分仍按 QA 的意见**不做**。

### 与用户并行改动的冲突

- 无。开工前逐个 `Read` 过将要修改/搬迁的文件（`CreateRoomWindow` / `HostSettingsWindow` / `SettingsWindow` / `InviteCodeWindow` 的 XAML 与代码隐藏、`NavigationService.cs`、`ShellWindow.xaml.cs`、`RoomListPage.xaml.cs`、`RoomPage.xaml.cs`、`App.xaml.cs`、`PageCatalog.cs`）；四页均为**从旧 XAML 搬迁 + 改造**，旧文件一字未动；其余改动为追加或单处替换。

**本轮未执行任何 `git commit` / `git push`**（顶部警示）。

### S2 返修（QA 复核发现 P2 · 2026-10-18）

QA 独立复核确认 T41 的实现侧三条都对，但抓到一条**浮层叠浮层**的 P2：

- **现象**：创建房间浮层开着 → `Ctrl+,` 打开设置（浮层叠浮层）→ 收起设置 → 回到创建房间页点「创建」→ **房间静默不建**（`Complete()` 返回 false → 走兜底弹回列表，无任何提示）。
- **根因**：`NavigationService` 的「等待结果」是**单个字段**（`_pendingResult`），而 `PopCore` 在**任何**出栈后都会 `TrySetResult(null)` —— 于是「弹出上层设置浮层」把下层创建房间的等待判成了「取消」。
- **修法**：等待者从「导航服务的字段」挪到**栈格**（`Entry.Pending`）。`PushForResultAsync` 先压栈、再挂到刚压进去那一格；`PopCore` 只结算**被弹出的那一格**；`Complete` 只在**栈顶那一格确实在等结果**时才出栈。同一次改动顺带修掉两个同根隐患：① 同 `PageId` 复用弹掉旧格子时，旧格子的等待当场按「取消」结算（原来连点两次「创建房间」，第一次的 `await` 永不返回）；② `Reset()` 清栈时结算所有在场格子（原来会永久悬挂）。
- **断言**：我这边加了 `PageCatalog.CheckOverlayResultChannel()`（P13，12 条，与 QA 的 `S2QaGuard` 各自独立实现，互不复用辅助方法）：上层浮层来去后下层等待仍存活、结果原样送达、`Complete` 返回值正确、复用弹掉旧格子按取消结算不挂住、`Reset` 结算不悬挂。

**返修后四档实测**（临时输出目录）：默认 **1 7 1 8**（全绿；含 QA 后补的 `CheckPendingNeverHangs` 9 条）、`--ui` **5 9 5**（全绿）、`--embedded` **5 4**、`--embeddedqa` **5 0**。QA 的 2 条红已转绿，条数只增不减。可加性核对：`--ui` 由「工程师侧 `5 3 8` + QA 侧 `5 7`」组成，与 QA 拆装对照的结果逐位一致。

**一条观察（不是缺陷，留给以后）**：`--ui` 里 `EmbeddedServerService.Changed` 的订阅者数会累积到 41 —— 因为 `--ui` 进程里为了跑断言创建了几十个 `AppSession`，而每个 `EmbeddedServerViewModel` 都会订阅这个**静态**事件且没有释放入口。生产环境一个进程只有一个 session，所以不是泄漏；若将来要在测试里收紧这个绝对值，得让 `EmbeddedServerViewModel` 可释放。目前 QA 的判据是「进出 N 轮前后不涨」，这是对的。

---

## S3 闭环记录（关于 / 更新日志 / 保留房间 · 2026-10-18）

| 步骤 | 内容 |
|---|---|
| ① 读 | 通读 P0 / P1 / P2；本批三条主线：迁三页（T36 收尾）、T43 真端到端、跨特性「房间 → 关于 → 更新日志 → 返回 → 返回」不丢数据不涨订阅 |
| ② 挑 | 架构 §9.1 的 S3 阶段：`AboutPage` / `ChangelogPage` / `KeepRoomPage`。**入栈语义按用途分**：关于 / 更新日志 = `Push`（换页，下层正常退订）；保留房间 = `PushForDecisionAsync`（浮层，下层**必须**活着 —— T41 的原始事故形态就是"问的人自己被退订了"） |
| ③ 记 | 新建：`Pages/AboutPage.xaml(.cs)`、`Pages/ChangelogPage.xaml(.cs)`、`Pages/KeepRoomPage.xaml(.cs)`。修改：`NavigationService.cs`（`PushForDecisionAsync` 返回 `Task<bool?>`；`PageFactory` 认满 10 页）、`GameViewModel.cs`（新增 `LeaveRoomAsync(bool)` / `LeaveRoomForPageAsync(bool)` 两条重载，统一委托给 `LeaveRoomCoreAsync` / `LeaveRoomForPageCoreAsync`；`ConfirmKeepRoom` 的 `Func<bool>` 签名**一行未动**）、`RoomPage.xaml(.cs)`（帮助 → 关于 / 更新日志改 `Push`；菜单与导航栏「退出房间」改走同一个 `Click` 的异步询问路径；`OnEnter` 不再注入同步询问）、`ClientGUI.Tests/PageCatalog.cs`（页面表 7 → 10 + S3 结构契约 + S3 入口接线 + T43 端到端）、`UiSmokeCheck.cs`（T43 挂在最后一批 + 一行合计）。**旧 10 个 Window 类一个没删**（S4 才删） |
| ④ 销 | **T36 闭环**（10/10 页，含完成判定口径修正）、**T43 闭环**（46 条真端到端断言）；T44 / T45 / T46 仍在（S4/S5），T40 仍在（「取消退房」这条出口 S3 也没加，`KeepRoomPage` 仍是「保留 / 关闭」两个出路） |
| ⑤ 回写 | 无新增待办。新增的只有一条**实现注记**（见 T43 的"踩过的坑"）：WPF 测试宿主必须自己补 `DispatcherSynchronizationContext`，否则 `await` 续体会掉到线程池 —— 这不是产品缺陷，别去改产品代码 |

### 页数契约 7 → 10（规格变更，逐条列明）

| # | 变更 | 位置 | 理由 |
|---|---|---|---|
| 1 | `PageCatalog.All` 7 → 10 条 | `ClientGUI.Tests/PageCatalog.cs` | S3 迁完最后三页，页面表必须与 `PageId` 的十个值对齐 |
| 2 | `CheckCatalog` 的 `All.Count == 7` → `== 10` | 同上 | 同上 |
| 3 | 新增 `S3Pages`（3 条）并断言 `S3Pages.Count == 3` | 同上 | 与 `S2Pages` 同构，便于"新增页面没进表 = 等于没迁"这条自检定位 |
| 4 | 新增 `All.Count == Enum.GetValues<PageId>().Length` | 同上 | 把"页数契约"从硬编码数字升级成**与枚举同源**，以后再也不会漂 |
| 5 | `PageFactory` 补 3 个分支（共 10 个） | `ClientGUI/Services/NavigationService.cs` | 十个枚举值全部有页面，兜底 `NotSupportedException` 只为"将来新增枚举忘了加页面"存在 |
| 6 | `SkeletonGuarded` 随 `All` 自然从 7 → 10 | `PageCatalog.cs` | 定义是"已迁移页面全部受骨架守护"，不需要单独改，但条数会跟着涨（`--ui` 每页 5 条） |
| 7 | T36 的完成判定里"`SkeletonGuarded.Count == 8`"改为 10 | `docs/TODO.md`（T36） | 架构 §8.3 写"8"是 S1 时按三页 + 五个批次的估计；S1+S2+S3 实际共 10 页，按实际修正 |

### 三页「旧 N → 新 M」对照（旧窗口形体一条不少）

| 页面 | 旧载体 | 设计尺寸 | 入栈语义 | 保留的形体（都有断言） |
|---|---|---|---|---|
| 关于页 | `Views/AboutWindow.xaml(.cs)` | 470×560 | `Push`（换页） | 🐺 58×58 图标徽章 / 产品名 + `v` 版本胶囊 + 框架说明 / 简介段 / **信息表三行**（解决方案 · 客户端 · 网络加密）/ **技术栈徽章**（`ItemsControl` + `ItemTemplate`，条目数 = `AppInfo.TechStack.Count`）/ 免责说明 / 「更新日志」+「关闭」两个按钮（右对齐） |
| 更新日志页 | `Views/ChangelogWindow.xaml(.cs)` | 560×620 | `Push`（换页） | 📜 图标 + 标题 + 「当前版本 vX.Y.Z」/ 版本列表**倒序**（断言版本号逐条严格递减）/ 「最新」标记只挂第一条 / 长文本在 `ScrollViewer` 里 / 版本徽章文字色跟随主题（浅色主题不白字白底 —— B3 修过的回归点）/ 底部「关闭」 |
| 保留房间页 | `Views/KeepRoomWindow.xaml(.cs)` | 456×541 | `PushForDecisionAsync`（浮层，`Task<bool?>`） | 「保留房间」通栏主按钮（≥44 高、≥80% 通栏）/「关闭房间」次按钮 /「返回 = 保留」提示行 / 三态判定仍只经 `KeepRoomDecision.Resolve`（`true`/`false`/`null⇒保留` 三条断言） |

### 本轮断言同步（只增不减：一条未删、一条未替换我的既有断言）

- **默认组 ±0 条**（1 7 7 9 → 1 7 7 9）：S3 的纯逻辑缺口已被 S2 的 P12/P13 覆盖（浮层语义 + 结果通道），本轮没有新增默认组断言。**其中 3 条是 QA 的 `S2QaGuard` 旧口径**（「`PageId.KeepRoom/About/Changelog` 本轮**不该**已接入」），随页数契约 7 → 10 按设计不再成立，需 QA 侧翻转（见下）。
- **`--ui` +106**（6 1 6 → 7 2 2）：三页骨架实测（能布局 / 导航栏 ≥56 / 标题居中 / 主按钮通栏）、S3 结构契约 28 条（关于 13 / 更新日志 8 / 保留房间 7）、S3 入口接线守卫 11 条（源码级：`Push` 两处、`PushForDecisionAsync`、`Resolve(choice)`、`LeaveRoomForPageAsync(keepRoom)`、页面里没有 `new AboutWindow/ChangelogWindow/KeepRoomWindow`、「退出房间」不再绑 `DisconnectCommand` 且两处都走同一个 `Click`、`PageFactory` 覆盖十个 `PageId`）、页面表自检 2 条、**T43 端到端 46 条**，其余为骨架循环随页面数自然增长。
- **阈值未放宽**：三页的「导航标题居中」沿用 `PageCatalog` 原判据（与窗口形态逐字相同），实测偏移 0.0 / 0.0 / 0.3px；保留房间页主按钮实测 424/456 = 93% 通栏、48 高。`CloseIntentSource`（4 值）/ `ShellCloseIntent`（3 值）/ `CloseIntent` 映射表 / `KeepRoomDecision.Resolve` 的"null ⇒ 保留"一条也没动；`ShellWindow` 的 `Navigation.BackAsync()` 出口数仍为 **2**。
- 四档实测（临时输出目录 `ClientGUI.Tests/bin/_s3check/`，原因见下条「构建冲突」）：默认 **1 7 7 9**（1 7 7 6 通过 + **3 条红 = QA 的旧口径**）、`--ui` **7 2 2**（7 2 1 通过 + **1 条红 = QA 的旧口径**）、`--embedded` **5 4**、`--embeddedqa` **5 0**；总数均 ≥ 开工令给的基线 1 7 7 9 / 6 1 6 / 5 4 / 5 0。
- **4 条变红全部是 QA 自己的守卫**（`ClientGUI.Tests/S2QaGuard.cs` 的「S3 范围不该已接入」），它们是 S2 阶段"先加后删"记账本的一部分，按设计在 S3 翻面。**这 4 条不由我改**（QA 的文件，避免覆盖并行改动），已把位置与建议替换文本同步给 QA。翻转后预期：默认 **1 7 7 9 全绿**、`--ui` **7 2 2 全绿**。

### 跨特性验收（开工令第 7 条）实测

`RoomPage → 关于 → 更新日志 → 返回 → 返回`（真服务器、真房间、房主独自在房）：

- 压栈期间房间页 `DataContext == null`（`OnLeave` 成对调用，视图模型已 `Detach`），`GameClient.StateReceived` 订阅者 **1 → 0**；
- 逐层退回后回到**同一个** `RoomPage` 实例，视图模型是**新的**一份（旧的 `IsDetached == true`）；
- 玩家列表人数 / 玩家名字 / 房间号 / 标题条逐一与离开前相同（`OnEnter` 先按 `GameClient.LastState` 回填、再主动拉一次 `roomstate`）；
- `GameClient.StateReceived` 订阅者 **1 → 1**、`EmbeddedServerService.Changed` 订阅者前后相等 —— 订阅数**不涨**。

### 构建冲突（按约定处理，未报红）

- `ClientGUI.exe`（PID 52344，仍是上次会话留下的运行中客户端）继续锁着 `ClientGUI/bin/Debug/net8.0-windows/Werewolf.Server.dll`。按约定：**先看进程 → 是正在运行的客户端（不是编译残留）→ 不杀它** → 用 `-p:OutputPath=bin/_s3check/` 做临时输出目录的编译 + 四档复跑（0 警告 0 错误）。`--embedded` 有一次 403（沙箱拒绝外部地址），按纪律**不重试、不改探针**，下一次复跑即恢复 `5 4`。

### 与用户并行改动的冲突

- 无。开工前逐个 `Read` 过要改的文件（`RoomPage.xaml(.cs)`、`GameViewModel.cs`、`NavigationService.cs`、`PageCatalog.cs`、`UiSmokeCheck.cs`）；三页均为**从旧 XAML 新建 + 改造**，旧 `AboutWindow` / `ChangelogWindow` / `KeepRoomWindow` 一字未动；`RoomPage` 的改动是"换接线 + 删掉不再使用的 `AskKeepRoom`（页面内不再 `new KeepRoomWindow`）"。

**本轮未执行任何 `git commit` / `git push`**（顶部警示）。

---

## S4 闭环记录（删 10 个旧 Window + 断言迁移 · 2026-10-19）

| 步骤 | 内容 |
|---|---|
| ① 读 | 通读 P0 / P1 / P2；本批两条主线：**删干净 10 个旧 `Window` 类**（S1–S3 一直「先加后删」留着的那批）、**把断言里的 Window 引用清零**（T37）。并入 T47（QA Q-S3-01）/ T48（QA Q-S3-02）直接销号；顺带收 T38 / T44 / T45 / T39 |
| ② 挑 | 不做新功能、不加新页面、不改任何行为 —— S4 是**纯收口批**。唯一"新写"的代码是 T44 的标题通知（`IPageTitleSource`），因为窗口标题随内容变这件事在旧窗口上是异步的、页面化后必须补一条通道，否则是**功能回退** |
| ③ 记 | **删 20 个文件**（10 个类 × XAML + 代码后置，见下）/ **改 9 个已跟踪的产品文件**（`App.xaml` / `App.xaml.cs` / `Controls/OverlayDialog.xaml.cs` / `Services/AppShutdown.cs` / `Services/CloseIntent.cs` / `Themes/Panels.xaml` / `ViewModels/EmbeddedServerViewModel.cs` / `ViewModels/GameViewModel.cs` / `ViewModels/RoomListViewModel.cs` —— 除 `GameViewModel` 的 T47/T48 删除外，其余**全是注释与接线清理**）/ **改 5 个测试文件**（`AppShellGuard` / `UiSmokeCheck` / `ActionComputationTests` / `V25BatchProbe` / `PageCatalog`）。产品侧本轮**唯一新增的代码**是 `IPageTitleSource` 接口与 `ShellWindow` 里的订阅/退订（T44），**没有新增任何文件** |
| ④ 销 | **T47、T48、T37、T38、T44、T45、T39** 七条（见「已完成」）。T40 / T42 / T46 **保持不动**（T42 是 S5 的判据统一，T46 是观感项，T40 要新增「取消退房」出口 —— 都不在 S4 范围） |
| ⑤ 回写 | 新增 **T49**（S5：真显示外壳后按 `PageHost.ActualWidth/ActualHeight` 复测页面几何 —— QA 终验点名"未登记编号"，属登记完整性欠账）。另留三条实现注记：① `S3QaGuard` 的 H 判据在 T47 之后必须从「有 `ConfirmKeepRoom = () => false`」改成「有 `keepRoom: false`」，否则它和同文件的 G（`ConfirmKeepRoom` 必须归零）互斥；② 断言里数旧标识符**必须带左括号**（`ResolveKeepRoom(`），因为页面侧有个合法的 `ResolveKeepRoomAsync`；③ 默认组是 **MTA**，任何 `UserControl` 构造都不能出现在 `RunLogicGuards()` 里（详见下文「本轮踩过的三个坑」） |

### 删除清单：20 个文件（10 个类 × XAML + 代码后置）

| # | 类 | 删除的文件 | 原职责 | 现载体 |
|---|---|---|---|---|
| 1 | `LoginWindow` | `ClientGUI/Views/LoginWindow.xaml` + `.xaml.cs` | 登录 / 一键开服的根窗口 | `Pages/LoginPage` |
| 2 | `RoomListWindow` | `ClientGUI/Views/RoomListWindow.xaml` + `.xaml.cs` | 房间列表 + 彩蛋热区 | `Pages/RoomListPage` |
| 3 | `MainWindow` | `ClientGUI/MainWindow.xaml` + `.xaml.cs` | 房间内三栏 + **菜单栏** + 关闭意图分派 | `Pages/RoomPage`（三栏 / 菜单）+ `ShellWindow`（关闭意图） |
| 4 | `CreateRoomWindow` | `ClientGUI/Views/CreateRoomWindow.xaml` + `.xaml.cs` | 建房表单 + 游戏下拉 | `Pages/CreateRoomPage` |
| 5 | `HostSettingsWindow` | `ClientGUI/Views/HostSettingsWindow.xaml` + `.xaml.cs` | 房主设置（身份配置 / 游戏徽标） | `Pages/HostSettingsPage` |
| 6 | `SettingsWindow` | `ClientGUI/Views/SettingsWindow.xaml` + `.xaml.cs` | 四标签页设置 + `ResolveSession(Window)` + 自带 `ViewOptionsChanged` | `Pages/SettingsPage` + `Services/ViewOptions` |
| 7 | `InviteCodeWindow` | `ClientGUI/Views/InviteCodeWindow.xaml` + `.xaml.cs` | 邀请码展示 / 复制 | `Pages/InviteCodePage` |
| 8 | `KeepRoomWindow` | `ClientGUI/Views/KeepRoomWindow.xaml` + `.xaml.cs` | 模态「是否保留房间」（`DialogResult` 三态 + `OnClosing` 兜底） | `Pages/KeepRoomPage`（浮层 + `Task<bool?>`） |
| 9 | `AboutWindow` | `ClientGUI/Views/AboutWindow.xaml` + `.xaml.cs` | 关于（信息表 / 技术栈 / 🐺） | `Pages/AboutPage` |
| 10 | `ChangelogWindow` | `ClientGUI/Views/ChangelogWindow.xaml` + `.xaml.cs` | 更新日志（倒序长文） | `Pages/ChangelogPage` |

**删完的硬判据（三段，都在 `PageCatalog.CheckPushWiring` 里）**：① 全仓 `new <10 个旧类>` **0 处**（剥行注释）；② 20 个文件路径全部**不存在**；③ `ClientGUI/Views` 下 `*Window.xaml` **0 个**（外壳 `ShellWindow.xaml` 在外层目录，不在 `Views` 里）。另加 QA 侧一条更硬的：**整个 `ClientGUI` 里 `class X : Window` 只剩 `ShellWindow` 一个**（正则扫类声明，不搜字符串 —— 否则 `WindowStartupLocation.CenterScreen` 之类会误命中）。

### 断言迁移表（旧删 N / 新覆盖 M · **静态核算**，实测见下一节）

> 口径：**只算被删除/替换的断言条数**，载体换了但判据一字未改的记在「载体替换」列（不计入 N/M，避免虚增）。行内数字按文件归属。
> ⚠ 本表是**静态核算**（按代码路径数出来的），**不是跑数** —— 真实签字数字见下面「构建与实测」一节。两者对不上时以实测为准。

| 文件 | 旧删 N（窗口形态） | 新覆盖 M（页面形态） | 载体替换（判据不变） | 净变化 |
|---|---|---|---|---|
| `UiSmokeCheck.cs` | **10** 条 `Try("XxxWindow")`（10 个窗口实例化） | **14**：10 条 `TryPage("XxxPage")` + **4 条新增**（`TryShellWindow` 1 条「外壳可实例化且有唯一页面宿主」+ `CheckShellTitleFollowsPage` 3 条：外壳标题随页变化 / 游戏变了标题跟着变 / 外壳里能换到房主设置页） | 菜单栏 5 条（主窗口→房间页）+ 菜单模板 4 条 + 登录标签页 24 条 + 设置 24 条 + 房主设置 4+7 条 + 建房下拉 4 条 + 主题 1 条（`LoginWindow`→`ShellWindow`） | **+4** |
| `AppShellGuard.cs` | **45**：骨架 8×5=**40**（`MigratedWindows` 8 项）+ `CheckKeepRoomDecision` 的源码 grep **5**（`root != null` / `KeepRoomWindow.xaml.cs 存在` / `OnClosing` / `DialogResult is null` / `DialogResult = true`） | **68**：骨架 10×5=**50**（`MigratedPages` 由 `PageCatalog.SkeletonGuarded` 投影）+ `CheckKeepRoomExitPaths` **18 条行为级**（四条真出口 × 4 = 16〔交回的 `bool?` / `Resolve` 翻译 / 入栈栈深 2 / 出栈栈深 1〕+「`KeepRoomDecision.Resolve(` 文件清单恰好 `[RoomPage.xaml.cs]`」1 +「`ResolveKeepRoom(` / `ConfirmKeepRoom` 全仓 0 处」1） | `CheckTapTargets` 1 条（`RoomListWindow`→`RoomListPage`） | **+23** |
| `V25BatchProbe.cs` | **8** 条导航居中（8 个窗口） | **10** 条导航居中（`PageCatalog.All` 十页，多测关于 / 更新日志） | 一键开服状态机 10 条（`LoginWindow`→`LoginPage`，`Embedded` 同名） | **+2** |
| `PageCatalog.cs` | **3**：S2「页面里没有 new 4 个旧窗口」1 条（只扫 `Views/Pages`）+ S3「页面里没有 new 3 个旧窗口」1 条 + S3「`RoomPage.xaml` 不绑 `Command="{Binding DisconnectCommand}"`」1 条（只判那一种绑定形态） | **6**：全仓 `new` 10 类归零 / 20 个文件已删 / `Views` 下无 `*Window.xaml` / `RoomPage.xaml` 剥注释后 `DisconnectCommand` 0 处 / 全仓 `DisconnectCommand` 代码 0 处 / （「房间页两个入口同一个 `Click`」由原来那句 `Check.Equal(2, …)` 保留） | S2 四条入口接线 4 条 + S3 三条入口接线 3 条 + `PageFactory` 覆盖十页 1 条 | **+3** |
| `ActionComputationTests.cs` | **2**（`ConfirmKeepRoom` 注入 + `ResolveKeepRoom()`，依赖的成员已删） | **4**：`KeepRoomDecision.Resolve` 三态 3 条（含 `null ⇒ 保留` 的翻面口径）+ 反射「`LeaveRoom*` 恰好三条且都带 `keepRoom`」1 条 | `IsHost` / `IsJoined` / `NeedsKeepRoomDecision` 7 条 | **+2** |
| `AppShellQaGuard.cs`（QA） | **41**：骨架 40 + `AboutWindow.xaml 存在` 1 | **52**：骨架 51（`1 + 10×5`）+ 1 | 实测判据 8 组 → 10 组 | **+11** |
| **合计（我的 5 个文件）** | **68** | **102** | — | **+34** |

**M ≥ N 的落地方式**：不是靠"补几条凑数"，而是每条被删的窗口断言都有一条**语义等价或更严**的页面断言接手 —— 三处最关键的"更严"：

1. `KeepRoomWindow.OnClosing` 的**源码 grep**（4 条，只能证明"代码里有这行"）→ **真页面栈四条出口**（走真 `Click` 事件、真 `DismissOverlay`、真 `BackAsync`），每条都断言"交回的 `bool?`"与"`Resolve` 翻出来的布尔"，**证明的是"这一行真的产生了保留的结果"**；
2. `AppShellQaGuard` 的「8 个窗口文件**存在**」→ 翻成「8 个文件**不存在**」+「10 个页面文件**存在且带骨架**」，正反两面都钉（原来是单向的）；
3. `PageCatalog` 的「页面里没有 `new XxxWindow`」（只扫 `Views/Pages`，看不见旧 `App` / 旧窗口互 new）→ **全仓** `new` 归零 + 20 个文件名归零 + `Views` 下 `*Window.xaml` 归零。

**另加 7 条（不在这张 N/M 对照内）**：QA 的「恒真 / 睡着守卫」扫描发现 `PageCatalog` 有两处**静默吞断言**（不是恒绿 —— 恒绿至少还占着条数，静默返回连条数都没了），收尾时一并补掉：
- `MeasurePrimary` 第一行 `if (FindName(elementName) is not FrameworkElement) return;` —— `ExpectPrimary == true` 的 7 页每页 3 条，**最多 21 条可以无声消失**。改为加 `bool required = false`：required 时缺件判红，非 required 时记一条「本页按设计无此按钮」的绿断言 —— **任何路径都至少 1 条断言，没有静默 return**（7 页各 3 条不再可能消失）；
- `CheckPrimary` 的 `if (Tabs is ... && EmbeddedPrimaryAction != null) {…6 条…} else {…3 条…}` —— 登录页丢 `Tabs` 会静默降级。改为按 `PageId.Login` 显式分派 + 缺件判红（登录页 **+2 条**：`Tabs` 存在 / `EmbeddedPrimaryAction` 存在，6 → 8）；
- 调用点 `if (probe.ExpectPrimary) CheckPrimary(...)` 的预筛去掉 —— 十页**每页都进**，非 required 的 3 页（房间 / 关于 / 更新日志）各记 1 条「按设计无通栏主按钮」（**+3 条**），条数从此可对账；
- `ReadPage()` 缺文件返回 `""`（下一条断言 `"".Contains(…)` 恒绿）→ 先断言文件存在（**+2 条**，`RoomListPage` / `RoomPage` 各一）。

**另修一处静默分支（同为「静默跳过比恒绿更危险」这一类，条数不变、但不再可能无声消失）**：`UiSmokeCheck.ExerciseRoomPageMenu` 里那句 `if (shown) Check.Equal(opened, realized, …)` —— 环境显示不出窗口时整条断言**静默跳过**。已改成 `if/else` 两支各产一条断言（else 分支记录 `opened` 值并说明"改为按图结构断言"）。这条与 QA `§21.9` 的静默点清单同源（她登记了 4 条，这是第 5 条）。

**同时纠正一个口径**：`PageCatalog` 的 1020×720 之外的 `DesignWidth/DesignHeight`（房间页 1220×780、登录页 470×792）是**设计尺寸**，不是运行时视口 —— S4 之后页面都装在 `ShellWindow` 的 1020×720 `PageHost` 里。所以「导航栏 ≥56 / 标题居中 / 主按钮通栏 ≥80%」这组只在「页面自身体形不缩水」这个意义上成立；「装进外壳后还够不够宽」由 `AppShellQaGuard` 的 1020×720 那组实测（T37 分工）。已在 `Arrange` 的 XML 注释里写明，避免后来人拿错数据下结论。

### T47 / T48 闭环（原 Q-S3-01 / Q-S3-02）

| 项 | 改前 | 改后 | 判据 |
|---|---|---|---|
| T47 三条无参同步询问入口 | `DisconnectCommand` / 私有 `LeaveRoomAsync()` / `LeaveRoomForPageAsync()` 三条不接收显式选择的出口 + `ConfirmKeepRoom`（`Func<bool>`） | 只剩 `LeaveRoomForShutdownAsync()`（内部 `keepRoom: false`）/ `LeaveRoomAsync(bool)` / `LeaveRoomForPageAsync(bool)` | 反射：`LeaveRoom*` 恰好这三条；全仓 `ConfirmKeepRoom` 0 处 |
| T48 `?? false` 与 I6 反向 | `ConfirmKeepRoom?.Invoke() ?? false` → 没人应答 = **不保留** | `KeepRoomDecision.Resolve(bool? dialogResult) => dialogResult ?? true` → 没选 = **保留** | 全仓 `KeepRoomDecision.Resolve(` 文件清单**恰好** `[RoomPage.xaml.cs]`（判据从「≤2」收成「=1」）+ 四条真出口 |
| 两把尺子的一致性 | — | QA 的 G（`ConfirmKeepRoom` 归零）与 H（收尾不弹询问）在 T47 之后必须同时成立 | H 的判据由「有 `ConfirmKeepRoom = () => false`」改为「有 `keepRoom: false`」（QA 侧执行） |

### 构建与实测（**签字口径 · QA 终验**）

- 二进制：`f7690aadd163`（620032 B，主理人 14:00:06 统一构建，**0 警告 / 0 错误**）。
- **五档全 exit 0，QA 签字 NoOne**：

| 档位 | 冻结基线 | 实测 | 差 |
|---|---|---|---|
| 默认 | `1 8 2 0` | **1833** | **+13** |
| `--ui` | `7 6 5` | **825** | **+60** |
| `--embedded` | `1 1 6` | **116** | ±0 |
| `--embeddedqa` | `5 0` | **50** | ±0 |
| `--egg` | 11 项 | **11 项全过** | ±0 |

- **`--ui` 逐批 subtotal（9 批命名 + 「其余」）** —— 这些 `Console.WriteLine` 是本轮新加的，目的是让「条数到底落在哪一批」不再靠猜（它也正是本轮把「720 到底丢在哪」定住的手段）。

| 批次 | 断言数 |
|---|---|
| `AppShellGuard` | 113 |
| `V25BatchProbe` | 32 |
| `AppShellQaGuard`（**由 QA 提供，我没有抄她的口径**） | 108 |
| `PageCatalog` | 233 |
| `S1-QA` | 67 |
| `S2-QA` | 63 |
| `B3-QA` | 21 |
| `S3-QA` | 38 |
| `T43` | 46 |
| 其余（`UiSmokeCheck` 主体：页面实例化 / 菜单 / 登录标签页 / 设置 / 房主设置 / 建房下拉 / 主题 / 剪贴板） | 104 |
| **合计** | **825** |

- **`--ui` 相对基线 +60 的真实来源**（按时间线拆分，**不要照「缺口」去补**，见下面那条更正）：
  - **基线 765 → 720**：S4 收口期间 `AppShellQaGuard` 的视觉树检查排在 `Arrange` 之前 → 10 页假红 → 触发 `if (!loaded) continue;` 每页跳过 7–10 条（≈74）；
  - **720 → 804（+84）**：**全部来自 QA 自己修掉那句 `if (!loaded) continue;`**（顺序改对后 74 条恢复执行、10 条红转绿），叠加 `required` 重构 +7。**这一阶段工程师没有新增任何断言**；
  - **804 → 825（+21）**：QA 的 `ExpectPrimary` 契约声明 +3，工程师的 `CheckKeepRoomExitPaths` **+18**（**18 条行为级替换掉 5 条源码 grep**：那 5 条 grep 只能证明"代码里有这行"，这 18 条证明"这一行真的产生了保留/关闭的结果"，并额外钉死"浮层只加一层、只弹自己那一层"）；
  - 相对基线的其余净增来自：`AppShellGuard` 窗口表 8 项→10 页 +14、`CheckBlockButton` 调用 8→9 次 +3、`V25BatchProbe` 导航居中 8→10 +2、`PageCatalog` 静默洞必修 +7。
- **更正（QA 自曝）**：`--ui` 从 720 恢复到 804 的 **+84，全部来自 QA 修掉 `AppShellQaGuard` 的 `if (!loaded) continue;`**（视觉树检查早于 `Arrange` 的假红 → 10 页每页跳过 7–10 条 ≈74），叠加 `required` 重构 +7；**工程师在此阶段未新增断言**。`804 → 825` 的 +21 才是「QA 的 `ExpectPrimary` +3 + 工程师行为级块 +18」。因此**不存在「窗口级主按钮测量未被页面级接住」的缺口** —— 那条判断（QA 提出、工程师一度采信）已被撤回，别再照它去"补"。
- **默认组 +13**：`ActionComputationTests` 的 `KeepRoomPrompt` 10→12 条（`Resolve` 三态 + 反射钉死 `LeaveRoom*` 只有三条显式出口）+ `PageCatalog` 的两条接线守卫全仓归零（默认组跑）。

### 本轮踩过的三个坑（写给 S5，别再踩）

1. **STA 边界**：`--ui` 才是 STA，默认组是 **MTA**。任何 `UserControl` / `Page` 的构造都不能出现在 `RunLogicGuards()` 里 —— 第一版把 10 条行为级出口断言和 4 条纯函数断言塞进同一个方法，默认组直接抛「调用线程必须为 STA」，整块只剩 1 条 fallback 红。**分法**：按"要不要 WPF 上下文"拆方法，不要按"语义相近"拆。
2. **`ShellWindow.Loaded` 会重置页面**：`OnLoaded()` → `Navigation.Reset(new PageRequest(PageId.Login))`。所以**测试里不能用 `ShellWindow` 当宿主**去承载任意页面（`Show()` 一触发内容就被换回登录页，页面被摘出视觉树 → 菜单 `Popup` 永远拿不到容器，表现为 `opened=3 / realized=0`）。用匿名 `Window` 承载（它不是 Window 子类，不影响「只剩 `ShellWindow`」那条守卫）。
3. **日志行数 ≠ 断言条数**：`CheckBlockButton` 不打印占比行、`MeasurePrimary` 打印 —— 拿"日志里少了几行"推"断言少了多少条"会推错（本轮 QA 与我都在这一点上误判过一次，后来靠 subtotal 才定住）。**要对账就按批打 subtotal**。

### 与用户并行改动的冲突

- 无。删的 20 个文件都是 S1–S3「先加后删」策略下**明确登记要删**的那批；产品侧其余改动只落在注释与新增接口（`IPageTitleSource`），没有改任何既有行为。
- 有一处**跨人交叠**：`docs/v25/SHELL-ARCHITECTURE.md` 的 §18.5 / §19 / §20 / §21 由 QA 写，本批没有改架构文档；`S3QaGuard` / `S1QaGuard` / `AppShellQaGuard` / `EmbeddedServerQaAudit` 四个 QA 文件**一行未动**（改法与依据全部以消息同步给 QA 本人执行，包括那条 `ConfirmKeepRoom` 自相矛盾的翻面方案）。

**本轮未执行任何 `git commit` / `git push`**（顶部警示）。

---

## 本轮闭环记录（S6 · 2026-10-24 · 退出异常修复 + 房间页返回键）

| 步骤 | 内容 |
|---|---|
| ① 读 | 用户报「关闭后程序退出异常」+「添加返回键」；先读 `ShellWindow.xaml.cs` / `App.xaml.cs` / `AppShutdown.cs` / `CloseIntent.cs` / `RoomPage.*` 定位 |
| ② 挑 | 顺手修掉同源的「取消退出后菜单退出标记残留」；`RoomPage` 是十页中唯一没有返回键的页面，补上 |
| ③ 记 | 本轮由主理人直接执行（两位成员因 429 频率限制不在线，额度 2026-10-25 13:31 重置） |
| ④ 销 | 不新增待办编号；修复内容写入 `docs/v25/SHELL-ARCHITECTURE.md` §25 |
| ⑤ 回写 | 新增两条经验，见本节末尾 |

### 修了什么

1. **退出异常根因**：`ShutdownMode.OnExplicitShutdown` 下，「关外壳走完整收尾」分支只放行关闭、**没有请求退出应用** → 进程留在无窗口状态、`OnExit` 不触发、内置服务器成孤儿（菜单退出那条一直对，因为它走 `RequestExit`）。
2. **修法**：`CloseIntent` 新增 `ShellCloseAction` + `ResolveShellClose` / `AfterConfirm` 两个纯函数，把「放行后必须显式退出应用」写成枚举值 `AllowAndExitApp`（可被断言钉死）；`Shell_OnClosing` 改为「问纯函数 → 执行」；确认分支 `Dispatcher.BeginInvoke(RequestExit)`。
3. **同源隐患**：菜单退出被用户取消后 `_menuExitRequested` 残留 true，之后每次 × 都被误判成退出程序 —— 取消时清位。
4. **测试缝**：`AppShutdown.ExitApplication`（默认 true；测试宿主置 false，否则「关外壳」会把整个测试进程带着退）。
5. **返回键**：`RoomPage` 导航栏左槽位补「返回」，与菜单「退出房间」/ 右侧按钮共用 `LeaveRoomFromUiAsync`（三入口一条路径）。

### 验证（临时输出目录 `bin/_s6check/`，构建 0/0）

默认 **1 8 4 5** / `--ui` **9 3 7** / `--embedded` **1 1 6** / `--embeddedqa` **5 0** / `--egg` **11**，五档全绿、全部 exit 0。
标准目录构建被用户运行中的客户端（PID 13596）占用，正式数字待其退出后按标准目录复跑补齐。

**本轮未执行任何 `git commit` / `git push`**（顶部警示；用户运行中的客户端进程未被动过）。

### ⑤ 回写：两条新经验

- **「窗口关了 ≠ 程序退了」**：`ShutdownMode.OnExplicitShutdown` 下，任何「允许窗口关闭」的分支都必须显式请求退出应用，否则进程会留在无窗口状态。已由 `CloseIntent.AfterConfirm(true) == AllowAndExitApp` 断言永久守住。
- **测试宿主关「会退出程序的窗口」时要当心**：修好退出 bug 后，`--ui` 里「实例化并关闭外壳」立刻把整个测试进程带着退，表现是下游断言莫名失败（「应用程序对象正在关闭」）。修这类 bug 的人要主动想「谁会在测试里关这个窗口」。

---

## 本轮闭环记录（S7 · 2026-10-24 · Android 式页面切换动画）

| 步骤 | 内容 |
|---|---|
| ① 读 | 用户要求「返回和页面切换像 Android 一样有动画效果」；读 `ShellWindow.xaml(.cs)` / `NavigationService` / 架构 D6 / 各测试对 `PageHost.Content` 的依赖 |
| ② 挑 | 顺手做掉：动效开关（设置 → 主界面），因为"关不掉的动画"在低端机上会卡 |
| ③ 记 | 本轮由主理人直接执行（成员仍在 429 限额内） |
| ④ 销 | 不新增待办编号；设计写入 `docs/v25/SHELL-ARCHITECTURE.md` §26 |
| ⑤ 回写 | 一条新经验，见本节末尾 |

### 做了什么

1. **显式推翻架构 D6「不做页面转场动画」**：动画只在切页过程中存在 —— 两页装进临时 `Grid`，动画一结束拆掉，`PageHost.Content` 立刻回到「就是那一页」。**既有断言一条未改**（所有断言都只观察静止态）。
2. **动画规格**（`ClientGUI/Services/PageTransition.cs`，纯函数 + 常量）：280ms；前进 = 新页整幅从右滑入 + 旧页 25% 视差淡出；后退 = 旧页整幅滑出右侧 + 新页从左侧 25% 回来；进入 `CubicEase(EaseOut)`、离开 `CubicEase(EaseIn)`。
3. **可关**：`UiStateStore.State.PageTransitionAnimation`（默认 true）+ 设置 → 主界面「页面切换动画」复选。
4. **实现期坑**：旧页没先从 `PageHost` 摘下来就加进转场层 → 「一个元素不能同时是两个父的逻辑子元素」。已修（先 `PageHost.Content = null` 再装），并由既有 `CheckShellTitleFollowsPage` 断言守住。

### 验证（标准目录，构建 0/0）

默认 **1 8 5 4**（+9）/ `--ui` **9 4 0**（+3）/ `--embedded` **1 1 6** / `--embeddedqa` **5 0** / `--egg` **11**，五档全绿、全部 exit 0。
被测 DLL：`16:12:16 / 631296 B / sha256 bbf8638016d3`（源码全部早于 DLL）。

**本轮未执行任何 `git commit` / `git push`**（顶部警示）。

### ⑤ 回写：一条新经验

- **给"会动的界面"加功能时，先分清"瞬态"和"静止态"**：把动画限制在瞬态里（两页临时同居一层、结束即拆），静止态的不变式就能原样保留 —— 于是一条既有断言都不用改。反之若让动画常驻（例如常驻一个转场层），所有结构断言都要重写。

---

## S8（BugFix）收口记录：退房询问的三出口 + 连续导航收尾

**用户报的两件事**
1. 「退出房间」弹出「是否保留房间」后，点任意按键**停在房间页面**（应回房间列表）；用户提示「这是一个特殊情况，也许还会有，一起修了」。
2. 给它加一个「取消」键，按下后**回到房间**（连退房都不做）。

**两个根因（都不是"加个跳转"能盖住的）**

| # | 根因 | 修法 |
|---|---|---|
| 1 | 退房后回列表走 `BackAsync()` → 重入 `RoomPage.OnBackRequestedAsync`，而那里挂着连点去重、连房判断等**瞬时标志**；结论已定却还要再过一道闸门，时序一错这次出栈就被吞掉 | 新增 `NavigationService.PopTo(PageId)`：结论已定时一路弹到目标页，中途无人能说"不"；房间页收敛出唯一出口 `NavigateBackToList()`（优先 `PopTo`，列表不在栈里才退回 `BackAsync`），靠 `PopTo` 幂等做到两条路各走一遍也不多弹 |
| 2 | S7 转场期间 `PageHost.Content` 装的是**临时 Grid**；连续两次导航会把它当"旧页"塞进下一个转场层，界面停在中途页（重则抛"元素已是另一个元素的逻辑子元素"）；上一场的 `Completed` 还会提前收尾后一场 | `OnNavigated` 开头先 `FinishTransition()`（开新场必先结算上一场）；新增转场令牌 `_transitionToken`，过期的 `Completed` 直接丢弃 |

**「取消」为什么不能塞进 `bool?`**：`null` 已被 I6 占用（"没显式选" ⇒ 保留），取消挤进去就会变成"随手关掉询问 = 退房"。
新增 `KeepRoomChoice { Keep, Close, Cancel }`；翻译仍只有 `KeepRoomDecision.ResolveChoice` 一处，
`Resolve(bool?)` 变成它的薄封装 —— 全仓 `KeepRoomDecision.Resolve(` 调用点**仍然恰好 1 处**（`RoomPage.xaml.cs`）。

**验证（标准目录，构建 0/0）**：默认 `1 8 5 4` → **`1 8 7 7`**；`--ui` `9 4 0` → **`9 4 8`**；
`--embedded` 116 / `--embeddedqa` 50 / `--egg` 11/11 不变。**未执行任何 `git commit` / `git push`。**

### 回写：两条新经验

- **"结论已定"的导航不要用可拦截的返回**：`BackAsync` 的语义是"问一下能不能走"，而 `leaveroom` 已经发完了还要再问一次，等于给自己埋一个时序地雷。结论已定时走 `PopTo` 这种直弹通道，把"要不要走"和"往哪走"分开。
- **瞬态容器必须先结算再复用**：转场层是瞬态的，但它在 `PageHost.Content` 上占的是"静止态"的位置。任何一次新导航进入时先把它结算掉（收尾 + 令牌），否则"连续两次操作"这种最常见的用户行为就会踩雷 —— 用户说的"也许还会有"，指的就是这一类。
