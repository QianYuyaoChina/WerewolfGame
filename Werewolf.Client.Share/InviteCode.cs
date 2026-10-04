using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Werewolf.Client.Share.Crypto;

namespace Werewolf.Client.Share;

/// <summary>邀请码携带的信息：连哪台服务器、进哪个房间、房间密码（如有）。</summary>
public sealed class InvitePayload
{
    /// <summary>服务器地址（完整 Hub 地址，如 <c>http://192.168.1.8:5000/gamehub</c>）。</summary>
    public string Url { get; set; } = "";

    /// <summary>房间号（5 位）。</summary>
    public string RoomId { get; set; } = "";

    /// <summary>房间密码；公开房间为 null。</summary>
    public string? Password { get; set; }

    /// <summary>房间名（仅用于提示展示）。</summary>
    public string? RoomName { get; set; }

    /// <summary>是否携带房间密码。</summary>
    public bool HasPassword => !string.IsNullOrEmpty(Password);
}

/// <summary>
/// 邀请码：把「服务器地址 + 房间号 + 房间密码」打包后**对称加密**成一段可复制的字符串，
/// 接收方客户端解析后自动带上密码进房，无需手动输入密码。
///
/// 加密密钥由预共享密钥（<see cref="PreSharedKey"/>）派生，因此：
/// <list type="bullet">
///   <item>邀请码天然只在同一部署内互通（换了 <c>WEREWOLF_PSK</c> 的服务器无法解码）；</item>
///   <item>码内**不含账号信息**——拿到码的人仍需用自己的账号登录，房主身份不会被人冒领。</item>
/// </list>
/// 线格式：<c>WW1:</c> + Base64Url( nonce(12) | tag(16) | ciphertext )。
/// </summary>
public static class InviteCode
{
    /// <summary>邀请码前缀（便于识别与版本演进）。</summary>
    public const string Prefix = "WW1:";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("werewolf-invite-v1");

    /// <summary>生成邀请码。</summary>
    public static string Create(string url, string roomId, string? password, string? roomName)
    {
        var payload = new InvitePayload
        {
            Url = (url ?? "").Trim(),
            RoomId = (roomId ?? "").Trim(),
            Password = string.IsNullOrEmpty(password) ? null : password,
            RoomName = string.IsNullOrEmpty(roomName) ? null : roomName
        };

        var cipher = InviteCipher();
        return Prefix + ToBase64Url(cipher.Encrypt(JsonSerializer.Serialize(payload, Json)));
    }

    /// <summary>判断一段文本是否形如邀请码（用于控制台「直接粘贴邀请码当地址」的判定）。</summary>
    public static bool LooksLikeCode(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && text.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>解析邀请码；失败时返回 false 并给出可直接展示给用户的原因。</summary>
    public static bool TryParse(string? code, out InvitePayload? payload, out string error)
    {
        payload = null;
        error = "";

        var text = (code ?? "").Trim();
        if (text.Length == 0)
        {
            error = "请先输入邀请码。";
            return false;
        }

        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"邀请码格式不正确（应以 {Prefix} 开头）。";
            return false;
        }

        var body = text[Prefix.Length..].Trim();
        if (body.Length == 0)
        {
            error = "邀请码内容为空。";
            return false;
        }

        // 解密失败 = 内容被改动，或来自使用不同预共享密钥的服务器。
        string json;
        try
        {
            json = InviteCipher().Decrypt(FromBase64Url(body));
        }
        catch
        {
            error = "邀请码无效或已损坏（也可能来自其他服务器）。";
            return false;
        }

        InvitePayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<InvitePayload>(json, Json);
        }
        catch
        {
            error = "邀请码内容无法识别。";
            return false;
        }

        if (parsed == null || string.IsNullOrWhiteSpace(parsed.RoomId))
        {
            error = "邀请码内容不完整。";
            return false;
        }

        payload = parsed;
        return true;
    }

    /// <summary>邀请码专用密钥：由预共享密钥派生（info 与握手密钥区分，互不干扰）。</summary>
    private static SessionCipher InviteCipher()
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, PreSharedKey.Resolve(), 32, salt: null, info: Info);
        return SessionCipher.FromKey(key);
    }

    // ---- Base64Url：避免 '+' '/' '=' 在聊天工具/命令行里被转义或截断 ----

    private static string ToBase64Url(string base64)
        => base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string FromBase64Url(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        return (text.Length % 4) switch
        {
            2 => text + "==",
            3 => text + "=",
            _ => text
        };
    }
}
