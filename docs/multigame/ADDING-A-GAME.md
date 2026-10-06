# 如何新增一个游戏（ADDING-A-GAME）

> 配套文档：`docs/multigame/ARCHITECTURE.md`（先读它，理解 `IGameModule` / `IGameInstance` / `IGameContext` / `IGameViewModel`）。
> 本文面向「要给平台加一个新游戏」的开发者。**目标：只新增文件 + 在 2 个注册点各加一条，不改任何既有平台模块。**

---

## 0. 一页速览：新增一个游戏要做什么

| 步骤 | 加什么 | 放哪里 | 改既有文件？ |
|---|---|---|---|
| ① 元数据 | 一个 `IGameModule`（声明 `GameInfo`） | `WerewolfGame/Games/<Game>/<Game>GameModule.cs` | 否（反射自动收录） |
| ② 游戏逻辑 | 一个 `IGameInstance` | `WerewolfGame/Games/<Game>/<Game>GameInstance.cs` | 否 |
| ③ 操作协议 | `<Game>Ops` + payload DTO | `WerewolfGame/Games/<Game>/` + `Werewolf.Client.Share/Games/` | 否 |
| ④ 面板 | GUI 面板 + 控制台模块 | `ClientGUI/Views/Games/` + `Werewolf.Client/Games/` | **注册点**：`GameViewModelFactory` 一条 case、`Themes/GamePanels.xaml` 一条模板、`ConsoleGameRegistry` 一条 case |
| ⑤（可选）规则说明 | `GameInfo.RulesText` | 同 ① | 否 |
| ⑥（可选）房间标识 | `GameInfo.AccentHex` / `HasPlayerConfig` | 同 ① | 否 |
| ⑦（可选）接管发言 | `IGameInstance.OnPlayerChat` | 同 ② | 否 |

> 「注册点」= 工厂 switch 与 DataTemplate 表。它们是**插件的登记处**，不是平台逻辑；`RoomManager`/账号/邀请码/重连/主题/大厅宿主逻辑**一行都不用改**。

---

## 1. 不应改动的文件（红线）

新增游戏**禁止**修改下列文件（评审据此判定「抽象是否抽干净」）：

- `WerewolfGame/RoomManager.cs`（房间注册表）
- `WerewolfGame/GameRoom.cs`（房间宿主）
- `WerewolfGame/GameHub.cs`（仅默认分支路由游戏 op，不认具体游戏）
- `WerewolfGame/AccountStore.cs`、`Werewolf.Client.Share/InviteCode.cs`（账号 / 邀请码）
- `Werewolf.Client.Share/GameClient.cs` 的握手/重连链路（可加通用方法，但不为某个游戏改链路）
- `ClientGUI/Services/ThemeManager.cs`、`UiStateStore.cs`（主题 / 设置持久化）
- `ClientGUI/ViewModels/RoomListViewModel.cs`（大厅/房间列表宿主逻辑）
- `ClientGUI/MainWindow.xaml(.cs)` 的宿主骨架（仅插槽与胶囊绑定，与具体游戏无关）
- `WerewolfGame/Games/Werewolf/**`（其他游戏的文件）

允许触碰的「注册点」只有 3 处（都是登记性质）：
1. `WerewolfGame/Program.cs` —— **通常也不用改**（反射发现）；仅当选择「手写清单」方案时才在此加一行。
2. `ClientGUI/ViewModels/GameViewModelFactory.cs` —— 加一条 `case`。
3. `ClientGUI/Themes/GamePanels.xaml` —— 加一条 `<DataTemplate>`。
4. `Werewolf.Client/ConsoleGameRegistry.cs` —— 加一条 `case`。

---

## 2. 可复制粘贴的最小骨架

下面以假想的 `undercover`（谁是卧底）为例。把 `<Game>` 全部替换为你的机器名。

### 2.1 共享层：元数据已由模块提供，这里只定义 payload 与 op

`Werewolf.Client.Share/Games/UndercoverOps.cs`
```csharp
namespace Werewolf.Client.Share;

/// <summary>谁是卧底的操作名（框架只透传，不理解语义）。</summary>
public static class UndercoverOps
{
    public const string Describe = "uc_describe"; // payload: { Text:string }
    public const string Vote     = "uc_vote";     // payload: { TargetId:int }
}
```

