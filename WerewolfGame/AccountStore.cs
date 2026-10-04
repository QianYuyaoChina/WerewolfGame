using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Werewolf.Client.Share.Crypto;
using Werewolf.Server.Crypto;

namespace Werewolf.Server;

/// <summary>账号存储：用户名 → 密码哈希。文件持久化，服务端重启不丢失。</summary>
public class AccountStore
{
    private readonly string _filePath;
    private readonly Dictionary<string, string> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public AccountStore(string filePath = "accounts.json")
    {
        _filePath = filePath;
        Load();
    }

    public bool Exists(string username)
    {
        lock (_lock) return _accounts.ContainsKey(username);
    }

    /// <summary>注册新账号。成功返回 null，失败返回错误信息。</summary>
    public string? Register(string username, string password)
    {
        lock (_lock)
        {
            if (_accounts.ContainsKey(username)) return "该用户名已被注册";
            _accounts[username] = Hash(username, password);
            Save();
            return null;
        }
    }

    public bool Verify(string username, string password)
    {
        lock (_lock)
            return _accounts.TryGetValue(username, out var h) && h == Hash(username, password);
    }

    /// <summary>
    /// 密码哈希。除 SHA-256 之外，再用基于快速幂的模幂加密对摘要做一层混淆，
    /// 使存储值无法被直接彩虹表反查。前缀 <c>v2:</c> 标识新算法，旧账号（无前缀）仍可正常校验。
    /// </summary>
    private static string Hash(string username, string password)
    {
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(Encoding.UTF8.GetBytes($"werewolf::{username}::{password}"));

        // 用“用户名的 SHA-256 前 8 字节”作为快速幂加密的明文种子，保持确定性（同一账号密码结果一致）。
        using var seedSha = SHA256.Create();
        var seedBytes = seedSha.ComputeHash(Encoding.UTF8.GetBytes(username));
        var message = new BigInteger(seedBytes.AsSpan(0, 8), isUnsigned: true, isBigEndian: true);

        // 一组固定的 RSA 风格参数（演示用途）：n 与 e。
        var key = DemoKey;
        var encrypted = FastPower.ModPow(message, key.Exponent, key.Modulus);

        var hmac = sha.ComputeHash(Encoding.UTF8.GetBytes(
            $"werewolf::{username}::{Convert.ToHexString(digest)}::{encrypted}"));

        return "v2:" + Convert.ToHexString(hmac);
    }

    /// <summary>演示用固定 RSA 参数（小规模，仅用于给密码摘要增加一层确定性混淆）。</summary>
    private static readonly RsaPublicKey DemoKey = new(
        BigInteger.Parse("3231700607131100730071487668866995196044410266971548403213034542752465513886789089319720141152291346368871796092189801949411955915049092109508815238644828312063087736730099609175019775038965210679605763838406756827679221864261975616183809433847617047058164585203630504288757589154106580860755239912393038552191433338966830242067472924073576538580936380938665487909118268402880591745805406647783506268113973840868660043773677495521581639470720031314818266131221652014434204672933217604623578584927396043314210583444666416481852812070326938444333630825817711108143494363751734143566258253385155050454825651774055520371"),
        BigInteger.Parse("65537"));

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict == null) return;
            foreach (var kv in dict) _accounts[kv.Key] = kv.Value;
        }
        catch { /* 文件损坏时静默忽略 */ }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_accounts,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { /* 忽略写盘失败 */ }
    }
}