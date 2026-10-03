using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    private static string Hash(string username, string password)
    {
        // 用户名作为盐，避免不同用户相同密码产生相同哈希
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"werewolf::{username}::{password}"));
        return Convert.ToHexString(bytes);
    }

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