`Werewolf.Client.Share/Games/UndercoverGameState.cs`
```csharp
namespace Werewolf.Client.Share;

/// <summary>谁是卧底下发给「某玩家视角」的私有负载。
/// 注意：词条是私有信息，只能进入对应玩家的 payload（视角隔离）。</summary>
public sealed class UndercoverGameState
{
    public string Phase { get; set; } = "";        // 如 "Describe" / "Vote"
    public string MyWord { get; set; } = "";       // 仅本人可见的词条
    public int Round { get; set; }
    public bool AlreadyActed { get; set; }         // 本环节我是否已描述/已投票
}
```

> 🚫 **禁止**把 `MyWord` 放进 `PlayerDto` 或广播消息里——那会泄露给所有玩家。只放 `GameStateDto.GamePayload`，且只对该玩家生成。

### 2.2 服务端：游戏模块（接入点 ①② ）

`WerewolfGame/Games/Undercover/UndercoverGameModule.cs`
```csharp
using Werewolf.Client.Share;
using Werewolf.Server.Games;

namespace Werewolf.Server.Games.Undercover;

/// <summary>谁是卧底的注册单元。反射发现要求公共无参构造。</summary>
public sealed class UndercoverGameModule : IGameModule
{
    public GameInfo Info { get; } = new()
    {
        GameId = GameIds.Undercover,
        DisplayName = "谁是卧底",
        Emoji = "🕵️",
        Tagline = "每人一个词，描述+投票找出卧底",
        MinPlayers = 3,
        MaxPlayers = 12,
        RulesText = "多数玩家拿到相同词，卧底拿到相近词；轮流描述后投票放逐。"
    };

    public IGameInstance CreateInstance(IGameContext context)
        => new UndercoverGameInstance(Info, context);
}
```

`WerewolfGame/Games/Undercover/UndercoverGameInstance.cs`
```csharp
using System.Text.Json.Nodes;
using Werewolf.Client.Share;
using Werewolf.Server.Games;

namespace Werewolf.Server.Games.Undercover;

/// <summary>谁是卧底玩法。无夜晚；描述 → 投票 两个环节，可自行驱动主循环或纯事件驱动。</summary>
public sealed class UndercoverGameInstance : IGameInstance
{
    private readonly object _sync = new();
    private readonly IGameContext _ctx;
    private readonly Random _rng = new();
    private readonly Dictionary<string, string> _words = new();   // username → 词条
    private readonly Dictionary<string, bool> _acted = new();      // username → 本环节是否已动作
    private GameStatus _status = GameStatus.Waiting;
    private int _round;
    private bool _voting;   // false=描述环节，true=投票环节

    public UndercoverGameInstance(GameInfo info, IGameContext context)
    { Info = info; _ctx = context; }

    public GameInfo Info { get; }
    public string GameId => Info.GameId;
    public GameStatus Status { get { lock (_sync) return _status; } }

    // —— 开局 ——
    public (bool ok, string msg) Start()
    {
        lock (_sync)
        {
            var players = _ctx.Players;
            if (players.Count < Info.MinPlayers)
                return (false, $"至少需要 {Info.MinPlayers} 名玩家");

            // 发词：多数同词，1 个卧底拿相近词。
            _words.Clear(); _acted.Clear();
            _round = 1; _voting = false;
            var ci = _rng.Next(players.Count);                       // 卧底下标
            for (var i = 0; i < players.Count; i++)
                _words[players[i].Username] = i == ci ? "柠檬" : "橘子";
            foreach (var p in players)
            { p.IsAlive = true; _acted[p.Username] = false; }

            _status = GameStatus.Running;
            return (true, "");
        }
    }

    public async Task RunAsync()
    {
        await _ctx.BroadcastAsync("🕵️ 谁是卧底开始！请依次描述你的词。");
        await _ctx.BroadcastStateAsync();
        // 本例用纯事件驱动（不做定时阶段）；需要计时可在此用 Task.Delay+BroadcastStateAsync。
    }

    // —— 接收本游戏操作 ——
    public (bool ok, string msg) HandleAction(string op, JsonObject? payload, string connectionId)
    {
        var player = _ctx.FindByConnection(connectionId);
        if (player == null) return (false, "你不在房间中");

        lock (_sync)
        {
            if (_status != GameStatus.Running) return (false, "游戏未开始");

            switch (op)
            {
                case UndercoverOps.Describe:
                    if (_voting) return (false, "当前是投票环节");
                    _acted[player.Username] = true;
                    return (true, "");

                case UndercoverOps.Vote:
                    if (!_voting) return (false, "当前是描述环节");
                    _acted[player.Username] = true;
                    // …计票、淘汰、判胜负（略）
                    return (true, "");

                default:
                    return (false, "未知操作");
            }
        }
    }

    // —— 某玩家视角的私有负载（视角隔离，词条仅本人在此可见）——
    public JsonNode? BuildPayload(string viewerConnectionId)
    {
        var me = _ctx.FindByConnection(viewerConnectionId);
        if (me == null) return null;

        lock (_sync)
        {
            var payload = new UndercoverGameState
            {
                Phase = _voting ? "Vote" : "Describe",
                MyWord = _words.TryGetValue(me.Username, out var w) ? w : "",
                Round = _round,
                AlreadyActed = _acted.TryGetValue(me.Username, out var a) && a
            };
            return JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(payload));
        }
    }

    // 可选：给玩家行打中立标签（务必勿泄露词条）
    public void DecoratePlayers(IReadOnlyList<PlayerDto> rows, string viewerUsername)
    {
        // 例如给「已描述」的人加个标记；不要放词条。
    }

    public void Reset() { lock (_sync) { _status = GameStatus.Waiting; _words.Clear(); _acted.Clear(); } }
}
```

