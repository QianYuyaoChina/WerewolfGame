using System.Reflection;
using ClientGUI.ViewModels;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// 校验“服务器状态 → 操作按钮”的翻译逻辑。
/// 直接驱动 <see cref="GameViewModel"/>，不依赖真实服务器。
/// </summary>
public static class ActionComputationTests
{
    private static readonly MethodInfo ApplyStateMethod =
        typeof(GameViewModel).GetMethod("OnStateReceived", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 OnStateReceived");

    /// <summary>模拟收到一次服务器 State 推送。</summary>
    private static GameViewModel Apply(GameStateDto state)
    {
        var vm = new GameViewModel();
        ApplyStateMethod.Invoke(vm, new object[] { state });
        return vm;
    }

    private static GameStateDto State(
        string phase,
        string yourRole,
        int yourId = 0,
        bool isAlive = true,
        int day = 1,
        int playerCount = 4,
        int[]? dead = null,
        string? actingRole = null)
    {
        var players = new List<PlayerDto>();
        for (var i = 0; i < playerCount; i++)
        {
            players.Add(new PlayerDto
            {
                Id = i,
                Name = "P" + i,
                IsAlive = dead == null || !dead.Contains(i),
                IsHost = i == 0,
                IsYou = i == yourId,
                IsConnected = true,
                Role = i == yourId ? yourRole : null
            });
        }

        return new GameStateDto
        {
            Phase = phase,
            Day = day,
            YourId = yourId,
            YourRole = yourRole,
            YourRoleDesc = "desc",
            IsAlive = isAlive,
            ActingRole = actingRole ?? "",
            Players = players
        };
    }

    public static async Task RunAllAsync()
    {
        WaitingPhase();
        NightPhase();
        NightTurnGating();
        VotePhase();
        HunterPhase();
        PassivePhases();
        StateConsistency();
        RulesDrivenUi();
        await ConnectionFailureAsync();
    }

    // ==================== 规则驱动的界面（可配置化回归） ====================

    /// <summary>
    /// 倒计时与“最少人数”提示必须跟随服务端下发的规则；缺省时回退默认值（兼容旧服务端）。
    /// </summary>
    private static void RulesDrivenUi()
    {
        // 倒计时秒数来自服务端规则。
        var vote = State(Phases.Vote, Roles.Villager, yourId: 0);
        vote.Rules = new GameRules { VoteSeconds = 7 };
        var voteVm = Apply(vote);
        Check.True(voteVm.CountdownText.Contains("7"), "规则：倒计时采用服务端下发的投票秒数");

        // 人数不足时房主不能开始，提示里的最少人数也来自规则。
        var short3 = State(Phases.Waiting, Roles.Villager, yourId: 0, playerCount: 3);
        short3.Rules = new GameRules { MinPlayers = 6 };
        var shortVm = Apply(short3);
        Check.False(shortVm.CanStartGame, "规则：人数不足时房主不能开始");
        Check.True(shortVm.Actions[0].Hint?.Contains("6") == true, "规则：开始提示采用服务端下发的最少人数");
        Check.True(shortVm.StartHint.Contains("6"), "规则：等待提示采用服务端下发的最少人数");

        // 人数达标后可开始。
        var enough = State(Phases.Waiting, Roles.Villager, yourId: 0, playerCount: 6);
        enough.Rules = new GameRules { MinPlayers = 6 };
        Check.True(Apply(enough).CanStartGame, "规则：人数达标后房主可开始");

        // 未下发规则时回退默认（4 人）。
        var legacy = State(Phases.Waiting, Roles.Villager, yourId: 0, playerCount: 3);
        Check.False(Apply(legacy).CanStartGame, "规则：缺省时按默认最少 4 人判定");
        Check.True(Apply(legacy).Actions[0].Hint?.Contains("4") == true, "规则：缺省时提示默认最少人数");
    }

    // ==================== 连接失败 ====================

    /// <summary>
    /// 回归：连接失败必须只反映在状态文案里，不能抛异常、不能进入房间。
    /// 早期版本会在失败路径上抛异常（Foreground = UnsetValue），
    /// 被全局兜底吞掉后表现为“点连接就报一堆错”。
    /// </summary>
    private static async Task ConnectionFailureAsync()
    {
        // 连接/登录已从 GameViewModel 移到 LoginViewModel，这里跟着改测登录视图模型。
        var vm = new LoginViewModel
        {
            ServerUrl = "http://127.0.0.1:5999/gamehub",   // 该端口不会有人监听
            UsernameInput = "regression",
            PasswordInput = "regression123"
        };

        // AsyncRelayCommand.Execute 是 async void：若内部抛异常会直接终止进程，
        // 因此“进程没死 + 状态文案正确”本身就是断言。
        _ = Task.Run(() =>
        {
            if (vm.LoginCommand.CanExecute(null)) vm.LoginCommand.Execute(null);
        });

        await WaitUntil(() => vm.Status.StartsWith("无法连接", StringComparison.Ordinal), 10_000);

        Check.False(vm.IsLoggedIn, "连接失败：不应进入房间");
        Check.True(vm.Status.StartsWith("无法连接", StringComparison.Ordinal),
            "连接失败：状态栏应给出无法连接提示（实际：" + vm.Status + "）");
        Check.False(vm.Session.IsJoined, "连接失败：会话未加入房间");
        Check.False(vm.Session.IsConnected, "连接失败：会话未保持连接");
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(250);
        }
    }

