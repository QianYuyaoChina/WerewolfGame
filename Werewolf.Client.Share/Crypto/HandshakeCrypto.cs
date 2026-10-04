using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Werewolf.Client.Share.Crypto;

/// <summary>
/// 握手期的密钥派生与身份校验（PSK 认证的 Diffie-Hellman）。
///
/// 流程：
/// <list type="number">
///   <item>双方交换 DH 公钥与各自随机 nonce；</item>
///   <item>各自用 <c>HKDF(DH共享密钥 ‖ 双方nonce, salt = PSK)</c> 派生 64 字节，
///         前 32 字节作会话密钥，后 32 字节作校验密钥；</item>
///   <item>双方互相对“握手记录（transcript）”做 HMAC，验证对方确实掌握 PSK，
///         即完成双向身份认证。</item>
/// </list>
/// 由于 PSK 只参与本地推导、从不传输，中间人无论怎样替换公钥都无法伪造校验值。
/// </summary>
public static class HandshakeCrypto
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("werewolf-auth-v1");

    /// <summary>派生结果：会话密钥（AES-GCM）与校验密钥（HMAC）。</summary>
    public sealed record SessionKeys(byte[] SessionKey, byte[] VerifyKey);

    /// <summary>由 DH 共享密钥 + 双方 nonce + PSK 派生会话密钥与校验密钥。</summary>
    public static SessionKeys DeriveKeys(
        BigInteger sharedSecret, byte[] clientNonce, byte[] serverNonce, byte[] preSharedKey)
    {
        var ikm = Concat(BigIntToBytes(sharedSecret), clientNonce, serverNonce);
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 64, salt: preSharedKey, info: Info);
        return new SessionKeys(okm[..32], okm[32..]);
    }

    /// <summary>
    /// 构造待签名的握手记录：把方向标签、双方公钥、双方 nonce 按固定长度前缀拼在一起，
    /// 任一元素被改动都会得到完全不同的 HMAC，从而无法被拼接歧义钻空子。
    /// </summary>
    public static byte[] Transcript(
        string label, BigInteger clientPublic, BigInteger serverPublic,
        byte[] clientNonce, byte[] serverNonce)
        => Concat(
            Encoding.UTF8.GetBytes(label),
            BigIntToBytes(clientPublic),
            BigIntToBytes(serverPublic),
            clientNonce,
            serverNonce);

    /// <summary>对握手记录生成校验值（Base64）。</summary>
    public static string Proof(byte[] verifyKey, byte[] transcript)
        => Convert.ToBase64String(HMACSHA256.HashData(verifyKey, transcript));

    /// <summary>常量时间校验对方的握手记录签名。</summary>
    public static bool VerifyProof(byte[] verifyKey, byte[] transcript, string proofBase64)
    {
        byte[] given;
        try { given = Convert.FromBase64String(proofBase64); }
        catch { return false; }

        var expected = HMACSHA256.HashData(verifyKey, transcript);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private static byte[] BigIntToBytes(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        return bytes.Length == 0 ? new byte[] { 0 } : bytes;
    }

    /// <summary>把多段字节按「4 字节大端长度 + 内容」拼接，避免不同切分方式产生相同结果。</summary>
    private static byte[] Concat(params byte[][] parts)
    {
        var total = 4 * parts.Length + parts.Sum(p => p.Length);
        var buffer = new byte[total];
        var offset = 0;

        foreach (var part in parts)
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), part.Length);
            offset += 4;
            part.CopyTo(buffer, offset);
            offset += part.Length;
        }

        return buffer;
    }
}