> 刷新逻辑：游戏在 `RunAsync` / 收到操作后调用 `await _ctx.BroadcastStateAsync()`，平台会为**每个玩家**独立生成 DTO 并回调 `BuildPayload(viewer)`。

### 2.3 GUI：面板（接入点 ④ ）

`ClientGUI/Views/Games/UndercoverPanel.xaml`
```xml
<UserControl x:Class="ClientGUI.Views.Games.UndercoverPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:ClientGUI.ViewModels.Games.Undercover">
    <Border Background="{DynamicResource PanelBrush}" CornerRadius="10"
            BorderBrush="{DynamicResource BorderBrush2}" BorderThickness="1" Padding="14">
        <StackPanel>
            <TextBlock Text="我的词条" Style="{StaticResource SectionTitle}" />
            <TextBlock Text="{Binding MyWord}" FontSize="16" FontWeight="Bold"
                       Foreground="{DynamicResource AccentBrush}" />
            <TextBlock Text="{Binding Hint}" TextWrapping="Wrap" Margin="0,10,0,0"
                       Foreground="{DynamicResource MutedBrush}" />
            <ItemsControl ItemsSource="{Binding Actions}"
                          ItemTemplate="{StaticResource ActionButtonTemplate}" Margin="0,10,0,0" />
        </StackPanel>
    </Border>
</UserControl>
```

`ClientGUI/ViewModels/Games/Undercover/UndercoverViewModel.cs`
```csharp
using System.Collections.ObjectModel;
using ClientGUI.ViewModels;   // GameAction / IGameViewModel / IGameHostBridge / StatusChip / PlayerRow

namespace ClientGUI.ViewModels.Games.Undercover;

public sealed class UndercoverViewModel : IGameViewModel
{
    private readonly GameClient _client;
    private readonly IGameHostBridge _bridge;
    private Werewolf.Client.Share.UndercoverGameState? _game;

    public UndercoverViewModel(GameClient client, IGameHostBridge bridge)
    { _client = client; _bridge = bridge; }

    public string GameId => Werewolf.Client.Share.GameIds.Undercover;
    public ObservableCollection<GameAction> Actions { get; } = new();
    public IReadOnlyList<StatusChip> Chips { get; } = new List<StatusChip>();
    public string Hint { get; private set; } = "";
    public string MyWord => _game?.MyWord ?? "";

    public bool CanSelectPlayer(PlayerRow row) => true;
    public void OnPlayerSelected(PlayerRow row) { /* 记录目标并 Refresh */ }
    public void OnState(Werewolf.Client.Share.GameStateDto state)
    {
        _game = state.GamePayload?.Deserialize<Werewolf.Client.Share.UndercoverGameState>();
        Hint = _game?.Phase == "Vote" ? "请投票放逐你怀疑的卧底。" : "请描述你的词。";
        // Actions：根据 _game.Phase 组装按钮，命令内调用
        //   _client.CallGameOpAsync(Werewolf.Client.Share.UndercoverOps.Describe, Node(new { Text }))
        //   _client.CallGameOpAsync(Werewolf.Client.Share.UndercoverOps.Vote, Node(new { TargetId }))
    }
    public void Refresh() { /* 用缓存的 _game 重算 Actions/Hint */ }
    public void Reset() { Actions.Clear(); Hint = ""; _game = null; }
}
```