    // ==================== 等待阶段 ====================

    private static void WaitingPhase()
    {
        var host = Apply(State(Phases.Waiting, Roles.Villager, yourId: 0));
        Check.Equal(1, host.Actions.Count, "等待阶段：房主只有 1 个按钮");
        Check.Equal("开始游戏", host.Actions[0].Label, "等待阶段：按钮文案");
        Check.True(host.Actions[0].IsEnabled, "等待阶段：房主可开始");
        Check.True(host.CanStartGame, "等待阶段：CanStartGame");
        Check.False(host.ShowStartHint, "等待阶段：房主不显示等待提示");

        var guest = Apply(State(Phases.Waiting, Roles.Villager, yourId: 2));
        Check.False(guest.Actions[0].IsEnabled, "等待阶段：非房主不可开始");
        Check.False(guest.CanStartGame, "等待阶段：非房主 CanStartGame");
        Check.True(guest.ShowStartHint, "等待阶段：非房主显示等待提示");
    }

    // ==================== 夜晚 ====================

    private static void NightPhase()
    {
        foreach (var role in new[] { Roles.Werewolf, Roles.Seer, Roles.Guard })
        {
            var vm = Apply(State(Phases.Night, role, yourId: 1, playerCount: 5, actingRole: role));
            Check.SequenceEqual(
                new[] { 0, 2, 3, 4 },
                vm.Players.Where(p => p.Selectable).Select(p => p.Id).OrderBy(i => i),
                $"夜晚 {role}：可选目标为其他存活玩家");
        }

        var villager = Apply(State(Phases.Night, Roles.Villager, actingRole: Roles.Guard));
        Check.Equal(0, villager.Actions.Count, "夜晚 平民：没有操作按钮");
        Check.True(villager.ActionHint.Contains("等待") || villager.ActionHint.Contains("平民"), "夜晚 平民：提示身份或等待");
        Check.False(villager.Players.Any(p => p.Selectable), "夜晚 平民：没有可选目标");

        var witch = Apply(State(Phases.Night, Roles.Witch, yourId: 1, playerCount: 5, actingRole: Roles.Witch));
        Check.Equal(3, witch.Actions.Count, "夜晚 女巫：3 个按钮");
        Check.True(witch.Actions.Any(a => a.Label.Contains("解药") && a.IsEnabled), "夜晚 女巫：解药可用");
        Check.True(witch.Actions.Any(a => a.Label.Contains("毒药") && a.IsEnabled), "夜晚 女巫：毒药可用");
        Check.True(witch.Actions.Any(a => a.Label.Contains("不用药")), "夜晚 女巫：可不使用药水");
        Check.SequenceEqual(
            new[] { 0, 2, 3, 4 },
            witch.Players.Where(p => p.Selectable).Select(p => p.Id).OrderBy(i => i),
            "夜晚 女巫：毒药目标为其他存活玩家");

        // 选中目标后应更新提示并允许提交
        var wolf = Apply(State(Phases.Night, Roles.Werewolf, yourId: 0, playerCount: 4, actingRole: Roles.Werewolf));
        Check.Equal("确认击杀", wolf.Actions.Single().Label, "夜晚 狼人：提交按钮文案");
        Check.False(wolf.Actions.Single().IsEnabled, "夜晚 狼人：未选目标时不可提交");

        wolf.SelectPlayer(wolf.Players.Single(p => p.Id == 2));
        Check.True(wolf.ActionHint.Contains("[2]"), "夜晚 狼人：提示包含已选目标");
        Check.True(wolf.Actions.Single().IsEnabled, "夜晚 狼人：选目标后可提交");
        Check.True(wolf.Players.Single(p => p.Id == 2).IsTarget, "夜晚 狼人：目标行被标记");

        // 再次点击同一目标应取消选择
        wolf.SelectPlayer(wolf.Players.Single(p => p.Id == 2));
        Check.False(wolf.Actions.Single().IsEnabled, "夜晚 狼人：再次点击取消选择");

        // 已出局的玩家夜晚没有任何操作
        var dead = Apply(State(Phases.Night, Roles.Seer, yourId: 0, isAlive: false, dead: new[] { 0 }, actingRole: Roles.Seer));
        Check.Equal(0, dead.Actions.Count, "夜晚 出局玩家：没有操作");
    }

