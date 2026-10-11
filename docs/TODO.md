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

---

## 已完成

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
