using System.Text;
using Werewolf.Client;
using Werewolf.Client.Share;

// ---------------- 主程序 ----------------
class Program
{
    static readonly object ConsoleLock = new();
    static GameStateDto? _state;
    static GameClient _client = null!;

    /// <summary>启动时若直接粘贴了邀请码，登录完成后用它直接进房（跳过房间列表）。</summary>
    static InvitePayload? _pendingInvite;

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("════════════ 狼人杀 ════════════");

        // ① 输入服务器地址（也支持直接粘贴邀请码）
        Console.Write("请输入服务器地址（默认 http://localhost:5000/gamehub），或直接粘贴邀请码：");
        string input = Console.ReadLine()?.Trim() ?? "";
        string url;
        if (InviteCode.LooksLikeCode(input))
        {
            if (!InviteCode.TryParse(input, out var invite, out var inviteError) || invite == null)
            {
                Print("❌ 邀请码无效：" + inviteError);
                return;
            }

            url = invite.Url;
            _pendingInvite = invite;
            Print($"🎟 已识别邀请码：将连接 {url} 并在登录后进入房间 {invite.RoomId}。");
        }
        else
        {
            url = string.IsNullOrEmpty(input) ? "http://localhost:5000/gamehub" : input;
        }

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

        // ③.5 启动时粘贴过邀请码 → 登录后直接进房（房间密码已由邀请码携带）
        if (_pendingInvite != null)
        {
            var pending = _pendingInvite;
            _pendingInvite = null;
            try
            {
                var rr = await _client.JoinByInviteAsync(pending);
                if (rr.Ok)
                {
                    Print($"🎟 已通过邀请码进入房间 {rr.RoomId}。");
                    PrintHelp();
                    await GameLoopAsync();     // leave 之后继续走下面的房间列表循环
                }
                else
                {
                    Print($"❌ 邀请码进房失败：{rr.Message}，已回到房间列表。");
                }
            }
            catch (Exception ex)
            {
                Print($"❌ 邀请码进房失败：{ex.Message}，已回到房间列表。");
            }
        }

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
            Console.Write("输入序号加入 / j 用邀请码加入 / c 创建房间 / r 刷新 / q 退出：\n> ");
            string line = (Console.ReadLine() ?? "").Trim();

            if (line.Equals("q", StringComparison.OrdinalIgnoreCase)) return false;
            if (line.Equals("r", StringComparison.OrdinalIgnoreCase)) continue;

            if (line.Equals("j", StringComparison.OrdinalIgnoreCase))
            {
                if (await JoinByInviteFlowAsync()) return true;
                continue;
            }

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