`ClientGUI/ViewModels/GameViewModelFactory.cs`（**注册点，加一条 case**）
```csharp
internal static class GameViewModelFactory
{
    public static IGameViewModel Create(string gameId, GameClient client, IGameHostBridge bridge) => gameId switch
    {
        GameIds.Werewolf => new Games.Werewolf.WerewolfViewModel(client, bridge),
        GameIds.Undercover => new Games.Undercover.UndercoverViewModel(client, bridge),   // ← 新增
        _ => new UnknownGameViewModel(gameId)   // 兜底
    };
}
```

`ClientGUI/Themes/GamePanels.xaml`（**注册点，加一条 DataTemplate**）
```xml
<ResourceDictionary xmlns="..." xmlns:x="..."
                    xmlns:uc="clr-namespace:ClientGUI.ViewModels.Games.Undercover"
                    xmlns:ucv="clr-namespace:ClientGUI.Views.Games">
    <!-- 既有：狼人杀 -->
    <DataTemplate DataType="{x:Type vm:WerewolfViewModel}">
        <views:WerewolfPanel />
    </DataTemplate>
    <!-- 新增：谁是卧底 -->
    <DataTemplate DataType="{x:Type uc:UndercoverViewModel}">
        <ucv:UndercoverPanel />
    </DataTemplate>
</ResourceDictionary>
```

### 2.4 控制台：指令模块（接入点 ④ 控制台侧）

`Werewolf.Client/Games/UndercoverConsoleModule.cs`
```csharp
using Werewolf.Client.Share;

namespace Werewolf.Client;

public sealed class UndercoverConsoleModule : IConsoleGameModule
{
    public string GameId => GameIds.Undercover;
    public string HelpText => "  desc 文字          描述你的词\n  vote 3             投票给 3 号";
    public string RoomHeader(GameStateDto s) => "🕵️ 谁是卧底";

    public string RenderState(GameStateDto s)
    {
        var g = s.GamePayload?.Deserialize<UndercoverGameState>();
        return g == null ? "" : $"│ 我的词：{g.MyWord}　环节：{g.Phase}";
    }

    public async Task<bool> TryHandleAsync(string line, GameStateDto? state, GameClient client)
    {
        if (line.StartsWith("desc ", StringComparison.OrdinalIgnoreCase))
        {
            await client.CallGameOpAsync(UndercoverOps.Describe,
                System.Text.Json.Nodes.JsonNode.Parse($"{{\"Text\":\"{line[5..].Trim()}\"}}"));
            return true;
        }
        if (line.StartsWith("vote ", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(line[5..].Trim(), out var id))
        {
            await client.CallGameOpAsync(UndercoverOps.Vote,
                System.Text.Json.Nodes.JsonNode.Parse($"{{\"TargetId\":{id}}}"));
            return true;
        }
        return false;   // 交回通用指令
    }
}
```

`Werewolf.Client/ConsoleGameRegistry.cs`（**注册点，加一条 case**）
```csharp
internal static class ConsoleGameRegistry
{
    private static readonly Dictionary<string, IConsoleGameModule> Modules = new()
    {
        [GameIds.Werewolf]   = new WerewolfConsoleModule(),
        [GameIds.Undercover] = new UndercoverConsoleModule(),   // ← 新增
    };

    public static IConsoleGameModule Resolve(string gameId)
        => Modules.TryGetValue(gameId ?? "", out var m) ? m : new UnknownConsoleModule(gameId);
}
```

---

## 3. 接入点清单（评审逐条核对）

新增游戏完成下列 4（+1 可选）项即可：