    /// <summary>“未轮到放技能时不能放技能”的回归用例。</summary>
    private static void NightTurnGating()
    {
        // 狼人玩家，但当前轮到守卫行动 → 不应出现击杀按钮、不可选目标。
        var wolf = Apply(State(Phases.Night, Roles.Werewolf, yourId: 0, playerCount: 5, actingRole: Roles.Guard));
        Check.Equal(0, wolf.Actions.Count, "未轮到：狼人看不到击杀按钮");
        Check.False(wolf.Players.Any(p => p.Selectable), "未轮到：狼人无可选目标");
        Check.True(wolf.ActionHint.Contains("守卫"), "未轮到：提示当前轮到守卫");

        // 轮到自己（狼人）时才出现击杀按钮。
        var wolfTurn = Apply(State(Phases.Night, Roles.Werewolf, yourId: 0, playerCount: 5, actingRole: Roles.Werewolf));
        Check.Equal(1, wolfTurn.Actions.Count, "轮到：狼人出现击杀按钮");
        Check.True(wolfTurn.Players.Any(p => p.Selectable), "轮到：狼人可选目标");

        // 女巫轮到时只有毒药需要选目标；未轮到守卫阶段则无操作。
        var witchIdle = Apply(State(Phases.Night, Roles.Witch, yourId: 1, playerCount: 5, actingRole: Roles.Werewolf));
        Check.Equal(0, witchIdle.Actions.Count, "未轮到：女巫看不到按钮");
    }

    // ==================== 投票 ====================

    private static void VotePhase()
    {
        var vm = Apply(State(Phases.Vote, Roles.Villager, yourId: 0));
        Check.Equal(2, vm.Actions.Count, "投票阶段：2 个按钮");
        Check.Equal("确认投票", vm.Actions[0].Label, "投票阶段：第一个按钮为确认投票");
        Check.False(vm.Actions[0].IsEnabled, "投票阶段：未选目标不可投票");
        Check.Equal("弃票", vm.Actions[1].Label, "投票阶段：提供弃票");
        Check.True(vm.Actions[1].IsEnabled, "投票阶段：弃票始终可用");

        vm.SelectPlayer(vm.Players.Single(p => p.Id == 3));
        Check.True(vm.Actions[0].IsEnabled, "投票阶段：选目标后可投票");
        Check.True(vm.ActionHint.Contains("[3]"), "投票阶段：提示包含目标");

        var dead = Apply(State(Phases.Vote, Roles.Villager, yourId: 0, isAlive: false, dead: new[] { 0 }));
        Check.Equal(0, dead.Actions.Count, "投票阶段：出局玩家无法投票");
        Check.True(dead.ActionHint.Contains("出局"), "投票阶段：出局提示");
        Check.False(dead.Players.Any(p => p.Selectable), "投票阶段：出局玩家没有可选目标");
        Check.False(dead.CanChat, "投票阶段：出局玩家不能发言");
    }

    // ==================== 猎人开枪 ====================