            Print("⚠️ 无效输入，请输入序号 / j / c / r / q。");
        }
    }

    /// <summary>用邀请码加入：码里含服务器地址与房间密码，无需再输密码。</summary>
    static async Task<bool> JoinByInviteFlowAsync()
    {
        Console.Write("邀请码：");
        string code = (Console.ReadLine() ?? "").Trim();

        if (!InviteCode.TryParse(code, out var invite, out var error) || invite == null)
        {
            Print("❌ " + error);
            return false;
        }

        try
        {
            var rr = await _client.JoinByInviteAsync(invite);
            if (!rr.Ok) { Print("❌ 加入失败：" + rr.Message); return false; }

            Print($"🎟 已通过邀请码进入房间 {rr.RoomId}（密码已自动验证）。");
            PrintHelp();
            return true;
        }
        catch (Exception ex)
        {
            Print("❌ 加入失败：" + ex.Message);
            return false;
        }
    }

    static async Task<bool> CreateRoomFlowAsync()
    {
        Console.Write("房间名（回车使用默认名）：");
        string name = (Console.ReadLine() ?? "").Trim();

        // 选择游戏（服务器下发 gamelist；回车默认狼人杀）。
        string? gameId = GameIds.Werewolf;
        try
        {
            var games = await _client.ListGamesAsync();
            if (games.Ok && games.Games.Count > 0)
            {
                Console.WriteLine("可选游戏：");
                for (var i = 0; i < games.Games.Count; i++)
                {
                    var g = games.Games[i];
                    Console.WriteLine($"  {i + 1}) {g.Label}　（{g.MinPlayers}-{g.MaxPlayers} 人）");
                }

                var fallback = games.Games.FirstOrDefault(g => g.GameId == GameIds.Werewolf) ?? games.Games[0];
                Console.Write($"选择游戏序号（回车默认 {fallback.Label}）：");
                var pick = (Console.ReadLine() ?? "").Trim();
                gameId = int.TryParse(pick, out var idx) && idx >= 1 && idx <= games.Games.Count
                    ? games.Games[idx - 1].GameId
                    : fallback.GameId;
            }
        }
        catch
        {
            // 拉取失败 → 回退默认狼人杀（服务端也会对缺失 gameId 做同样回退）。
        }

        Console.Write("密码（回车表示不设密码）：");
        string pwd = ReadPassword();
        string? password = string.IsNullOrEmpty(pwd) ? null : pwd;

        try
        {
            var rr = await _client.CreateRoomAsync(name, password, gameId);
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
            string game = string.IsNullOrWhiteSpace(r.GameName)
                ? (string.IsNullOrWhiteSpace(r.GameId) ? "—" : r.GameId)
                : (string.IsNullOrEmpty(r.GameEmoji) ? r.GameName : $"{r.GameEmoji} {r.GameName}");
            Print($"  {i + 1}) {r.Id}  {game,-14} {r.Name,-16} {r.PlayerCount}/{r.MaxPlayers}   " +
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
                    // 控制台不做「是否保留房间」的询问：房主且房间只剩自己时按「不保留」处理（直接关闭房间）。
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
            // 用带回执的调用：轮流发言的玩法会在服务端拒绝插话，这里要把原因打出来。
            var r = await _client.CallGameOpAsync(Ops.Chat,
                System.Text.Json.JsonSerializer.SerializeToNode(new ChatPayload { Text = line[5..] }));
            if (!r.ok) Print("⚠️ " + r.msg);
            return;
        }
        if (line.Equals("invite", StringComparison.OrdinalIgnoreCase))
        {
            await ShowInviteAsync();
            return;
        }
        if (line.Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            await HostSettingsMenuAsync();
            return;
        }

        // 本房间当前游戏的操作指令（数字 / heal / poison / none 等）委派给对应游戏模块。
        var module = ConsoleGameRegistry.Resolve(s?.GameId);
        if (await module.TryHandleAsync(line, s, _client)) return;

        Print("⚠️ 未知指令，输入 help 查看帮助。");
    }

    // ---------------- 渲染 ----------------

    static void Print(string msg)
    {
        lock (ConsoleLock) Console.WriteLine(msg);
    }

    // ==================== 房主设置 ====================

    /// <summary>
    /// 生成并打印当前房间的邀请码。
    /// 邀请令牌只发给房主，因此非房主会看到失败提示（与 GUI 一致）。
    /// </summary>
    static async Task ShowInviteAsync()
    {
        var got = await _client.GetHostSettingsAsync();
        if (!got.Ok || got.Settings == null)
        {
            Print("⚠️ " + (string.IsNullOrEmpty(got.Message) ? "只有房主可以生成邀请码。" : got.Message));
            return;
        }

        var code = _client.CreateInviteCode(got.Settings.InviteToken);
        if (string.IsNullOrEmpty(code)) { Print("⚠️ 生成邀请码失败。"); return; }

        Print("\n──────── 邀请码（发给好友，对方粘贴后可直接进入本房间，无需输入密码）────────");
        Print(code);
        Print("提示：改房间密码不会让这个码失效；如需作废，用 host → 4 重置邀请令牌。");
        Print("──────────────────────────────────────────────────────");
    }

    /// <summary>房主设置文本菜单：最大人数 / 密码 / 邀请码 / 重置邀请码。</summary>
    static async Task HostSettingsMenuAsync()
    {
        var got = await _client.GetHostSettingsAsync();
        if (!got.Ok || got.Settings == null)
        {
            Print("⚠️ " + (string.IsNullOrEmpty(got.Message) ? "只有房主可以修改房间设置。" : got.Message));
            return;
        }

        var st = got.Settings;
        Print($@"
──────── 房主设置（房间 {st.RoomId} · {st.RoomName}）────────
  1) 最大人数：{st.MaxPlayers}（允许 {st.MinPlayers}-{st.MaxPlayersLimit}）{(st.Running ? " · 游戏进行中不可修改" : "")}
  2) 房间密码：{(st.HasPassword ? "已设置" : "公开房间")}
  3) 生成邀请码
  4) 重置邀请码（让旧码立即失效）
  0) 返回
