using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Werewolf.Client.Share.Crypto;
using Werewolf.Server;
using Werewolf.Server.Crypto;

namespace ClientGUI.Tests;

/// <summary>快速幂加密算法、DH 会话密钥、PSK 握手认证与账号存储的校验。</summary>
public static class CryptoTests
{
    public static void RunAll()
    {
        FastPowerCorrectness();
        FastPowerEdgeCases();
        RsaRoundTrip();
        RsaEncryptThenDecryptDifferentKey();
        DhKeyAgreement();
        SessionCipherRoundTrip();
        SessionCipherRejectsTamper();
        HandshakeAuthentication();
        AccountHashStability();
    }

    // ==================== 快速幂正确性 ====================

    private static void FastPowerCorrectness()
    {
        // 与 BigInteger.ModPow 对照（后者是标准库实现，作为可信基线）。
        var rng = new Random(20261003);
        for (int i = 0; i < 200; i++)
        {
            long b = rng.Next(1, 10_000);
            long e = rng.Next(0, 20);
            long m = rng.Next(2, 1_000_000);
            var expected = BigInteger.ModPow(b, e, m);
            var actual = FastPower.ModPow(b, e, m);
            Check.Equal(expected, actual, $"快速幂：{b}^{e} mod {m}");
        }
    }

    private static void FastPowerEdgeCases()
    {
        Check.Equal(BigInteger.One, FastPower.ModPow(7, 0, 13), "快速幂：任何数的 0 次幂为 1");
        Check.Equal(BigInteger.Zero, FastPower.ModPow(0, 5, 7), "快速幂：0 的幂为 0");
        Check.Equal(new BigInteger(1), FastPower.ModPow(2, 10, 11), "快速幂：2^10 mod 11 = 1");
    }

    // ==================== RSA 加解密 ====================

    private static void RsaRoundTrip()
    {
        var (pub, priv) = RsaCrypto.GenerateKeyPair();

        // 用“用户名摘要”作为明文，模拟加密任意消息块。
        var message = new BigInteger(123456789);
        if (message >= pub.Modulus)
            message = pub.Modulus - 1;

        var cipher = RsaCrypto.Encrypt(message, pub);
        Check.NotEqual(message, cipher, "RSA：加密结果不等于明文");
        Check.Equal(message, RsaCrypto.Decrypt(cipher, priv), "RSA：加解密往返一致");
    }

    private static void RsaEncryptThenDecryptDifferentKey()
    {
        var (pub, _) = RsaCrypto.GenerateKeyPair();

        // 不同明文应产生不同密文（加密真正依赖输入）。
        var m1 = new BigInteger(12345);
        var m2 = new BigInteger(54321);
        var c1 = RsaCrypto.Encrypt(m1, pub);
        var c2 = RsaCrypto.Encrypt(m2, pub);
        Check.NotEqual(c1, c2, "RSA：不同明文产生不同密文");

        // 同一明文两次加密结果一致（确定性，RSA 明文加密不含随机填充）。
        Check.Equal(c1, RsaCrypto.Encrypt(m1, pub), "RSA：同一明文加密结果确定");
    }

    // ==================== DH 密钥协商（快速幂） ====================

    private static void DhKeyAgreement()
    {
        Check.Equal(2048, (int)ModpGroup.Prime.GetBitLength(), "DH：使用 RFC3526 2048 位 MODP 群");

        var a = new DhKeyExchange();
        var b = new DhKeyExchange();

        // 双方各自用自己的私钥 + 对方公钥算出共享密钥，结果必须相同。
        var sa = a.DeriveSharedSecret(b.PublicValue);
        var sb = b.DeriveSharedSecret(a.PublicValue);
        Check.Equal(sa, sb, "DH：两端算出的共享密钥一致");
        Check.True(sa > 1, "DH：共享密钥非平凡");
        Check.NotEqual(a.PublicValue, b.PublicValue, "DH：双方公钥不同（各自随机私钥）");

        // 第三方用自己的私钥算不出同一把密钥。
        var c = new DhKeyExchange();
        Check.NotEqual(sa, c.DeriveSharedSecret(a.PublicValue), "DH：第三方无法得到相同共享密钥");
    }

    // ==================== 会话加密（AES-GCM） ====================

    private static void SessionCipherRoundTrip()
    {
        var a = new DhKeyExchange();
        var b = new DhKeyExchange();
        var cipherA = SessionCipher.FromSharedSecret(a.DeriveSharedSecret(b.PublicValue));
        var cipherB = SessionCipher.FromSharedSecret(b.DeriveSharedSecret(a.PublicValue));

        Check.Equal(cipherA.Fingerprint, cipherB.Fingerprint, "会话密钥：两端派生出的密钥一致");

        const string text = """{"op":"join","payload":{"username":"alice","password":"pw"}}""";
        var envelope = cipherA.Encrypt(text);
        Check.NotEqual(text, envelope, "会话加密：密文与明文不同");
        Check.Equal(text, cipherB.Decrypt(envelope), "会话加密：对端可解密还原原文");

        // 随机 nonce：同一明文两次加密结果不同，避免重放式分析。
        Check.NotEqual(cipherA.Encrypt(text), cipherA.Encrypt(text), "会话加密：同一明文两次密文不同");
    }