    private static void HunterPhase()
    {
        var hunter = Apply(State(Phases.HunterShoot, Roles.Hunter, yourId: 0, isAlive: false, dead: new[] { 0 }));
        Check.Equal(2, hunter.Actions.Count, "猎人阶段：2 个按钮");
        Check.True(hunter.Actions.Any(a => a.Label.Contains("开枪")), "猎人阶段：提供开枪");
        Check.True(hunter.Actions.Any(a => a.Label.Contains("放弃")), "猎人阶段：提供放弃");
        Check.True(hunter.Players.Any(p => p.Selectable), "猎人阶段：出局的猎人仍可选目标");

        var other = Apply(State(Phases.HunterShoot, Roles.Villager, yourId: 0, isAlive: false, dead: new[] { 0 }));
        Check.Equal(0, other.Actions.Count, "猎人阶段：非猎人无操作");
        Check.True(other.ActionHint.Contains("等待猎人"), "猎人阶段：非猎人看到等待提示");
    }

    // ==================== 被动阶段 ====================

    private static void PassivePhases()
    {
        foreach (var phase in new[] { Phases.Day, Phases.Discussion, Phases.GameOver })
        {
            var vm = Apply(State(phase, Roles.Villager));
            Check.Equal(0, vm.Actions.Count, $"{phase}：没有操作按钮");
            Check.True(vm.HasActionHint, $"{phase}：有阶段提示文字");
        }
    }

    // ==================== 状态一致性 ====================

    private static void StateConsistency()
    {
        var vm = Apply(State(Phases.Discussion, Roles.Seer, yourId: 2, day: 3, playerCount: 6));
        Check.Equal(6, vm.Players.Count, "状态：玩家数量");
        Check.Equal(3, vm.Day, "状态：天数");
        Check.Equal("第 3 天", vm.DayText, "状态：天数文案");
        Check.Equal("💬 讨论", vm.PhaseDisplay, "状态：阶段文案");
        Check.Equal("🔮 预言家", vm.YourRoleDisplay, "状态：身份文案");
        Check.Equal(2, vm.YourId, "状态：自己的编号");
        Check.True(vm.IsAlive, "状态：存活标记");
        Check.False(vm.IsHost, "状态：房主标记");

        // 相同状态应被去重，列表实例保持不变
        var state = State(Phases.Night, Roles.Seer, yourId: 0);
        var vm2 = Apply(state);
        var firstRow = vm2.Players[0];
        ApplyStateMethod.Invoke(vm2, new object[] { state });
        Check.True(ReferenceEquals(firstRow, vm2.Players[0]), "状态：重复推送不重建列表");

        // 进入新的一天应清空上一轮的选择
        var vm3 = Apply(State(Phases.Vote, Roles.Villager, yourId: 0, day: 1));
        vm3.SelectPlayer(vm3.Players.Single(p => p.Id == 1));
        Check.True(vm3.Players.Any(p => p.IsTarget), "状态：第 1 天已选中目标");

        ApplyStateMethod.Invoke(vm3, new object[] { State(Phases.Vote, Roles.Villager, yourId: 0, day: 2) });
        Check.False(vm3.Players.Any(p => p.IsTarget), "状态：新回合清空选中目标");
        Check.False(vm3.Actions.First(a => a.Label == "确认投票").IsEnabled, "状态：新回合重置提交按钮");

        // 倒计时只在有时限的阶段出现
        var timed = Apply(State(Phases.Vote, Roles.Villager, yourId: 0));
        Check.True(timed.HasCountdown, "倒计时：投票阶段有倒计时");
        Check.True(timed.CountdownText.Contains("45"), "倒计时：投票阶段为 45 秒");
        Check.False(Apply(State(Phases.GameOver, Roles.Villager, yourId: 0)).HasCountdown, "倒计时：结束阶段无倒计时");

        // 结束阶段服务器会公开所有身份
        var reveal = State(Phases.GameOver, Roles.Villager, yourId: 0, isAlive: false);
        reveal.Players[1].Role = Roles.Werewolf;
        var vm4 = Apply(reveal);
        var wolfRow = vm4.Players.Single(p => p.Id == 1);
        Check.True(wolfRow.HasRole, "身份公开：狼人身份可见");
        Check.True(wolfRow.RoleText.Contains("狼人"), "身份公开：身份文案正确");
    }
}
