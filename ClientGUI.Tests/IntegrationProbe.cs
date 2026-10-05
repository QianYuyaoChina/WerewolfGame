using System.Text.Json;
using System.Text.Json.Nodes;
using Werewolf.Client.Share;
using Werewolf.Client.Share.Crypto;

namespace ClientGUI.Tests;

/// <summary>
/// 针对真实服务器的端到端校验（多房间版）：
/// 账号登录 → 建房（加密 / 公开）→ 他人按房间号加入 → 房间隔离 → 开局。
/// 需要调用方先启动服务器（见 Program.cs 的 --server 参数）。
/// </summary>
public static class IntegrationProbe
{
    public sealed class ProbeResult
    {
        public bool Ok { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    public static async Task<ProbeResult> RunAsync(string url)
    {
        var notes = new List<string>();
        var ok = true;

        void Fail(string what)
        {
            ok = false;
            notes.Add("❌ " + what);
        }

        void Pass(string what) => notes.Add("✅ " + what);

        var clients = new Dictionary<string, GameClient>(StringComparer.Ordinal);
        var states = new Dictionary<string, GameStateDto>(StringComparer.Ordinal);
        var messages = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // SignalR 回调在别的线程上写入集合，读写都必须串行化。
        var gate = new object();

        GameClient Make(string name)
        {
            var c = new GameClient();
            c.StateReceived += s => { lock (gate) states[name] = s; };
            c.MessageReceived += m =>
            {
                lock (gate)
                {
                    if (!messages.TryGetValue(name, out var list)) messages[name] = list = new List<string>();
                    list.Add(m);
                }
            };
            clients[name] = c;
            return c;
        }

        List<string> MessagesOf(string name)
        {
            lock (gate) return messages.TryGetValue(name, out var l) ? l.ToList() : new List<string>();
        }

        GameStateDto? StateOf(string name)
        {
            lock (gate) return states.TryGetValue(name, out var s) ? s : null;
        }

        var roomANames = new[] { "alice", "bob", "carol", "dave" };
        var roomBNames = new[] { "eve", "frank" };

        try
        {
            // ---------- 1. 六名玩家登录（账号校验） ----------
            foreach (var name in roomANames.Concat(roomBNames))
            {
                var c = Make(name);
                await c.ConnectAsync(url);
                var login = await c.LoginAsync(name, "pw" + name);
                if (!login.ok) Fail($"{name} 登录失败：{login.msg}");
            }
            if (ok) Pass("6 名玩家连接并登录（账号校验成功）");

            // ---------- 2. 创建加密房 ----------
            var roomA = await clients["alice"].CreateRoomAsync("新手房A", "123");
            if (!roomA.Ok) Fail("alice 创建加密房失败：" + roomA.Message);
            else Pass($"alice 创建加密房 {roomA.RoomId}（新手房A，密码 123），成为房主");

            if (clients["alice"].CurrentRoomId != roomA.RoomId)
                Fail("创建后 alice 的 CurrentRoomId 不是新房号");
            else
                Pass("创建后客户端记住当前房间号");

            // ---------- 3. 密码门禁 ----------
            var wrong = await clients["bob"].JoinRoomAsync(roomA.RoomId, "definitely-wrong");
            if (wrong.Ok) Fail("错误密码竟然加入成功");
            else if (wrong.Message == ServerMessages.WrongPassword) Pass("加密房错误密码被拒（统一文案「密码错误」）");
            else Fail("错误密码的提示不符合预期：" + wrong.Message);

            // ---------- 4. 正确密码加入 ----------
            var joinFailures = new List<string>();
            foreach (var name in new[] { "bob", "carol", "dave" })
            {
                var jr = await clients[name].JoinRoomAsync(roomA.RoomId, "123");
                if (!jr.Ok) joinFailures.Add($"{name}:{jr.Message}");
            }
            if (joinFailures.Count == 0) Pass("bob/carol/dave 以正确密码加入加密房");
            else Fail("正确密码加入失败：" + string.Join(", ", joinFailures));

            // ---------- 5. 第二个房间（公开） ----------
            var roomB = await clients["eve"].CreateRoomAsync("公开房B", null);
            if (!roomB.Ok) Fail("eve 创建公开房失败：" + roomB.Message);
            else Pass($"eve 创建公开房 {roomB.RoomId}（公开房B，无密码）");

            var fj = await clients["frank"].JoinRoomAsync(roomB.RoomId, null);
            if (!fj.Ok) Fail("frank 加入公开房失败：" + fj.Message);
            else Pass("frank 无需密码加入公开房");

            // ---------- 6. 房间列表包含两个新房间 ----------
            var listRes = await clients["alice"].ListRoomsAsync();
            var aInfo = listRes.Rooms.FirstOrDefault(r => r.Id == roomA.RoomId);
            var bInfo = listRes.Rooms.FirstOrDefault(r => r.Id == roomB.RoomId);
            if (aInfo == null || bInfo == null)
            {
                Fail("房间列表未包含两个新建房间");
            }
            else
            {
                var aOk = aInfo.HasPassword && aInfo.Host == "alice" && aInfo.PlayerCount >= 4
                          && aInfo.Status == RoomStatus.Waiting;
                var bOk = !bInfo.HasPassword && bInfo.Host == "eve";
                if (aOk && bOk)
                    Pass($"房间列表正确（A：加密/房主alice/{aInfo.PlayerCount}人/等待中；B：公开/房主eve）");
                else
                    Fail("房间列表字段异常：A=" + JsonSerializer.Serialize(aInfo) +
                         " B=" + JsonSerializer.Serialize(bInfo));
            }

            // ---------- 7. 状态推送携带房间信息 ----------
            await WaitUntil(() => StateOf("alice") is { } s && s.RoomId == roomA.RoomId, 5000);
            var aState = StateOf("alice");
            if (aState != null && aState.RoomId == roomA.RoomId
                && aState.RoomName == "新手房A" && aState.HostName == "alice")
                Pass("状态推送携带房间号/房间名/房主");
            else
                Fail("状态未携带正确房间信息：" +
                     (aState == null ? "无状态" : $"{aState.RoomId}/{aState.RoomName}/{aState.HostName}"));

            if (clients["alice"].CurrentRoomId == roomA.RoomId && clients["eve"].CurrentRoomId == roomB.RoomId)
                Pass("客户端 CurrentRoomId 与所在房间一致");
            else
                Fail("客户端 CurrentRoomId 不一致");

            // ---------- 8. 房间隔离：A 房消息不出现在 B 房 ----------
            await clients["alice"].ChatAsync("A房专属消息-A1");
            await clients["eve"].ChatAsync("B房专属消息-B1");
            await Task.Delay(700);

            var daveMsgs = MessagesOf("dave");
            var eveMsgs = MessagesOf("eve");
            var aliceMsgs = MessagesOf("alice");
            var frankMsgs = MessagesOf("frank");

            if (daveMsgs.Any(m => m.Contains("A房专属消息-A1"))) Pass("A 房内消息送达到同房玩家 dave");
            else Fail("A 房消息未送达同房玩家 dave");

            if (!eveMsgs.Any(m => m.Contains("A房专属消息-A1"))) Pass("A 房消息未泄漏到 B 房（房间隔离生效）");
            else Fail("A 房消息泄漏到了 B 房！");

            if (frankMsgs.Any(m => m.Contains("B房专属消息-B1"))) Pass("B 房内消息送达到同房玩家 frank");
            else Fail("B 房消息未送达同房玩家 frank");

            if (!aliceMsgs.Any(m => m.Contains("B房专属消息-B1"))) Pass("B 房消息未泄漏到 A 房（房间隔离生效）");
            else Fail("B 房消息泄漏到了 A 房！");

            // ---------- 9. 开局（仅房主） ----------
            var guestStart = await clients["bob"].StartGameAsync();
            if (guestStart.ok) Fail("非房主竟然可以开局");
            else Pass("非房主无法开局：" + guestStart.msg);

            var start = await clients["alice"].StartGameAsync();
            if (!start.ok) Fail("房主开局失败：" + start.msg);
            else Pass("房主成功开局");

            await WaitUntil(() => roomANames.All(n => MessagesOf(n).Any(m => m.Contains("你的身份是"))), 8000);
            var gotRoles = roomANames.Count(n => MessagesOf(n).Any(m => m.Contains("你的身份是")));
            if (gotRoles == 4) Pass("A 房 4 名玩家均收到身份私聊");
            else Fail($"A 房只有 {gotRoles}/4 收到身份私聊");

            if (MessagesOf("eve").Any(m => m.Contains("你的身份是")))
                Fail("B 房玩家收到了 A 房的身份私聊（隔离破坏）");
            else Pass("B 房玩家未收到 A 房身份私聊");

            // ---------- 9.2 B 房不受 A 房开局影响 ----------
            var eveState = StateOf("eve");
            var evePayload = eveState?.GamePayload?.Deserialize<WerewolfGameState>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (eveState != null && eveState.Players.Count == 2
                && evePayload?.Phase is Phases.Waiting or Phases.GameOver
                && eveState.RoomId == roomB.RoomId)
                Pass("B 房保持自身状态（未受 A 房开局影响）");
            else
                Fail($"B 房状态异常：{evePayload?.Phase}/{eveState?.Players.Count}");

            // ---------- 9.3 心跳 ----------
            try
            {
                var latency = await clients["alice"].PingAsync();
                if (latency >= 0 && latency < 5000) Pass($"心跳往返正常（{latency} ms）");
                else Fail("心跳时延异常：" + latency + " ms");
            }
            catch (Exception ex)
            {
                Fail("心跳失败：" + ex.Message);
            }

            // ---------- 9.4 JSON 契约：用服务器传来的形状反序列化 ----------
            // 多游戏改造后 GameStateDto 通用化：阶段/天数/身份等迁入狼人杀私有负载 GamePayload。
            var wire = JsonNode.Parse("""
            {
              "GameId": "werewolf",
              "RoomId": "ABCDE",
              "RoomName": "新手房",
              "HostName": "alice",
              "Running": true,
              "Status": "Running",
              "YourId": 1,
              "IsAlive": true,
              "Players": [
                { "Id": 0, "Name": "alice", "IsAlive": true, "IsHost": true, "IsYou": false, "IsConnected": true, "Tag": "狼人", "TagColor": "#FFA07A" },
                { "Id": 1, "Name": "bob", "IsAlive": false, "IsHost": false, "IsYou": true, "IsConnected": false, "Tag": null }
              ],
              "GamePayload": {
                "Phase": "Night",
                "Day": 2,
                "YourRole": "狼人",
                "YourRoleDesc": "夜晚与队友共同击杀一名玩家，白天伪装成好人。",
                "ActingRole": "狼人"
              }
            }
            """);

            var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = wire.Deserialize<GameStateDto>(opt);
            var parsedPayload = parsed?.GamePayload?.Deserialize<WerewolfGameState>(opt);
            if (parsed is { YourId: 1, IsAlive: true, RoomId: "ABCDE", RoomName: "新手房", HostName: "alice" }
                && parsed.GameId == GameIds.Werewolf && parsed.Running
                && parsed.Players.Count == 2
                && parsed.Players[0] is { Id: 0, Name: "alice", IsHost: true, IsYou: false, IsConnected: true, Tag: "狼人", TagColor: "#FFA07A" }
                && parsed.Players[1] is { IsAlive: false, IsYou: true, IsConnected: false, Tag: null }
                && parsedPayload is { Phase: "Night", Day: 2, YourRole: "狼人" })
                Pass("DTO 与服务器 JSON 形状匹配（通用信封 + 狼人杀 GamePayload / null Tag / 断线标记）");
            else
                Fail("DTO 反序列化结果与预期不符");

            // ---------- 9.5 离开房间（不拆连接） ----------
            var left = await clients["frank"].LeaveRoomAsync();
            if (left.ok && !clients["frank"].IsJoined && clients["frank"].IsConnected)
                Pass("frank 退出房间成功且连接保留");
            else
                Fail($"frank 退出房间异常：ok={left.ok} joined={clients["frank"].IsJoined} " +
                     $"connected={clients["frank"].IsConnected}");

            // ---------- 10. 身份认证：错误的预共享密钥必须被拒绝 ----------
            var originalPsk = Environment.GetEnvironmentVariable(PreSharedKey.EnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    PreSharedKey.EnvironmentVariable, "definitely-not-the-server-psk");

                var rogue = new GameClient();
                try
                {
                    await rogue.ConnectAsync(url);
                    Fail("错误的预共享密钥竟然握手成功（身份认证失效）");
                }
                catch (Exception ex) when (ex.Message.Contains("身份校验"))
                {
                    Pass("错误的预共享密钥被拒绝，身份认证生效：" + ex.Message);
                }
                catch (Exception ex)
                {
                    Fail("错误 PSK 的失败原因不符合预期：" + ex.Message);
                }
                finally
                {
                    try { await rogue.DisposeAsync(); } catch { }
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(PreSharedKey.EnvironmentVariable, originalPsk);
            }
        }
        catch (Exception ex)
        {
            Fail("异常：" + ex.Message);
        }
        finally
        {
            foreach (var c in clients.Values)
            {
                try { await c.DisposeAsync(); } catch { }
            }
        }

        return new ProbeResult { Ok = ok, Notes = notes };
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
    }
}
