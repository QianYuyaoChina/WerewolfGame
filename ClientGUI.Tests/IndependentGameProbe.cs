using System.Reflection;
using System.Text.Json.Nodes;
using ClientGUI.ViewModels;
using ClientGUI.ViewModels.Games;
using Werewolf.Client.Share;
using Werewolf.Server;
using Werewolf.Server.Games;

namespace ClientGUI.Tests;

/// <summary>
/// QA（严过关）**独立编写**的「第三游戏」可插拔验证（默认模式运行）。
/// 刻意不复用工程师的 <see cref="FakeGameProbe"/>，且刻意与狼人杀玩法**明显不同**：
/// 人数上限=2、无阶段、无回合、只有一种自定义操作 <c>poke</c>、自定义 payload 字段、自定义玩家标签。
/// 本文件不引用任何狼人杀玩法类型（Werewolf.* / Role / Phases），以自身编译通过作为「可插拔」的证据之一。
/// </summary>
public static class IndependentGameProbe
{
    private const string GameId = "poke_battle";

    /// <summary>与狼人杀无关的第三游戏注册单元（顶部专用模块；反射可发现）。</summary>
    internal sealed class PokeModule : IGameModule
    {
        public GameInfo Info { get; } = new()
        {
            GameId = GameId,
            DisplayName = "戳一戳",
            Emoji = "👆",
            Tagline = "QA 独立验证用：无阶段、无回合，只有一次戳",
            MinPlayers = 1,
            MaxPlayers = 2
        };

        public IGameInstance CreateInstance(IGameContext context) => new PokeInstance(context, Info);
    }

    /// <summary>第三游戏实例：无阶段、纯事件驱动，只依赖 <see cref="IGameContext"/>。</summary>
    private sealed class PokeInstance : IGameInstance
    {
        private readonly IGameContext _ctx;
        private readonly object _sync = new();
        private GameStatus _status = GameStatus.Waiting;
        private int _pokes;

        public PokeInstance(IGameContext ctx, GameInfo info)
        {
            _ctx = ctx;
            Info = info;
        }

        public GameInfo Info { get; }
        public string GameId => Info.GameId;
        public GameStatus Status { get { lock (_sync) return _status; } }

        public (bool ok, string msg) Start()
        {
            lock (_sync) { _status = GameStatus.Running; _pokes = 0; }
            return (true, "");
        }

        public Task RunAsync() => _ctx.BroadcastAsync("👆 戳一戳开始（无阶段）");

        public (bool ok, string msg) HandleAction(string op, JsonObject? payload, string connectionId)
        {
            lock (_sync)
            {
                if (_status != GameStatus.Running) return (false, "未开始");
                if (op != "poke") return (false, "未知操作");
                _pokes++;
                return (true, "");
            }
        }

        public JsonNode? BuildPayload(string viewerConnectionId)
        {
            var viewer = _ctx.FindByConnection(viewerConnectionId);
            if (viewer == null) return null;
            lock (_sync)
            {
                return new JsonObject
                {
                    ["game"] = GameId,
                    ["pokes"] = _pokes,
                    ["viewer"] = viewer.Username
                };
            }
        }

        public void DecoratePlayers(IReadOnlyList<PlayerDto> rows, string viewerUsername)
        {
            foreach (var row in rows)
            {
                row.Tag = "已戳";
                row.TagColor = "#22AAFF";
            }
        }

        public void Reset() { lock (_sync) { _status = GameStatus.Waiting; _pokes = 0; } }
    }

