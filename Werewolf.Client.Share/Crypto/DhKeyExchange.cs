using System.Numerics;
using System.Security.Cryptography;

namespace Werewolf.Client.Share.Crypto;

/// <summary>
/// 基于快速幂的 Diffie-Hellman 密钥协商。
/// 本端私钥 a，公钥 A = g^a mod p；收到对端公钥 B 后共享密钥 S = B^a mod p。
/// 由模幂的可交换性，两端算出的 S 完全相同（S = g^(ab) mod p），
/// 从而在不传输密钥本身的前提下“约定”出同一把加密密钥。
/// </summary>
public sealed class DhKeyExchange
{
    private readonly BigInteger _privateKey;

    /// <summary>本端 DH 公钥（g^a mod p），用于发给对端。</summary>
    public BigInteger PublicValue { get; }

    /// <summary>使用的模数。</summary>
    public BigInteger Prime { get; }

    public DhKeyExchange(BigInteger? prime = null, int? generator = null)
    {
        Prime = prime ?? ModpGroup.Prime;
        var g = generator ?? ModpGroup.Generator;

        _privateKey = GeneratePrivateKey(Prime);
        PublicValue = FastPower.ModPow(g, _privateKey, Prime);
    }

    /// <summary>用对端公钥算出共享密钥 <c>peer^a mod p</c>。</summary>
    public BigInteger DeriveSharedSecret(BigInteger peerPublicValue)
    {
        if (peerPublicValue <= 1 || peerPublicValue >= Prime)
            throw new ArgumentOutOfRangeException(nameof(peerPublicValue), "对端公钥超出 (1, p) 合法范围。");

        return FastPower.ModPow(peerPublicValue, _privateKey, Prime);
    }

    /// <summary>取 [2, p-2] 的随机私钥（2048 位，使用密码学安全随机源）。</summary>
    private static BigInteger GeneratePrivateKey(BigInteger prime)
    {
        var bytes = new byte[prime.GetByteCount(isUnsigned: true)];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            bytes[0] |= 0x80;   // 保证位长足够，避免私钥过小
            var candidate = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            if (candidate >= 2 && candidate < prime - 1)
                return candidate;
        }
    }
}
