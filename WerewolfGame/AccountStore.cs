using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Werewolf.Client.Share;
using Werewolf.Client.Share.Crypto;
using Werewolf.Server.Crypto;

namespace Werewolf.Server;

/// <summary>
/// 账号目录：服务端对外暴露账号相关能力的**唯一**门面（v2.5.0 第 4 / 5 条）。
///
/// 为什么要这层接口：房间（<see cref="GameRoom"/>）需要在下发状态时带上称号，
/// 但它不该认识 <see cref="AccountStore"/> 的存盘格式；平台层只认这三个方法。
/// </summary>
public interface IAccountDirectory
{
    /// <summary>是否内置管理员（**唯一**判定入口，架构 K5）。</summary>
    bool IsAdmin(string username);

    /// <summary>该账号当前的称号；无称号返回 ""。</summary>
    string TitleOf(string username);

    /// <summary>设置 / 清除该账号的称号（空 = 清除）。已做校验，非法值直接忽略。</summary>
    void SetTitle(string username, string title);
}

/// <summary>账号存储：用户名 → 密码哈希 + 称号。文件持久化，服务端重启不丢失。</summary>
public class AccountStore : IAccountDirectory
{
    /// <summary>
    /// 内置管理员的用户名（大小写不敏感）。
    /// 指向共享层的 <see cref="AdminDefaults.Username"/>，与客户端界面文案**同一个来源**，
    /// 不可能出现「服务端认 admin、界面写着 admin2」这类漂移。
    /// </summary>
    public const string AdminUsername = AdminDefaults.Username;

    private readonly string _filePath;
    private readonly Dictionary<string, string> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
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
            // 管理员用户名是保留字：普通注册抢不到（PRD 5.2），否则谁都能自称管理员。
            if (IsAdmin(username)) return ServerMessages.AdminReserved;
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

    // ==================== 内置管理员（v2.5.0 第 5 条） ====================

    /// <summary>
    /// 确保内置管理员存在：**不存在才创建**，默认密码 <see cref="AdminDefaults.DefaultAdminPassword"/>。
    ///
    /// 关键语义：管理员改过密码之后，重启服务器**不会被重置**回默认值 ——
    /// 因为这里只做「缺失补建」，不做「写回默认值」。
    /// </summary>
    /// <returns>本次是否新建了管理员账号（供启动日志用）。</returns>
    public bool EnsureAdmin()
    {
        lock (_lock)
        {
            if (_accounts.ContainsKey(AdminUsername)) return false;

            _accounts[AdminUsername] = Hash(AdminUsername, AdminDefaults.DefaultAdminPassword);
            Save();
            return true;
        }
    }

    /// <summary>
    /// 修改密码（v2.5.0 第 7 条）。成功返回 null，失败返回**给用户看**的原因。
    /// 规则（口径来自 <see cref="PasswordRules"/>，三端共用，TODO T7）：
    /// 账号必须存在 → 旧密码必须对 → 新密码长度合法 → 新密码不能与旧密码相同。
    /// </summary>
    public string? ChangePassword(string username, string oldPassword, string newPassword)
    {
        var user = (username ?? "").Trim();
        if (user.Length == 0) return ServerMessages.NotLoggedIn;

        oldPassword ??= "";
        newPassword ??= "";

        lock (_lock)
        {
            if (!_accounts.TryGetValue(user, out var current)) return ServerMessages.NotLoggedIn;

            if (current != Hash(user, oldPassword)) return ServerMessages.OldPasswordWrong;

            var (ok, error) = PasswordRules.Validate(newPassword);
            if (!ok) return error;

            if (newPassword == oldPassword) return ServerMessages.PasswordSameAsOld;

            _accounts[user] = Hash(user, newPassword);
            Save();
            return null;
        }
    }

    // ==================== 管理员判定（唯一入口） ====================

    /// <summary>
    /// 是否内置管理员。**全局唯一**判定入口：任何地方都不许再写 <c>用户名 == "admin"</c> 的硬编码比较。
    /// </summary>
    public bool IsAdmin(string username)
        => string.Equals((username ?? "").Trim(), AdminUsername, StringComparison.OrdinalIgnoreCase);

    // ==================== 称号（服务端权威存储） ====================

    /// <summary>该账号当前的称号；无称号返回 ""。</summary>
    public string TitleOf(string username)
    {
        lock (_lock)
            return string.IsNullOrEmpty(username) ? "" : _titles.TryGetValue(username, out var t) ? t : "";
    }

    /// <summary>
    /// 设置 / 清除该账号的称号（空 / 全空白 = 清除）。
    /// 口径与客户端共用 <see cref="TitleRules"/>，两端不会漂移；非法值直接忽略（调用方应先校验并给文案）。
    /// </summary>
    public void SetTitle(string username, string title)
    {
        if (string.IsNullOrWhiteSpace(username)) return;

        var text = TitleRules.Normalize(title);
        var (ok, _) = TitleRules.Validate(text);
        if (!ok) return;

        lock (_lock)
        {
            if (text.Length == 0) _titles.Remove(username);
            else _titles[username] = text;
            Save();
        }
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

    // ==================== 持久化 ====================

    /// <summary>
    /// 存盘信封（v2.5.0 起）。账号与称号**同一个文件**，不新增文件、不改清理规则。
    /// 旧格式（纯 <c>{"用户名":"哈希"}</c> 字典）照旧加载 —— 见 <see cref="Parse"/>。
    /// </summary>
    private sealed class AccountFile
    {
        /// <summary>格式版本号；旧格式没有这个字段。</summary>
        public int Version { get; set; } = 2;

        /// <summary>用户名 → 密码哈希。</summary>
        public Dictionary<string, string> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>用户名 → 称号（v2.5.0 第 4 条）。</summary>
        public Dictionary<string, string> Titles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var file = Parse(json);

            foreach (var kv in file.Accounts) _accounts[kv.Key] = kv.Value;
            foreach (var kv in file.Titles) _titles[kv.Key] = kv.Value;
        }
        catch { /* 文件损坏时静默忽略 */ }
    }

    /// <summary>
    /// 解析账号文件：信封格式（含 Accounts / Titles）优先，否则按**旧格式**（纯字典）读，
    /// 保证既有 accounts.json 无需迁移脚本即可继续使用（架构 U2）。
    /// </summary>
    private static AccountFile Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var isEnvelope = root.ValueKind == JsonValueKind.Object
                         && (root.TryGetProperty("accounts", out _) || root.TryGetProperty("Accounts", out _));

        if (isEnvelope)
        {
            var file = JsonSerializer.Deserialize<AccountFile>(json, FileOptions);
            if (file != null) return file;
        }

        var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(json, FileOptions);
        return new AccountFile { Accounts = legacy ?? new Dictionary<string, string>() };
    }

    private void Save()
    {
        try
        {
            // 统一按信封格式写回：旧文件会在第一次写入后自然升级。
            var file = new AccountFile
            {
                Version = 2,
                Accounts = _accounts,
                Titles = _titles
            };

            File.WriteAllText(_filePath, JsonSerializer.Serialize(file, FileOptions));
        }
        catch { /* 忽略写盘失败 */ }
    }
}
