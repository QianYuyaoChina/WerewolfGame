using System.Security.Cryptography;

namespace Werewolf.Server;

/// <summary>
/// 房间号生成器：5 位可读大写字母数字码。
/// 字符集刻意排除易混淆的 <c>0/O/1/I/L</c>（剩 31 个字符），降低口头/手抄出错概率。
/// 与存活房间冲突时重试，超过上限抛 <see cref="InvalidOperationException"/>。
/// </summary>
public static class RoomId
{
    /// <summary>可用字符集（31 个）：大写字母（去掉 I、L、O）+ 数字（去掉 0、1）。</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>房间号长度。</summary>
    public const int Length = 5;

    /// <summary>冲突重试上限；超过则视为“无法分配”，交由上层提示稍后再试。</summary>
    public const int MaxAttempts = 32;

    /// <summary>
    /// 生成一个不与现存房间冲突的房间号。
    /// </summary>
    /// <param name="exists">判定候选房间号是否已被占用（返回 true 表示冲突）。</param>
    /// <exception cref="InvalidOperationException">连续重试仍无法生成唯一房间号时抛出。</exception>
    public static string Generate(Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        for (var i = 0; i < MaxAttempts; i++)
        {
            var candidate = Create();
            if (!exists(candidate)) return candidate;
        }

        throw new InvalidOperationException("无法生成唯一房间号，请稍后重试。");
    }

    /// <summary>用加密随机数生成一个 5 位候选房间号（不保证唯一）。</summary>
    private static string Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(Length);
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return new string(chars);
    }
}
