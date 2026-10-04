using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Werewolf.Client.Share.Crypto;

/// <summary>
/// 会话加密器：把握手派生出的 256 位会话密钥用于 AES-GCM（带认证）加解密。
/// 密文线格式：Base64( nonce(12) | tag(16) | ciphertext )。
/// </summary>
public sealed class SessionCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    private SessionCipher(byte[] key) => _key = key;

    /// <summary>直接用已派生好的会话密钥构造（握手流程用，密钥已由 <see cref="HandshakeCrypto"/> 派生）。</summary>
    public static SessionCipher FromKey(byte[] key)
    {
        if (key is null || key.Length != 32)
            throw new ArgumentException("会话密钥必须是 32 字节。", nameof(key));
        return new SessionCipher((byte[])key.Clone());
    }

    /// <summary>
    /// 仅用 DH 共享密钥派生会话密钥（等价于“无 nonce、使用默认 PSK”的握手）。
    /// 真正的连接走 <see cref="HandshakeCrypto"/> 的认证派生；此重载保留给单元校验与简单场景。
    /// </summary>
    public static SessionCipher FromSharedSecret(BigInteger sharedSecret)
    {
        var keys = HandshakeCrypto.DeriveKeys(
            sharedSecret, Array.Empty<byte>(), Array.Empty<byte>(), PreSharedKey.Resolve());
        return new SessionCipher(keys.SessionKey);
    }

    /// <summary>密钥指纹（仅用于展示/自检，不泄露密钥本身）。</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_key))[..16];

    /// <summary>加密一段文本，返回可安全放在信封里的 Base64。</summary>
    public string Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var gcm = new AesGcm(_key, TagSize))
            gcm.Encrypt(nonce, plain, cipher, tag);

        var buffer = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, buffer, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, buffer, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, buffer, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(buffer);
    }

    /// <summary>解密信封内容；密文被篡改或密钥不匹配时抛 <see cref="CryptographicException"/>。</summary>
    public string Decrypt(string payload)
    {
        var buffer = Convert.FromBase64String(payload);
        if (buffer.Length < NonceSize + TagSize)
            throw new CryptographicException("密文长度无效。");

        var nonce = buffer.AsSpan(0, NonceSize);
        var tag = buffer.AsSpan(NonceSize, TagSize);
        var cipher = buffer.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using (var gcm = new AesGcm(_key, TagSize))
            gcm.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}
