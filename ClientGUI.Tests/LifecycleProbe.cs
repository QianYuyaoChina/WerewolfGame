using System.Text.Json;
using System.Text.Json.Nodes;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// QA（严过关）独立编写的房间生命周期探针（<c>--lifecycle &lt;url&gt;</c>）。
/// 需服务端为「小局」配置：GameRules.MinPlayers=3、MaxPlayers=4、时长调小；
/// Room.EmptyGraceSeconds=3、Room.RecycleScanSeconds=1、Room.MaxRooms=2。
/// 覆盖：D10 一局结束→自动重置→同批玩家重开；D11 空房宽限期回收；E19 MaxRooms 上限。
/// </summary>
public static class LifecycleProbe
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>解析通用状态里的狼人杀私有负载（阶段 / 天数 / 身份等已迁入 GamePayload）。</summary>
    private static WerewolfGameState? Wf(GameStateDto? s)
        => s?.GamePayload?.Deserialize<WerewolfGameState>(Json);

    private sealed class C
    {
        private string? _actedKey;

        public required string Name { get; init; }
        public GameClient Client { get; } = new();
        public GameStateDto? State { get; set; }
        public bool SawDeath { get; set; }
        public readonly object Gate = new();

        public void Attach(bool drive)
        {
            Client.StateReceived += s =>
            {
                lock (Gate)
                {
                    State = s;
                    if (s.Players.Any(p => !p.IsAlive)) SawDeath = true;
                }
                if (drive) Drive(s);
            };
        }

        private void Drive(GameStateDto s)
        {
            var wf = Wf(s);
            if (wf == null) return;

            if (!s.IsAlive && wf.Phase != Phases.HunterShoot) return;

            if (wf.Phase == Phases.Night && !string.IsNullOrEmpty(wf.ActingRole) && wf.ActingRole == wf.YourRole)
            {
                var key = $"{wf.Day}:Night:{wf.ActingRole}";
                lock (Gate) { if (_actedKey == key) return; _actedKey = key; }
                var target = s.Players.FirstOrDefault(p => p.IsAlive && !p.IsYou)?.Id ?? -1;
                // 女巫本局一律不救（保证狼刀生效，快速进入 GameOver）；其余角色选一个非自己目标。
                _ = Safe(() => wf.YourRole == Roles.Witch
                    ? Client.CallGameOpAsync(WerewolfOps.Night,
                        JsonSerializer.SerializeToNode(new NightPayload { TargetId = -1, Action = "none" }))
                    : Client.CallGameOpAsync(WerewolfOps.Night,
                        JsonSerializer.SerializeToNode(new NightPayload { TargetId = target, Action = "do" })));
            }
            else if (wf.Phase == Phases.Vote && s.IsAlive)
            {
                var key = $"{wf.Day}:Vote";
                lock (Gate) { if (_actedKey == key) return; _actedKey = key; }
                _ = Safe(() => Client.CallGameOpAsync(WerewolfOps.Vote,
                    JsonSerializer.SerializeToNode(new TargetPayload { TargetId = -1 })));   // 全员弃票
            }
        }

        private static async Task Safe(Func<Task> f) { try { await f(); } catch { } }
    }

    private static int _pass;
    private static readonly List<string> _fail = new();

    private static void Ok(string what) { _pass++; Console.WriteLine("   ✅ " + what); }
    private static void Bad(string what) { _fail.Add(what); Console.WriteLine("   ❌ " + what); }
    private static void Eq<T>(T exp, T act, string what)
    {
        if (EqualityComparer<T>.Default.Equals(exp, act)) Ok(what);
        else Bad($"{what}（期望 {exp}，实际 {act}）");
    }
    private static void True(bool v, string what) { if (v) Ok(what); else Bad(what); }

    public static async Task<int> RunAsync(string url)
    {
        _pass = 0; _fail.Clear();

        async Task<C> Login(string name, bool drive)
        {
            var c = new C { Name = name };
            c.Attach(drive);
            await c.Client.ConnectAsync(url);
            var r = await c.Client.LoginAsync(name, "pw" + name);
            if (!r.ok) Bad($"{name} 登录失败：{r.msg}");
            return c;
        }
        GameStateDto? St(C c) { lock (c.Gate) return c.State; }

        async Task<bool> WaitUntil(Func<bool> cond, int ms)
        {
            var dl = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < dl) { if (cond()) return true; await Task.Delay(100); }
            return cond();
        }

        var all = new List<C>();
        C? obs2 = null;
        try
        {
            // ============ E19：MaxRooms 上限（配置=2） ============
            var g1 = await Login("g1", true); all.Add(g1);
            var g2 = await Login("g2", true); all.Add(g2);
            var g3 = await Login("g3", true); all.Add(g3);
            var obs = await Login("obs", false); all.Add(obs);

            var r0 = await g1.Client.CreateRoomAsync("lifecycle-room", null);
            var r1 = await g2.Client.CreateRoomAsync("second-room", null);
            var r2 = await g3.Client.CreateRoomAsync("third-room", null);
            True(r0.Ok && r1.Ok, "E19 上限=2 时前两个房间创建成功");
            Eq(false, r2.Ok, "E19 上限=2 时第 3 个房间被拒");
            Eq(ServerMessages.TooManyRooms, r2.Message, "E19 超上限返回「房间数已达上限，请稍后再试」");
            var rid = r0.RoomId;

            // ============ D10：一局跑到 GameOver → 自动重置 → 同批玩家重开 ============
            await g2.Client.JoinRoomAsync(rid, null);
            await g3.Client.JoinRoomAsync(rid, null);
            await Task.Delay(300);

            var start1 = await g1.Client.StartGameAsync();
            Eq(true, start1.ok, "D10 房主开局成功（3 人房）");

            var over = await WaitUntil(() => Wf(St(g1)) is { Phase: Phases.GameOver }, 40000);
            True(over, "D10 一局跑到 GameOver");

            var dayAtOver = Wf(St(g1))?.Day ?? -1;
            True(dayAtOver >= 1, $"D10 首局推进到第 {dayAtOver} 天（阶段确实推进过）");
            True(g1.SawDeath || g2.SawDeath || g3.SawDeath, "D10 首局出现玩家死亡（旧局存在死亡状态）");

            await Task.Delay(500);
            var listAfterOver = await obs.Client.ListRoomsAsync();
            var roomInfo = listAfterOver.Rooms.FirstOrDefault(r => r.Id == rid);
            True(roomInfo != null, "D10 结束后房间仍在列表中（不因结束而移除）");
            Eq(RoomStatus.GameOver, roomInfo?.Status ?? "?", "D10 结束后列表状态=已结束待重开(GameOver)");

            var start2 = await g1.Client.StartGameAsync();
            Eq(true, start2.ok, "D10 结束后同一批玩家可再次 start 成功");
            var runningAgain = await WaitUntil(
                () => Wf(St(g1)) is { } s2 && s2.Day >= 1 && s2.Phase is Phases.Night or Phases.Day or Phases.Discussion or Phases.Vote, 15000);
            True(runningAgain, "D10 新一局已开始推进");
            var newState = St(g1);
            Eq(1, Wf(newState)?.Day ?? -1, "D10 新一局 Day 从初始值 1 重新开始");
            True(newState != null && newState.Players.All(p => p.IsAlive), "D10 新一局所有玩家恢复存活（旧局死亡状态已清空）");

            // ============ D11：空房宽限期回收 ============
            foreach (var c in all)
                try { await c.Client.DisposeAsync(); } catch { }

            await Task.Delay(1000);   // < 宽限期(3s)
            obs2 = new C { Name = "obs2" };
            await obs2.Client.ConnectAsync(url);
            await obs2.Client.LoginAsync("obs2", "pwobs2");
            var within = await obs2.Client.ListRoomsAsync();
            True(within.Rooms.Any(r => r.Id == rid), "D11 宽限期内(≈1s)房间仍在列表");

            await Task.Delay(5000);   // > 宽限期(3s)+扫描(1s)
            var after = await obs2.Client.ListRoomsAsync();
            True(!after.Rooms.Any(r => r.Id == rid), "D11 超过宽限期后空房已从列表移除");
        }
        catch (Exception ex)
        {
            Bad("探针异常：" + ex.Message);
        }
        finally
        {
            foreach (var c in all)
                try { await c.Client.DisposeAsync(); } catch { }
            if (obs2 != null)
                try { await obs2.Client.DisposeAsync(); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_fail.Count == 0
            ? $"✅ LifecycleProbe 通过：{_pass} 项断言"
            : $"❌ LifecycleProbe 失败 {_fail.Count} 项，通过 {_pass} 项");
        foreach (var f in _fail) Console.WriteLine("   · " + f);
        return _fail.Count == 0 ? 0 : 1;
    }
}
