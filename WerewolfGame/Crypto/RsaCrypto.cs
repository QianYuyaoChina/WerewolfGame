using System.Numerics;
using System.Security.Cryptography;
using Werewolf.Client.Share.Crypto;

namespace Werewolf.Server.Crypto;

/// <summary>一对 RSA 密钥（模数与指数均为十进制字符串，便于持久化/传输）。</summary>
public readonly record struct RsaKeyPair(RsaPublicKey Public, RsaPrivateKey Private);

/// <summary>RSA 公钥：(n, e)。</summary>
public readonly record struct RsaPublicKey(BigInteger Modulus, BigInteger Exponent)
{
    public override string ToString() => $"{Modulus}:{Exponent}";
}

/// <summary>RSA 私钥：(n, d)。</summary>
public readonly record struct RsaPrivateKey(BigInteger Modulus, BigInteger Exponent)
{
    public override string ToString() => $"{Modulus}:{Exponent}";
}

/// <summary>
/// 基于 <see cref="FastPower"/> 的 RSA 加解密实现。
/// 加密 <c>c = m^e mod n</c>、解密 <c>m = c^d mod n</c> 均走快速幂取模。
///
/// 说明：这是教学/演示性质的 RSA（未做 PKCS#1 填充），用于展示“快速幂”在
/// 公钥加密中的实际作用；生产环境请使用 <see cref="RSA"/> 与标准填充。
/// </summary>
public static class RsaCrypto
{
    /// <summary>生成一对 512 位 RSA 密钥。</summary>
    public static RsaKeyPair GenerateKeyPair()
    {
        using var rsa = RSA.Create(512);
        var p = rsa.ExportParameters(true);

        // 参数都以无符号大端表示（含前导 0），需要补正负号以还原正值。
        var n = ToPositive(p.Modulus!);
        var e = ToPositive(p.Exponent!);
        var d = ToPositive(p.D!);

        return new RsaKeyPair(new RsaPublicKey(n, e), new RsaPrivateKey(n, d));
    }

    /// <summary>用公钥加密明文块（明文须 &lt; n）。</summary>
    public static BigInteger Encrypt(BigInteger message, RsaPublicKey key)
    {
        if (message < 0 || message >= key.Modulus)
            throw new ArgumentOutOfRangeException(nameof(message), "明文必须满足 0 ≤ m < n。");
        return FastPower.ModPow(message, key.Exponent, key.Modulus);
    }

    /// <summary>用私钥解密回明文。</summary>
    public static BigInteger Decrypt(BigInteger cipher, RsaPrivateKey key)
    {
        if (cipher < 0 || cipher >= key.Modulus)
            throw new ArgumentOutOfRangeException(nameof(cipher), "密文必须满足 0 ≤ c < n。");
        return FastPower.ModPow(cipher, key.Exponent, key.Modulus);
    }

    /// <summary>把 RSA 参数的无符号大端字节数组还原为 <see cref="BigInteger"/>（保留正号）。</summary>
    private static BigInteger ToPositive(byte[] bytes)
        => new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
}
