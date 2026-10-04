using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Werewolf.Client.Share.Crypto;
using Werewolf.Server.Crypto;

namespace Werewolf.Server;

/// <summary>
/// 房间密码的单向哈希：带房间号盐（同一密码在不同房间得到不同存储值），
/// 复用 <see cref="AccountStore"/> 的风格（SHA-256 + 快速幂混淆 + HMAC，<c>v2:</c> 前缀）。
/// 仅内存态，不落盘；任何日志/推送/列表接口都不得出现明文或哈希。
/// </summary>
public static class RoomPassword
{
    /// <summary>哈希前缀，标识当前算法版本（便于将来升级且保持向后兼容）。</summary>
    public const string Prefix = "v2:";

    /// <summary>计算房间密码的存储值（形如 <c>v2:HEX</c>）。</summary>
    public static string Hash(string roomId, string password)
    {
        roomId ??= "";
        password ??= "";

        var salt = $"werewolf::room::{roomId}::{password}";

        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(salt));

        // 用“房间号的 SHA-256 前 8 字节”作为快速幂加密的明文种子，保持确定性。
        using var seedSha = SHA256.Create();
        var seedBytes = seedSha.ComputeHash(Encoding.UTF8.GetBytes(roomId));
        var message = new BigInteger(seedBytes.AsSpan(0, 8), isUnsigned: true, isBigEndian: true);

        var key = DemoKey;
        var encrypted = FastPower.ModPow(message, key.Exponent, key.Modulus);

        var hmac = sha.ComputeHash(Encoding.UTF8.GetBytes(
            $"werewolf::room::{roomId}::{Convert.ToHexString(digest)}::{encrypted}"));

        return Prefix + Convert.ToHexString(hmac);
    }

    /// <summary>校验密码是否与存储值匹配（存储值缺失时一律不通过）。</summary>
    public static bool Verify(string roomId, string? password, string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;

        var expected = hash;
        var actual = Hash(roomId, password ?? "");

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>演示用固定 RSA 参数（小规模，仅用于给密码摘要增加一层确定性混淆，与 AccountStore 一致）。</summary>
    private static readonly RsaPublicKey DemoKey = new(
        BigInteger.Parse("3231700607131100730071487668866995196044410266971548403213034542752465513886789089319720141152291346368871796092189801949411955915049092109508815238644828312063087736730099609175019775038965210679605763838406756827679221864261975616183809433847617047058164585203630504288757589154106580860755239912393038552191433338966830242067472924073576538580936380938665487909118268402880591745805406647783506268113973840868660043773677495521581639470720031314818266131221652014434204672933217604623578584927396043314210583444666416481852812070326938444333630825817711108143494363751734143566258253385155050454825651774055520371"),
        BigInteger.Parse("65537"));
}