提示：凭邀请码进房不需要密码，因此改密码不会让已发出的邀请码失效。");
        Console.Write("请选择（0-4）：");
        var pick = (Console.ReadLine() ?? "").Trim();

        switch (pick)
        {
            case "1":
                Console.Write($"新的最大人数（{st.MinPlayers}-{st.MaxPlayersLimit}）：");
                if (int.TryParse(Console.ReadLine(), out var max))
                {
                    var r = await _client.UpdateRoomSettingsAsync(max, null, false);
                    Print(r.Ok ? $"✅ 最大人数已设为 {max}。" : "⚠️ " + r.Message);
                }
                else Print("⚠️ 请输入数字。");
                break;

            case "2":
                Console.Write("新密码（回车保持不变；输入单个 - 表示清除密码、改为公开房间）：");
                var pwd = ReadPassword();
                if (pwd == "-")
                {
                    var r0 = await _client.UpdateRoomSettingsAsync(null, null, true);
                    Print(r0.Ok ? "✅ 已改为公开房间。" : "⚠️ " + r0.Message);
                }
                else if (pwd.Length > 0)
                {
                    var r1 = await _client.UpdateRoomSettingsAsync(null, pwd, false);
                    Print(r1.Ok ? "✅ 房间密码已更新（已发出的邀请码仍然有效）。" : "⚠️ " + r1.Message);
                }
                break;

            case "3":
                await ShowInviteAsync();
                break;

            case "4":
                var rot = await _client.ResetInviteAsync();
                Print(rot.Ok ? "✅ 已重置邀请令牌，旧的邀请码立即失效。" : "⚠️ " + rot.Message);
                break;
        }
    }

    static void RenderState(GameStateDto s)
    {
        var module = ConsoleGameRegistry.Resolve(s.GameId);
        lock (ConsoleLock)
        {
            Console.WriteLine();
            string roomHeader = string.IsNullOrEmpty(s.RoomId) ? "" : $" · 房间 {s.RoomId}";
            Console.WriteLine($"┌─────────── {module.RoomHeader(s)}{roomHeader} ───────────");
            Console.WriteLine(module.RenderState(s));
            Console.WriteLine("└──────────────────────────────────────");
        }
    }

    static void PrintHelp()
    {
        Print(@"
──────────── 指令帮助 ────────────
  start              房主开始游戏
  invite             生成邀请码（好友粘贴后可直接进本房间，含密码）
  host               房主设置（最大人数 / 密码 / 邀请码 / 重置邀请码）
  leave              退出当前房间，返回房间列表
                       （房主且房间只剩你时，会直接关闭该房间）
  chat 内容          发言
  help               显示本帮助");

        // 追加当前游戏的私有指令帮助。
        var extra = ConsoleGameRegistry.Resolve(_state?.GameId).HelpText;
        if (!string.IsNullOrWhiteSpace(extra)) Print(extra);

        Print(@"
──────────── 房间列表 ────────────
  序号 (如 2)        加入对应房间（加密房会要求输入密码）
  j                  用邀请码加入（无需再输密码）
  c                  创建房间（可选择游戏，可设置密码）
  r                  刷新列表
  q                  退出程序

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
