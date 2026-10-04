using System.Security.Cryptography;
using System.Text;

namespace Werewolf.Client.Share.Crypto;

/// <summary>
/// 握手用的预共享密钥（Pre-Shared Key，PSK）。
///
/// 仅靠 Diffie-Hellman 只能保证“密钥不被窃听”，却不能确认对端身份——
/// 中间人可以分别与两端各协商一把密钥而双方毫无察觉（MITM）。
/// 这里让 PSK 参与会话密钥派生（作为 HKDF 的 salt），
/// 于是“不掌握 PSK 的攻击者”根本无法算出同一把会话密钥，握手随即可被对方识破。
///
/// 客户端与服务端必须持有同一份 PSK：默认使用内置值，
/// 可用环境变量 <see cref="EnvironmentVariable"/> 覆盖（两端要保持一致）。
/// </summary>
public static class PreSharedKey
{
    /// <summary>用于覆盖内置密钥的环境变量名。</summary>
    public const string EnvironmentVariable = "WEREWOLF_PSK";

    /// <summary>内置的演示密钥；生产部署请务必用环境变量替换为自己的随机值。</summary>
    private const string BuiltIn = "werewolf-psk-v1::replace-me-via-WEREWOLF_PSK::9f2c4a";

    /// <summary>取得 PSK 的 32 字节摘要（先哈希，保证长度与分布均匀，可直接作 HKDF salt）。</summary>
    public static byte[] Resolve()
    {
        var text = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(text)) text = BuiltIn;
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }
}
