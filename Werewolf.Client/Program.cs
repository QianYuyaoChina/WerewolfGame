using System.Text;
using Microsoft.AspNetCore.SignalR.Client;

// ---------------- DTO ----------------
class PlayerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsAlive { get; set; }
    public bool IsHost { get; set; }
    public bool IsYou { get; set; }
    public bool IsConnected { get; set; }
    public string? Role { get; set; }
}

class GameStateDto
{
    public string Phase { get; set; } = "";
    public int Day { get; set; }
    public int YourId { get; set; }
    public string YourRole { get; set; } = "";
    public string YourRoleDesc { get; set; } = "";
    public bool IsAlive { get; set; }
    public List<PlayerDto> Players { get; set; } = new();
}

class JoinResult
{
    public bool ok { get; set; }
    public string msg { get; set; } = "";
}

// ---------------- 主程序 ----------------
class Program
{
    static readonly object ConsoleLock = new();
    static GameStateDto? _state;
    static HubConnection _conn = null!;

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("════════════ 狼人杀 ════════════");

        // ① 输入服务器地址
        Console.Write("请输入服务器地址（默认 http://localhost:5000/gamehub）：");
        string url = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrEmpty(url)) url = "http://localhost:5000/gamehub";

        // ② 连接
        _conn = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect()
            .Build();

        _conn.On<string>("Message", m => Print(m));
        _conn.On<GameStateDto>("State", s =>
        {
            _state = s;
            RenderState(s);
        });

        try
        {
            await _conn.StartAsync();
            Print($"✅ 已连接服务器：{url}");
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
                res = await _conn.InvokeAsync<JoinResult>("Join", username, password);
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

        await _conn.DisposeAsync();
    }

    static async Task HandleCommand(string line)
    {
        var s = _state;

        if (line.Equals("help", StringComparison.OrdinalIgnoreCase)) { PrintHelp(); return; }
        if (line.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            var r = await _conn.InvokeAsync<JoinResult>("Start");
            if (!r.ok) Print("⚠️ " + r.msg);
            return;
        }
        if (line.StartsWith("chat ", StringComparison.OrdinalIgnoreCase))
        {
            await _conn.SendAsync("Chat", line[5..]);
            return;
        }

        // 女巫专属指令
        if (s?.Phase == "Night" && s.YourRole == "女巫")
        {
            if (line.Equals("heal", StringComparison.OrdinalIgnoreCase))
            {
                await _conn.SendAsync("NightAction", -2, "heal");
                Print("✅ 已使用解药");
                return;
            }
            if (line.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                await _conn.SendAsync("NightAction", -1, "none");
                Print("✅ 今晚不使用药水");
                return;
            }
            if (line.StartsWith("poison ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[7..].Trim(), out int pid))
            {
                await _conn.SendAsync("NightAction", pid, "poison");
                Print($"✅ 已使用毒药 → [{pid}]");
                return;
            }
        }

        // 纯数字 → 阶段化指令
        if (int.TryParse(line, out int id))
        {
            switch (s?.Phase)
            {
                case "Night":
                    await _conn.SendAsync("NightAction", id, "do");
                    Print($"✅ 已选择 [{id}]");
                    return;
                case "Vote":
                    await _conn.SendAsync("Vote", id);
                    Print(id < 0 ? "✅ 已弃票" : $"✅ 已投票 → [{id}]");
                    return;
                case "HunterShoot":
                    await _conn.SendAsync("HunterShot", id);
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
            Console.WriteLine($"┌─────────── 第 {s.Day} 天 · {PhaseName(s.Phase)} ───────────");
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

    static string PhaseName(string phase) => phase switch
    {
        "Waiting" => "等待开始",
        "Night" => "🌙 夜晚",
        "Day" => "☀️ 天亮",
        "Discussion" => "💬 讨论",
        "Vote" => "🗳 投票",
        "HunterShoot" => "🔫 猎人开枪",
        "GameOver" => "🏁 游戏结束",
        _ => phase
    };

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