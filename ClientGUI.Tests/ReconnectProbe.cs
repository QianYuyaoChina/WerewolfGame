using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// 断线重连探针（<c>--reconnect &lt;url&gt; [秒]</c>）：
/// 连接并加入后等待外部“杀掉并重启服务端”，验证客户端能否自动重连、
/// 重新协商密钥（含 PSK 身份校验）并恢复房间身份，重连后心跳仍可用。
/// 需要调用方在探针打印 <c>[ready]</c> 之后重启服务端。
/// </summary>
public static class ReconnectProbe
{
    public static async Task<int> RunAsync(string url, int timeoutSeconds)
    {
        var client = new GameClient();
        var recovered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new List<string>();

        client.StatusChanged += s =>
        {
            lock (statuses) statuses.Add(s);
            Console.WriteLine("[status] " + s);
        };
        client.Reconnected += info =>
        {
            Console.WriteLine("[reconnected] " + info);
            recovered.TrySetResult(info);
        };

        try
        {
            await client.ConnectAsync(url);
            var res = await client.JoinAsync("reconnect_user", "pwreconnect");
            Console.WriteLine($"[join] ok={res.ok} msg={res.msg} encrypted={client.IsEncrypted}");

            if (!res.ok)
            {
                Console.WriteLine("❌ 初始加入失败：" + res.msg);
                return 1;
            }

            Console.WriteLine("[ready] 请重启服务端以触发重连…");

            var finished = await Task.WhenAny(recovered.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (finished != recovered.Task)
            {
                Console.WriteLine($"❌ {timeoutSeconds} 秒内未恢复连接");
                lock (statuses) Console.WriteLine("[statuses] " + string.Join(" | ", statuses));
                return 1;
            }

            // 重连成功还不够：密钥必须已经重新协商，且能再次收发。
            var latency = await client.PingAsync();
            Console.WriteLine($"[verify] 重连后心跳 {latency} ms · 加密={client.IsEncrypted} · " +
                              $"指纹={client.SessionKeyFingerprint}");

            if (!client.IsEncrypted || latency < 0)
            {
                Console.WriteLine("❌ 重连后会话未就绪");
                return 1;
            }

            Console.WriteLine("✅ 断线后自动重连、重新握手并恢复身份成功");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ 异常：" + ex.Message);
            return 1;
        }
        finally
        {
            try { await client.DisposeAsync(); } catch { }
        }
    }
}
