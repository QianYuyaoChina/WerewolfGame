using System.Text.Json.Nodes;
using Werewolf.Client.Share;
using Werewolf.Server;
using Werewolf.Server.Games;
using Werewolf.Server.Games.Werewolf;

namespace ClientGUI.Tests;

/// <summary>
/// 扩展性验证（默认模式运行）：注册一个**与狼人杀无关**的假游戏模块，
/// 走完「登记元数据 → 目录收录 → 建房 → 开局 → 状态快照 → 玩家标签 → 私有 payload」全链路，
/// 证明「新增游戏不需要改动任何平台模块」——本文件不引用任何狼人杀玩法类型即是最好的证据。
/// </summary>
public static class FakeGameProbe
{
    /// <summary>测试专用假游戏：无阶段、无夜晚，只有“举手”一种操作。</summary>
    private sealed class FakeGameModule : IGameModule
    {
        public GameInfo Info => new()
        {
            GameId = "fake_probe",
            DisplayName = "假游戏",
            Emoji = "🧪",
            Tagline = "测试用",
            MinPlayers = 1,
            MaxPlayers = 4
        };

        public IGameInstance CreateInstance(IGameContext context) => new FakeGameInstance(context, Info);
    }

    /// <summary>假游戏实例：不依赖任何平台实现类型，只经 <see cref="IGameContext"/> 交互。</summary>
    private sealed class FakeGameInstance : IGameInstance
    {
        private readonly IGameContext _ctx;
        private readonly object _sync = new();
        private GameStatus _status = GameStatus.Waiting;

        public FakeGameInstance(IGameContext ctx, GameInfo info)
        {
            _ctx = ctx;
            Info = info;
        }

        public GameInfo Info { get; }

        public string GameId => Info.GameId;

        public GameStatus Status { get { lock (_sync) return _status; } }

        public (bool ok, string msg) Start()
        {
            lock (_sync) _status = GameStatus.Running;
            return (true, "");
        }

        public async Task RunAsync() => await _ctx.BroadcastAsync("🧪 假游戏开始（无阶段）");

        public (bool ok, string msg) HandleAction(string op, JsonObject? payload, string connectionId)
        {
            lock (_sync)
            {
                if (_status != GameStatus.Running) return (false, "未开始");
            }
            return (true, "");
        }

        public JsonNode? BuildPayload(string viewerConnectionId) => new JsonObject { ["note"] = "fake" };

        public void DecoratePlayers(IReadOnlyList<PlayerDto> rows, string viewerUsername)
        {
            foreach (var row in rows) row.Tag = "🧪";
        }

        public void Reset()
        {
            lock (_sync) _status = GameStatus.Waiting;
        }

        public void OnPlayerJoined(RoomPlayer player) { }

        public void OnPlayerLeft(RoomPlayer player, bool stillInGame) { }

        public void OnPlayerReconnected(RoomPlayer player) { }
    }

    public static void RunAll()
    {
        var channel = new EncryptedChannel(null!, new CryptoSessionStore());
        var options = new RoomOptions { MaxRooms = 10, EmptyGraceSeconds = 60, RecycleScanSeconds = 1 };

        var catalog = new GameCatalog();
        catalog.Register(new WerewolfGameModule());
        catalog.Register(new FakeGameModule());

        Check.True(catalog.Contains("fake_probe"), "扩展性：新游戏登记后立即被目录收录");
        Check.Equal(2, catalog.Available.Count, "扩展性：目录同时列出狼人杀与假游戏");

        var mgr = new RoomManager(channel, catalog, options);
        mgr.BindIdentity("c1", "u1");
        var r = mgr.CreateRoom("c1", "假游戏房", null, "fake_probe");
        Check.True(r.ok, "扩展性：用新游戏创建房间成功（平台零改动）");

        var room = mgr.Find(r.roomId)!;
        Check.Equal("fake_probe", room.ToInfo().GameId, "扩展性：房间绑定新游戏");
        Check.Equal(4, room.ToInfo().MaxPlayers, "扩展性：人数上限取自新游戏元数据");
        Check.True(room.StartGame("c1").ok, "扩展性：新游戏可经通用 start 开局");

        var dto = room.SnapshotFor("c1")!;
        Check.Equal("fake_probe", dto.GameId, "扩展性：状态快照携带新游戏 GameId");
        Check.True(dto.GamePayload != null && dto.GamePayload["note"]!.GetValue<string>() == "fake",
            "扩展性：游戏私有 payload 原样透传");
        Check.Equal("🧪", dto.Players[0].Tag, "扩展性：游戏自定义玩家标签生效");

        // 未知游戏：目录拒绝（服务端健壮兜底），不影响既有游戏。
        var rejected = mgr.CreateRoom("c1", "未知游戏房", null, "does_not_exist");
        Check.False(rejected.ok, "扩展性：未知游戏被拒绝");
        Check.Equal(ServerMessages.GameUnavailable, rejected.msg, "扩展性：未知游戏返回统一文案");
    }
}
