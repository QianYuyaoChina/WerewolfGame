using System.Collections.Concurrent;
using Werewolf.Client.Share.Crypto;

namespace Werewolf.Server;

/// <summary>
/// 一次握手在“服务端已回送公钥、但客户端身份尚未确认”期间的中间状态。
/// 只有客户端回送正确的 HMAC 后，才会被提升为真正的会话（见 <see cref="CryptoSessionStore"/>）。
/// </summary>
public sealed class PendingSession
{
    /// <summary>已派生好的会话密钥（AES-GCM）。</summary>
    public required byte[] SessionKey { get; init; }

    /// <summary>校验密钥（HMAC），用于验证客户端的身份证明。</summary>
    public required byte[] VerifyKey { get; init; }

    /// <summary>客户端应签名的握手记录。</summary>
    public required byte[] ClientTranscript { get; init; }
}

/// <summary>
/// 按 SignalR 连接保存“已协商并认证好的会话密钥”。
/// 握手时先写入 pending，客户端回送身份证明后再提升为正式会话；
/// 推送时取出用于加密，断开时一并移除。
/// </summary>
public sealed class CryptoSessionStore
{
    private readonly ConcurrentDictionary<string, SessionCipher> _sessions = new();
    private readonly ConcurrentDictionary<string, PendingSession> _pending = new();

    /// <summary>
    /// 已建立会话的连接数（即「已连接玩家数」）。
    /// 供内置服务器在**进程内直接查询**运行信息用 —— 不新增任何协议字段。
    /// </summary>
    public int Count => _sessions.Count;

    public void Set(string connectionId, SessionCipher cipher) => _sessions[connectionId] = cipher;

    public SessionCipher? Get(string connectionId)
        => _sessions.TryGetValue(connectionId, out var cipher) ? cipher : null;

    public void SetPending(string connectionId, PendingSession pending) => _pending[connectionId] = pending;

    public PendingSession? GetPending(string connectionId)
        => _pending.TryGetValue(connectionId, out var pending) ? pending : null;

    public void Remove(string connectionId)
    {
        _sessions.TryRemove(connectionId, out _);
        _pending.TryRemove(connectionId, out _);
    }

    /// <summary>身份证明通过：把 pending 提升为正式会话，并清掉中间状态。</summary>
    public void Promote(string connectionId, PendingSession pending)
    {
        _sessions[connectionId] = SessionCipher.FromKey(pending.SessionKey);
        _pending.TryRemove(connectionId, out _);
    }
}
