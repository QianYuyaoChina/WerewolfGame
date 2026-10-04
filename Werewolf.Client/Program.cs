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

        // ③ 输入用户名 / 密码（首次即注册）
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("首次登录即注册；已注册账号请输入正确密码以重连。");
            Console.Write("用户名：");
            string username = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(username)) { Print("⚠️ 用户名不能为空"); continue; }

            Console.Write("密  码：");
            string password = ReadPassword();
            if (password.Length < 3) { Print("⚠️ 密码至少 3 位"); continue; }

            JoinResult res;
            try
            {
                res = await _client.JoinAsync(username, password);
            }
            catch (Exception ex)
            {
                Print("❌ 调用失败：" + ex.Message);
                continue;
            }

            if (res.ok)
            {
                Print(res.msg == "reconnected"
                    ? "🔄 重连成功！已恢复你在房间中的身份。"
                    : "✅ 加入成功！");
                break;
            }

            Print("❌ " + res.msg);
        }

        PrintHelp();

        // ④ 指令循环
        while (true)
        {
            string? line = Console.ReadLine();
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            try { await HandleCommand(line); }
            catch (Exception ex) { Print("⚠️ " + ex.Message); }
        }

        await _client.DisposeAsync();
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
            Console.WriteLine($"┌─────────── 第 {s.Day} 天 · {Phases.DisplayName(s.Phase)} ───────────");
            var me = s.Players.FirstOrDefault(p => p.IsYou);
            Console.WriteLine($"│ 你：[{s.YourId}] {me?.Name}　身份：{s.YourRole}　{(s.IsAlive ? "存活" : "已出局")}");
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
  数字 (如 3)        夜间选择目标 / 投票 / 猎人开枪
  -1                 弃票 / 放弃开枪
  chat 内容          发言
  heal               女巫使用解药
  poison 3           女巫毒杀 3 号
  none               女巫今晚不用药
  help               显示本帮助

  掉线重连：重新启动本程序，输入服务器地址，
  再输入【相同】用户名 + 密码即可恢复身份。
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
