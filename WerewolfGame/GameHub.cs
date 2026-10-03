using Microsoft.AspNetCore.SignalR;

namespace Werewolf.Server;

public class GameHub : Hub
{
    private readonly GameRoomService _service;
    private readonly AccountStore _accounts;

    public GameHub(GameRoomService service, AccountStore accounts)
    {
        _service = service;
        _accounts = accounts;
    }

    public async Task<object> Join(string username, string password)
    {
        username = (username ?? "").Trim();
        password = password ?? "";

        if (string.IsNullOrWhiteSpace(username))
            return new { ok = false, msg = "用户名不能为空" };
        if (username.Length > 20)
            return new { ok = false, msg = "用户名过长（最多 20 字符）" };
        if (password.Length < 3)
            return new { ok = false, msg = "密码至少 3 位" };

        // ---- 账号：首次即注册；否则校验 ----
        if (!_accounts.Exists(username))
        {
            var err = _accounts.Register(username, password);
            if (err != null) return new { ok = false, msg = err };
        }
        else
        {
            if (!_accounts.Verify(username, password))
                return new { ok = false, msg = "密码错误" };
        }

        // ---- 加入 / 重连房间 ----
        var room = _service.Room;
        var (ok, msg, player, reconnected) = room.AddOrReconnect(username, Context.ConnectionId);
        if (!ok || player == null) return new { ok = false, msg };

        if (reconnected)
        {
            await Clients.Others.SendAsync("Message", $"🔄 {username} 重新连接。");
            if (room.IsRunning)
                await room.ResendRoleAsync(player);   // 补发身份私聊
        }
        else
        {
            await Clients.Others.SendAsync("Message", $"👋 {username} 加入了房间。");
        }

        await room.BroadcastStateAsync();
        return new { ok = true, msg = reconnected ? "reconnected" : "joined" };
    }

    public async Task<object> Start()
    {
        var (ok, msg) = _service.Room.StartGame(Context.ConnectionId);
        if (!ok) await Clients.Caller.SendAsync("Message", "⚠️ " + msg);
        return new { ok, msg };
    }

    public Task NightAction(int targetId, string action)
    {
        _service.Room.SubmitNightAction(Context.ConnectionId, targetId, action);
        return Task.CompletedTask;
    }

    public Task Vote(int targetId)
    {
        _service.Room.SubmitVote(Context.ConnectionId, targetId);
        return Task.CompletedTask;
    }

    public Task HunterShot(int targetId)
    {
        _service.Room.SubmitHunterShot(Context.ConnectionId, targetId);
        return Task.CompletedTask;
    }

    public async Task Chat(string text)
    {
        var room = _service.Room;
        var p = room.GetPlayer(Context.ConnectionId);
        if (p == null || !p.IsAlive) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        await room.BroadcastAsync($"💬 {p.Username}：{text.Trim()}");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var room = _service.Room;
        var p = room.GetPlayer(Context.ConnectionId);
        room.OnDisconnected(Context.ConnectionId);

        if (p != null)
        {
            bool removed = !room.IsRunning;
            await room.BroadcastAsync(removed
                ? $"❌ {p.Username} 离开了房间。"
                : $"⚠️ {p.Username} 掉线了（可重新连接）。");
        }
        await room.BroadcastStateAsync();
        await base.OnDisconnectedAsync(exception);
    }
}