    public static void RunAll()
    {
        // ---- ① 登记即被收录：目录里只注册本新游戏（完全不含狼人杀） ----
        var catalog = new GameCatalog();
        catalog.Register(new PokeModule());
        Check.True(catalog.Contains(GameId), "第三游戏：Register 后立即被 GameCatalog 收录");
        Check.Equal(GameId, catalog.Describe(GameId)!.GameId, "第三游戏：目录可解析其元数据");
        Check.True(catalog.TryGetFactory(GameId, out _), "第三游戏：目录提供其实例工厂");

        // ---- ② 反射发现：模拟「只新增一个文件、不改组合根」 ----
        var discovered = GameDiscovery.Discover(typeof(PokeModule).Assembly)
            .Any(m => m.Info.GameId == GameId);
        Check.True(discovered, "第三游戏：反射发现可自动收录（组合根无需改动）");

        var channel = new EncryptedChannel(null!, new CryptoSessionStore());
        var options = new RoomOptions { MaxRooms = 10, EmptyGraceSeconds = 60, RecycleScanSeconds = 1 };
        var mgr = new RoomManager(channel, catalog, options);
        mgr.BindIdentity("p1", "u1");
        mgr.BindIdentity("p2", "u2");

        // ---- ③ 用其 GameId 建房成功，人数上限取其元数据（=2） ----
        var created = mgr.CreateRoom("p1", "戳一戳房", null, GameId);
        Check.True(created.ok, "第三游戏：用其 GameId 创建房间成功（平台零改动）");
        var room = mgr.Find(created.roomId)!;
        var info = room.ToInfo();
        Check.Equal(GameId, info.GameId, "第三游戏：房间绑定该 GameId");
        Check.Equal(2, info.MaxPlayers, "第三游戏：人数上限取自其元数据（=2）");
        Check.Equal("戳一戳", info.GameName, "第三游戏：房间列表显示名取自其元数据");
        Check.Equal("👆", info.GameEmoji, "第三游戏：房间列表 emoji 取自其元数据");

        // 满 2 人后再加入应被拒（验证上限确实生效）。
        mgr.JoinRoom(created.roomId, "p2", null);
        mgr.BindIdentity("p3", "u3");
        Check.False(mgr.JoinRoom(created.roomId, "p3", null).ok, "第三游戏：超过上限(2)的加入被拒");

        // ---- ④ 通用 start 能开局 ----
        Check.True(room.StartGame("p1").ok, "第三游戏：通用 start 可开局");

        // ---- ⑤ SnapshotFor 带其 GameId 与自己的 payload ----
        var dto = room.SnapshotFor("p1")!;
        Check.Equal(GameId, dto.GameId, "第三游戏：状态快照携带其 GameId");
        Check.True(dto.GamePayload is JsonObject jp && jp["game"]!.GetValue<string>() == GameId,
            "第三游戏：私有 payload 带自定义字段 game");
        Check.True(dto.GamePayload is JsonObject jp0 && jp0["pokes"]!.GetValue<int>() == 0,
            "第三游戏：私有 payload 带自定义计数字段 pokes");
        Check.Equal("u1", (dto.GamePayload as JsonObject)!["viewer"]!.GetValue<string>(),
            "第三游戏：私有 payload 按视角生成（含 viewer）");

        // ---- ⑥ 自定义 op 经 HandleGameAction 正常处理 ----
        Check.True(room.HandleGameAction("poke", new JsonObject(), "p1").ok,
            "第三游戏：自定义 op poke 经 HandleGameAction 成功");
        var dto2 = room.SnapshotFor("p1")!;
        Check.True(dto2.GamePayload is JsonObject jp2 && jp2["pokes"]!.GetValue<int>() == 1,
            "第三游戏：poke 生效（计数变为 1）");
        Check.False(room.HandleGameAction("nope", new JsonObject(), "p1").ok,
            "第三游戏：未知 op 被游戏自身拒绝");

        // ---- ⑦ 自定义标签出现在 PlayerDto.Tag ----
        Check.Equal("已戳", dto2.Players[0].Tag, "第三游戏：自定义玩家标签写入 PlayerDto.Tag");
        Check.Equal("#22AAFF", dto2.Players[0].TagColor, "第三游戏：自定义标签颜色写入 PlayerDto.TagColor");

        // ---- 反例（B7）：目录不含狼人杀时，平台仍能走通全链路；若平台暗含 werewolf 分支则会露馅 ----
        Check.False(catalog.Contains(GameIds.Werewolf), "反例：目录未注册狼人杀时 Contains(werewolf)=false");

        // ---- 未知游戏被拒（服务端兜底），不影响既有游戏 ----
        var rejected = mgr.CreateRoom("p1", "未知", null, "does_not_exist");
        Check.False(rejected.ok, "第三游戏：未知 GameId 建房被拒");
        Check.Equal(ServerMessages.GameUnavailable, rejected.msg, "第三游戏：未知 GameId 返回统一文案");

        // ---- ⑧ GUI 兜底：宿主遇到不认识的 GameId → UnknownGameViewModel 且不崩溃 ----
        RunUnknownGameFallback();
    }

    /// <summary>在测试宿主里构造 GUI 宿主并推送一个未知 GameId 的状态，断言兜底面板且不崩溃。</summary>
    private static void RunUnknownGameFallback()
    {
        var method = typeof(GameViewModel).GetMethod("OnStateReceived",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到 OnStateReceived");

        var vm = new GameViewModel();
        var state = new GameStateDto
        {
            GameId = "mystery_game",
            RoomId = "ZZZZZ",
            Status = RoomStatus.Waiting,
            Players = { new PlayerDto { Id = 0, Name = "u1", IsYou = true, IsHost = true } }
        };

        method.Invoke(vm, new object[] { state });
        Check.True(vm.Game is UnknownGameViewModel, "GUI 兜底：未知 GameId → UnknownGameViewModel（不崩溃）");
        Check.Equal("mystery_game", vm.Game!.GameId, "GUI 兜底：兜底面板记住未知 GameId");
        Check.Equal(0, vm.Game.Actions.Count, "GUI 兜底：兜底面板不提供任何操作按钮");
    }
}