    private static void SessionCipherRejectsTamper()
    {
        var a = new DhKeyExchange();
        var b = new DhKeyExchange();
        var cipher = SessionCipher.FromSharedSecret(a.DeriveSharedSecret(b.PublicValue));

        var envelope = cipher.Encrypt("attack at dawn");

        // 篡改密文：GCM 的认证标签会校验失败。
        var bytes = Convert.FromBase64String(envelope);
        bytes[^1] ^= 0xFF;
        var tampered = Convert.ToBase64String(bytes);
        Check.True(Throws(() => cipher.Decrypt(tampered)), "会话加密：篡改密文解密失败（GCM 认证）");

        // 错误密钥无法解密。
        var wrongKey = SessionCipher.FromSharedSecret(new DhKeyExchange().PublicValue);
        Check.True(Throws(() => wrongKey.Decrypt(envelope)), "会话加密：错误密钥无法解密");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }

    // ==================== PSK 握手身份认证 ====================

    private static void HandshakeAuthentication()
    {
        var psk = PreSharedKey.Resolve();
        Check.Equal(32, psk.Length, "PSK：派生为 32 字节的 HKDF salt");

        var a = new DhKeyExchange();
        var b = new DhKeyExchange();
        var nonceC = RandomNumberGenerator.GetBytes(32);
        var nonceS = RandomNumberGenerator.GetBytes(32);

        var sharedA = a.DeriveSharedSecret(b.PublicValue);
        var sharedB = b.DeriveSharedSecret(a.PublicValue);
        Check.Equal(sharedA, sharedB, "认证握手：两端 DH 共享密钥一致");

        var keysA = HandshakeCrypto.DeriveKeys(sharedA, nonceC, nonceS, psk);
        var keysB = HandshakeCrypto.DeriveKeys(sharedB, nonceC, nonceS, psk);
        Check.SequenceEqual(keysA.SessionKey, keysB.SessionKey, "认证握手：两端会话密钥一致");
        Check.SequenceEqual(keysA.VerifyKey, keysB.VerifyKey, "认证握手：两端校验密钥一致");

        // 服务端对自己的握手记录签名，客户端应能通过校验。
        var serverTranscript = HandshakeCrypto.Transcript(
            "server", a.PublicValue, b.PublicValue, nonceC, nonceS);
        var serverProof = HandshakeCrypto.Proof(keysB.VerifyKey, serverTranscript);
        Check.True(HandshakeCrypto.VerifyProof(keysA.VerifyKey, serverTranscript, serverProof),
            "认证握手：服务端身份证明校验通过");

        // 方向标签不同 → 不能拿服务端签名冒充客户端。
        var clientTranscript = HandshakeCrypto.Transcript(
            "client", a.PublicValue, b.PublicValue, nonceC, nonceS);
        Check.False(HandshakeCrypto.VerifyProof(keysA.VerifyKey, clientTranscript, serverProof),
            "认证握手：方向标签不同则校验失败");

        // 中间人不知道 PSK → 派生出的校验密钥不同，伪造不出证明。
        var malloryPsk = SHA256.HashData(Encoding.UTF8.GetBytes("mallory-psk"));
        var malloryKeys = HandshakeCrypto.DeriveKeys(sharedA, nonceC, nonceS, malloryPsk);
        Check.False(HandshakeCrypto.VerifyProof(malloryKeys.VerifyKey, serverTranscript, serverProof),
            "认证握手：伪造的校验密钥无法通过身份校验");

        // 篡改握手记录（例如中间人替换服务端公钥）→ 校验失败。
        var tamperedTranscript = HandshakeCrypto.Transcript(
            "server", a.PublicValue, new DhKeyExchange().PublicValue, nonceC, nonceS);
        Check.False(HandshakeCrypto.VerifyProof(keysA.VerifyKey, tamperedTranscript, serverProof),
            "认证握手：握手记录被篡改后校验失败");

        // 双方会话密钥可直接互相解密，说明握手确实“约定”出同一把密钥。
        var envelope = SessionCipher.FromKey(keysA.SessionKey).Encrypt("hello");
        Check.Equal("hello", SessionCipher.FromKey(keysB.SessionKey).Decrypt(envelope),
            "认证握手：双方会话密钥可互相解密");
    }

    // ==================== 账号哈希 ====================

    private static void AccountHashStability()
    {
        // 用临时文件，避免污染仓库里的 accounts.json。
        var path = Path.Combine(Path.GetTempPath(), $"wg_accounts_{Guid.NewGuid():N}.json");
        try
        {
            var store = new AccountStore(path);
            Check.False(store.Exists("crypto_user"), "账号：初始不存在");

            var err = store.Register("crypto_user", "secret123");
            Check.True(err == null, "账号：注册成功（" + err + "）");
            Check.True(store.Exists("crypto_user"), "账号：注册后存在");

            Check.True(store.Verify("crypto_user", "secret123"), "账号：正确密码可校验");
            Check.False(store.Verify("crypto_user", "wrong"), "账号：错误密码被拒绝");

            // 重新加载（模拟服务端重启），哈希仍可校验。
            var reloaded = new AccountStore(path);
            Check.True(reloaded.Verify("crypto_user", "secret123"), "账号：重启后仍可校验");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
