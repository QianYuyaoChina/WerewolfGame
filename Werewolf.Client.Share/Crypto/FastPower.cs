using System.Numerics;

namespace Werewolf.Client.Share.Crypto;

/// <summary>
/// 快速幂（fast exponentiation / binary exponentiation）。
/// 用“平方-乘法”把 <c>b^e mod m</c> 的复杂度从 O(e) 降到 O(log e)，
/// 是 RSA / Diffie-Hellman 等公钥加密的核心算子。
/// </summary>
public static class FastPower
{
    /// <summary>
    /// 计算 <c>(baseValue ^ exponent) mod modulus</c>。
    /// 迭代实现（避免递归栈），对指数按二进制位逐位平方相乘。
    /// </summary>
    /// <param name="baseValue">底数。</param>
    /// <param name="exponent">指数（要求 ≥ 0）。</param>
    /// <param name="modulus">模数（要求 &gt; 0）。</param>
    public static BigInteger ModPow(BigInteger baseValue, BigInteger exponent, BigInteger modulus)
    {
        if (modulus <= 0) throw new ArgumentOutOfRangeException(nameof(modulus), "模数必须大于 0。");
        if (exponent < 0) throw new ArgumentOutOfRangeException(nameof(exponent), "指数必须非负。");

        // 统一把底数规约到 [0, modulus)，避免负数与大数影响正确性。
        BigInteger result = 1;
        BigInteger b = baseValue % modulus;
        BigInteger e = exponent;

        while (e > 0)
        {
            // 当前最低位为 1 时乘进结果。
            if ((e & 1) == 1)
                result = result * b % modulus;

            // 底数平方（对应指数右移一位）。
            b = b * b % modulus;
            e >>= 1;
        }

        return result;
    }

    /// <summary>便捷重载：接受 <see cref="long"/>，内部转为 <see cref="BigInteger"/>。</summary>
    public static BigInteger ModPow(long baseValue, long exponent, long modulus)
        => ModPow(new BigInteger(baseValue), new BigInteger(exponent), new BigInteger(modulus));
}