1. **登记游戏元数据** —— 新增 `<Game>GameModule.cs`（`IGameModule`，公共无参构造）。反射发现自动收录，**不改 `Program.cs`**。
2. **实现游戏逻辑模块** —— 新增 `<Game>GameInstance.cs`（`IGameInstance`）。
3. **声明本游戏操作协议** —— 新增 `<Game>Ops.cs` + payload DTO（共享库）。
4. **提供本游戏操作面板** —— GUI：`<Game>Panel.xaml` + `GamePanels.xaml` 一条模板 + `GameViewModelFactory` 一条 case；控制台：`<Game>ConsoleModule.cs` + `ConsoleGameRegistry` 一条 case。
5. **（可选）规则展示** —— `GameInfo.RulesText`。

**核对「平台模块零改动」**：`RoomManager` / `AccountStore` / `InviteCode` / 断线重连 / `ThemeManager`+`UiStateStore` / `RoomListViewModel` 宿主逻辑——**均不触碰**。

---

## 4. 「谁是卧底」（已落地，v2.3 起）

- **需要的扩展点**：① 向每位玩家**私发不同私有信息**（词条，而非「身份名」）→ 由 `IGameInstance.BuildPayload(viewerConnectionId)` 的**视角隔离**天然支持（每个玩家拿到自己的 DTO）；② 描述环节 + 投票环节 → 游戏自定 `Phase`（私有，框架无感）；③ 无夜晚角色动作 → `RunAsync` 只驱动自己的阶段循环。
- **结论**：完全由接入点 1–4 覆盖，**没有新增框架能力**。
- **注意**：词条**只能**进 `GamePayload`（本人视角）；`PlayerDto.Tag` 对卧底应留空或用中立标记（如「已描述」），**严禁**把词条放进 `Tag`/广播消息。
- **v2.3.1 的两个可复用钩子**（其他游戏按需取用）：
  - `GameInfo.HasPlayerConfig = false` → 房主设置**隐藏**「身份配置」页（该页是狼人杀专属），保存时也不会写入狼人杀的身份布局。
  - `IGameInstance.OnPlayerChat(player, text)` → 平台在**广播聊天之前**先问一次，返回 `(false, 原因)` 即可拒绝插话。谁是卧底用它实现「按顺序轮流发言」：轮到谁谁才能发，发言即视为本轮完成；GUI 侧由 `IGameViewModel.CanSendChat / SendChatHint` 同步禁用「发送」按钮。
- 校验：`ClientGUI.Tests --undercover <url>`（需小局配置：`Undercover:DescribeSeconds/VoteSeconds` 调小）。

---

## 5. 「海龟汤」预留的口子（非回合制）

- 框架**不假设**必须有阶段/回合：`Phase`/`Day` 已从通用协议移除，属游戏私有。
- 开局后框架仅：`Start()`（可立即 `Running`）、`RunAsync()`（可为空实现，纯等事件）、透传 op + 广播。
- 无「阶段超时」框架概念；需计时自行 `Task.Delay` + `BroadcastStateAsync`。
- 「存活/淘汰」是**可选**通用语义：海龟汤若无人淘汰，保持 `RoomPlayer.IsAlive = true` 即可。
- 后续接入**复用接入点 1–4**，无需新增框架能力。

---

## 6. 提交前自检清单

- [ ] `GameInfo.GameId` 用 `snake_case` 且已登记到 `GameIds`。
- [ ] 私有信息只出现在 `BuildPayload` 里（视角隔离），**没有**进 `PlayerDto` / 广播。
- [ ] 操作 op 前缀带游戏名（如 `uc_`），避免与其他游戏冲突。
- [ ] 未修改 §1 红名单里的任何平台文件。
- [ ] `GameInfo.AccentHex` 给了本游戏的主题色（房间列表 / 标题栏 / 房主设置据此区分房间）。
- [ ] 无「身份 / 玩法配置」的玩法显式设了 `HasPlayerConfig = false`。
- [ ] 若限制发言顺序：服务端覆写 `OnPlayerChat`，GUI 侧同步实现 `CanSendChat` / `SendChatHint`。
- [ ] GUI：面板用 `{DynamicResource ...}` 引用既有配色键；如需新键，**两套配色都加**（否则 `--ui` 失败）。
- [ ] 控制台：`help` 合并了本游戏指令，狼人杀既有指令不受影响。
- [ ] 跑一遍 `ClientGUI.Tests` 默认模式 + `--ui`，均通过。
