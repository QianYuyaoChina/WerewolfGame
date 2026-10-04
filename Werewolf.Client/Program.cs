using System.Text;
using Werewolf.Client.Share;

// ---------------- 主程序 ----------------
class Program
{
    static readonly object ConsoleLock = new();
    static GameStateDto? _state;
    static GameClient _client = null!;

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("════════════ 狼人杀 ════════════");

        // ① 输入服务器地址
        Console.Write("请输入服务器地址（默认 http://localhost:5000/gamehub）：");
        string url = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrEmpty(url)) url = "http://localhost:5000/gamehub";

        // ② 连接：明文 HTTP 传输，连接后自动用快速幂 DH 协商会话密钥（应用层加密）
        _client = new GameClient();
        _client.MessageReceived += m => Print(m);
        _client.StateReceived += s =>
        {
            _state = s;
            RenderState(s);
        };

        try
        {
            await _client.ConnectAsync(url);
            Print($"✅ 已连接服务器：{url}");
            Print($"🔐 会话密钥已协商（指纹 {_client.SessionKeyFingerprint}），所有消息均已加密。");
        }
        catch (Exception ex)
        {
            Print("❌ 无法连接服务器：" + ex.Message);
            return;
        }

        // ③ 输入用户名 / 密码并登录（首次即注册）；登录只做账号校验，不再直接进房。
        if (!await LoginAsync()) return;

        // ④ 房间列表 → 加入 / 创建；进入房间后进入指令循环，leave 返回列表。
        while (true)
        {
            if (!await RoomListLoopAsync()) break;   // 用户退出 / 列表拉取失败
            await GameLoopAsync();                    // 进入房间；leave 时返回，继续外层
        }

        await _client.DisposeAsync();
    }

    // ==================== 登录 ====================

    static async Task<bool> LoginAsync()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("首次登录即注册；已注册账号请输入正确密码。");
            Console.Write("用户名：");
            string username = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(username)) { Print("⚠️ 用户名不能为空"); continue; }

            Console.Write("密  码：");
            string password = ReadPassword();
            if (password.Length < 3) { Print("⚠️ 密码至少 3 位"); continue; }

            JoinResult res;
            try
            {
                res = await _client.LoginAsync(username, password);
            }
            catch (Exception ex)
            {
                Print("❌ 调用失败：" + ex.Message);
                continue;
            }

            if (res.ok)
            {
                Print($"✅ 登录成功！欢迎 {username}。");
                return true;
            }

            Print("❌ " + res.msg);
        }
    }

    // ==================== 房间列表 ====================

    /// <summary>展示房间列表并处理「序号加入 / c 创建 / r 刷新 / q 退出」。返回是否进入了房间。</summary>
    static async Task<bool> RoomListLoopAsync()
    {
        while (true)
        {
            RoomListResult list;
            try
            {
                list = await _client.ListRoomsAsync();
            }
            catch (Exception ex)
            {
                Print("❌ 无法获取房间列表：" + ex.Message);
                return false;
            }

            var rooms = list.Rooms;
            PrintRoomList(rooms);

            Console.WriteLine();
            Console.Write("输入序号加入 / 输入 c 创建房间 / 输入 r 刷新 / 输入 q 退出：\n> ");
            string line = (Console.ReadLine() ?? "").Trim();

            if (line.Equals("q", StringComparison.OrdinalIgnoreCase)) return false;
            if (line.Equals("r", StringComparison.OrdinalIgnoreCase)) continue;

            if (line.Equals("c", StringComparison.OrdinalIgnoreCase))
            {
                if (await CreateRoomFlowAsync()) return true;
                continue;
            }

            if (int.TryParse(line, out int index) && index >= 1 && index <= rooms.Count)
            {
                if (await JoinRoomFlowAsync(rooms[index - 1])) return true;
                continue;
            }

            Print("⚠️ 无效输入，请输入序号 / c / r / q。");
        }
    }

    static async Task<bool> CreateRoomFlowAsync()
    {
        Console.Write("房间名（回车使用默认名）：");
        string name = (Console.ReadLine() ?? "").Trim();

        Console.Write("密码（回车表示不设密码）：");
        string pwd = ReadPassword();
        string? password = string.IsNullOrEmpty(pwd) ? null : pwd;

        try
        {
            var rr = await _client.CreateRoomAsync(name, password);
            if (!rr.Ok) { Print("❌ 创建失败：" + rr.Message); return false; }

            string extra = password == null ? "（公开房间）" : "（加密房间）";
            Print($"✅ 创建成功！房间号 {rr.RoomId}{extra}，你是房主。");
            PrintHelp();
            return true;
        }
        catch (Exception ex)
        {
            Print("❌ 创建失败：" + ex.Message);
            return false;
        }
    }

    static async Task<bool> JoinRoomFlowAsync(RoomInfoDto room)
    {
        string? password = null;
        if (room.HasPassword)
        {
            Print("该房间需要密码。");
            Console.Write("密码：");
            password = ReadPassword();
        }

        try
        {
            var rr = await _client.JoinRoomAsync(room.Id, password);
            if (!rr.Ok) { Print("❌ 加入失败：" + rr.Message); return false; }

            Print(rr.Reconnected
                ? $"🔄 已恢复房间身份（{rr.RoomId}）。"
                : $"✅ 加入成功！房间 {rr.RoomId}（{room.Name}）。");
            PrintHelp();
            return true;
        }
        catch (Exception ex)
        {
            Print("❌ 加入失败：" + ex.Message);
            return false;
        }
    }

    static void PrintRoomList(List<RoomInfoDto> rooms)
    {
        Print("\n──────── 房间列表 ────────");
        if (rooms.Count == 0)
        {
            Print("  （暂无房间，输入 c 创建）");
            return;
        }

        for (var i = 0; i < rooms.Count; i++)
        {
            var r = rooms[i];
            string lockMark = r.HasPassword ? " 🔒" : "";
            string host = string.IsNullOrEmpty(r.Host) ? "" : $"   房主:{r.Host}";
            Print($"  {i + 1}) {r.Id}  {r.Name,-16} {r.PlayerCount}/{r.MaxPlayers}   " +
                  $"{RoomStatus.Name(r.Status)}{lockMark}{host}");
        }
    }

    // ==================== 房间内指令循环 ====================

    static async Task GameLoopAsync()
    {
        while (true)
        {
            string? line = Console.ReadLine();
            if (line == null) return;
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line.Equals("leave", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var res = await _client.LeaveRoomAsync();
                    Print(res.ok ? "🚪 已退出房间，返回房间列表。" : "⚠️ " + res.msg);
                }
                catch (Exception ex)
                {
                    Print("⚠️ 退出房间失败：" + ex.Message);
                }
                _state = null;
                return;
            }

            try { await HandleCommand(line); }
            catch (Exception ex) { Print("⚠️ " + ex.Message); }
        }
    }

    static async Task HandleCommand(string line)
    {
        var s = _state;

        if (line.Equals("help", StringComparison.OrdinalIgnoreCase)) { PrintHelp(); return; }
        if (line.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            var r = await _client.StartGameAsync();
            if (!r.ok) Print("⚠️ " + r.msg);
            return;
        }
        if (line.StartsWith("chat ", StringComparison.OrdinalIgnoreCase))
        {
            await _client.ChatAsync(line[5..]);
            return;
        }

        // 女巫专属指令
        if (s?.Phase == Phases.Night && s.YourRole == Roles.Witch)
        {
            if (line.Equals("heal", StringComparison.OrdinalIgnoreCase))
            {
                await _client.NightActionAsync(-2, "heal");
                Print("✅ 已使用解药");
                return;
            }
            if (line.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                await _client.NightActionAsync(-1, "none");
                Print("✅ 今晚不使用药水");
                return;
            }
            if (line.StartsWith("poison ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[7..].Trim(), out int pid))
            {
                await _client.NightActionAsync(pid, "poison");
                Print($"✅ 已使用毒药 → [{pid}]");
                return;
            }
        }

        // 纯数字 → 阶段化指令
        if (int.TryParse(line, out int id))
        {
            switch (s?.Phase)
            {
                case Phases.Night:
                    await _client.NightActionAsync(id, "do");
                    Print($"✅ 已选择 [{id}]");
                    return;
                case Phases.Vote:
                    await _client.VoteAsync(id);
                    Print(id < 0 ? "✅ 已弃票" : $"✅ 已投票 → [{id}]");
                    return;
                case Phases.HunterShoot:
                    await _client.HunterShotAsync(id);
                    Print(id < 0 ? "✅ 放弃开枪" : $"✅ 开枪 → [{id}]");
                    return;
                default:
                    Print("⚠️ 当前阶段不能选择目标。");
                    return;
            }
        }

        Print("⚠️ 未知指令，输入 help 查看帮助。");
    }

    // ---------------- 渲染 ----------------

    static void Print(string msg)
    {
        lock (ConsoleLock) Console.WriteLine(msg);
    }

    static void RenderState(GameStateDto s)
    {
        lock (ConsoleLock)
        {
            Console.WriteLine();
            string roomHeader = string.IsNullOrEmpty(s.RoomId)
                ? ""
                : $" · 房间 {s.RoomId}";
            Console.WriteLine($"┌─────────── 第 {s.Day} 天 · {Phases.DisplayName(s.Phase)}{roomHeader} ───────────");
            var me = s.Players.FirstOrDefault(p => p.IsYou);
            Console.WriteLine($"│ 你：[{s.YourId}] {me?.Name}　身份：{s.YourRole}　{(s.IsAlive ? "存活" : "已出局")}");
            if (!string.IsNullOrEmpty(s.RoomName) || !string.IsNullOrEmpty(s.HostName))
                Console.WriteLine($"│ 房间：{s.RoomName}　房主：{s.HostName}");
            Console.WriteLine("│ 玩家列表：");
            foreach (var p in s.Players)
            {
                string role = p.Role != null ? $" <{p.Role}>" : "";
                string you = p.IsYou ? " ←你" : "";
                string host = p.IsHost ? " 👑" : "";
                string dc = p.IsConnected ? "" : " ⚠掉线";
                string dead = p.IsAlive ? "" : "（死亡）";
                Console.WriteLine($"│   [{p.Id}] {p.Name}{dead}{role}{host}{you}{dc}");
            }
            Console.WriteLine("└──────────────────────────────────────");
        }
    }

    static void PrintHelp()
    {
        Print(@"
──────────── 指令帮助 ────────────
  start              房主开始游戏
  leave              退出当前房间，返回房间列表
  数字 (如 3)        夜间选择目标 / 投票 / 猎人开枪
  -1                 弃票 / 放弃开枪
  chat 内容          发言
  heal               女巫使用解药
  poison 3           女巫毒杀 3 号
  none               女巫今晚不用药
  help               显示本帮助

  掉线重连：重新启动本程序，输入服务器地址，
  再输入【相同】用户名 + 密码即可恢复原房间身份。
──────────────────────────────────");
    }

    /// <summary>带掩码的密码输入；失败时回退到 ReadLine</summary>
    static string ReadPassword()
    {
        try
        {
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            return sb.ToString();
        }
        catch
        {
            return Console.ReadLine() ?? "";
        }
    }
}
