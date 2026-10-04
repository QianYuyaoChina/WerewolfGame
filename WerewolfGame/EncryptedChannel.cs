using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Werewolf.Client.Share;

namespace Werewolf.Server;

/// <summary>
/// 服务器出站通道：把广播 / 私聊 / 状态推送统一封装成加密信封后发给客户端。
/// 之前各处直接 <c>SendAsync("Message"|"State", …)</c>，现在集中在这里做序列化与加密，
/// 业务代码（<see cref="GameRoom"/>）不再关心加密细节。
/// </summary>
public sealed class EncryptedChannel
{
    private static readonly JsonSerializerOptions Json = new();

    private readonly IHubContext<GameHub> _hub;
    private readonly CryptoSessionStore _sessions;

    public EncryptedChannel(IHubContext<GameHub> hub, CryptoSessionStore sessions)
    {
        _hub = hub;
        _sessions = sessions;
    }

    /// <summary>推送一条文本消息（私聊 / 广播通用）。</summary>
    public Task SendMessageAsync(string connectionId, string text)
        => SendAsync(connectionId, PushTypes.Message,
            JsonSerializer.SerializeToNode(new MessagePayload { Text = text }, Json));

    /// <summary>推送一份该玩家视角的游戏状态。</summary>
    public Task SendStateAsync(string connectionId, GameStateDto state)
        => SendAsync(connectionId, PushTypes.State, JsonSerializer.SerializeToNode(state, Json));

    /// <summary>加密并发送推送；未完成密钥协商的连接会被安全跳过。</summary>
    public async Task SendAsync(string connectionId, string type, JsonNode? payload)
    {
        if (string.IsNullOrEmpty(connectionId)) return;

        var cipher = _sessions.Get(connectionId);
        if (cipher == null) return;   // 尚未握手，无法加密 → 丢弃

        var message = new PushMessage { Type = type, Payload = payload };
        var envelope = cipher.Encrypt(JsonSerializer.Serialize(message, Json));

        try { await _hub.Clients.Client(connectionId).SendAsync("Push", envelope); }
        catch { /* 忽略单个客户端异常 */ }
    }
}
