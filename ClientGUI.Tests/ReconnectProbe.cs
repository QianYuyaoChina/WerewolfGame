using System.Net;
using System.Net.Sockets;
using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// 断线重连探针（<c>--reconnect &lt;url&gt; [秒]</c>）：
/// 在客户端与真实服务器之间放一个本地 TCP 代理（其他部分字节透传，WebSocket 升级也能通过）。
/// 连接 → 登录 → 创建（并进入）一个加密房间后，**掐断代理**（仅客户端掉线，服务端与房间保持存在），
/// 再恢复代理，验证客户端能否自动重连、重新协商密钥（含 PSK 身份校验）、重新登录并**恢复到原房间**，
/// 且重连后心跳仍可用。
/// </summary>
public static class ReconnectProbe
{
    public static async Task<int> RunAsync(string url, int timeoutSeconds)
    {
        var target = new Uri(url);
        var proxy = new TcpForwarder(FindFreePort(), target.Host, target.Port);

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
            proxy.Start();
            var proxyUrl = $"{target.Scheme}://{target.Host}:{proxy.Port}{target.AbsolutePath}";
            Console.WriteLine($"[proxy] {proxyUrl} → {url}");

            await client.ConnectAsync(proxyUrl);

            var login = await client.LoginAsync("reconnect_user", "pwreconnect");
            Console.WriteLine($"[login] ok={login.ok} msg={login.msg} encrypted={client.IsEncrypted}");
            if (!login.ok)
            {
                Console.WriteLine("❌ 初始登录失败：" + login.msg);
                return 1;
            }

            var create = await client.CreateRoomAsync("重连测试房", "rpw123");
            Console.WriteLine($"[create] ok={create.Ok} roomId={create.RoomId} msg={create.Message}");
            if (!create.Ok)
            {
                Console.WriteLine("❌ 创建房间失败：" + create.Message);
                return 1;
            }

            var roomId = create.RoomId;
            Console.WriteLine("[ready] 即将掐断代理以触发客户端重连…");

            // 掐断代理：客户端掉线，服务端与房间保持存在（不重启服务端，房间不丢）。
            proxy.Stop();
            await Task.Delay(1500);

            if (recovered.Task.IsCompleted)
            {
                Console.WriteLine("❌ 代理已断开却已判定重连（不应发生）");
                return 1;
            }

            // 恢复代理：客户端下一次自动重试即可连上。
            proxy.Start();

            var finished = await Task.WhenAny(recovered.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (finished != recovered.Task)
            {
                Console.WriteLine($"❌ {timeoutSeconds} 秒内未恢复连接");
                lock (statuses) Console.WriteLine("[statuses] " + string.Join(" | ", statuses));
                return 1;
            }

            // 重连成功还不够：密钥必须重新协商，且必须回到原房间。
            var latency = await client.PingAsync();
            Console.WriteLine($"[verify] 重连后心跳 {latency} ms · 加密={client.IsEncrypted} · " +
                              $"指纹={client.SessionKeyFingerprint} · 房间={client.CurrentRoomId}");

            if (!client.IsEncrypted || latency < 0)
            {
                Console.WriteLine("❌ 重连后会话未就绪");
                return 1;
            }

            if (!client.IsJoined || client.CurrentRoomId != roomId)
            {
                Console.WriteLine($"❌ 重连后未恢复到原房间（期望 {roomId}，实际 {client.CurrentRoomId}）");
                return 1;
            }

            Console.WriteLine("✅ 断线后自动重连、重新握手、重新登录并恢复到原房间成功");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ 异常：" + ex.Message);
            return 1;
        }
        finally
        {
            proxy.Stop();
            try { await client.DisposeAsync(); } catch { }
        }
    }

    /// <summary>向系统申请一个空闲端口。</summary>
    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 极简 TCP 转发：把进来的连接原样桥接到目标地址（字节透传，支持 WebSocket 升级）。
    /// 可反复 <see cref="Stop"/> / <see cref="Start"/> 以模拟链路中断与恢复。
    /// </summary>
    private sealed class TcpForwarder
    {
        private readonly string _targetHost;
        private readonly int _targetPort;
        private readonly List<TcpClient> _open = new();
        private TcpListener? _listener;

        public TcpForwarder(int port, string targetHost, int targetPort)
        {
            Port = port;
            _targetHost = targetHost;
            _targetPort = targetPort;
        }

        public int Port { get; }

        public void Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, Port);
            // 允许快速重绑同一端口（掐断后立即恢复）。
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            _listener = listener;

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    lock (_open) _open.Add(client);
                    _ = Task.Run(() => ForwardAsync(client));
                }
            });
        }

        public void Stop()
        {
            try { _listener?.Stop(); } catch { /* 忽略 */ }
            _listener = null;

            lock (_open)
            {
                foreach (var c in _open)
                {
                    try { c.Close(); } catch { /* 忽略 */ }
                }
                _open.Clear();
            }
        }

        private async Task ForwardAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(_targetHost, _targetPort);

                    var down = client.GetStream();
                    var up = upstream.GetStream();
                    var a = down.CopyToAsync(up);
                    var b = up.CopyToAsync(down);
                    await Task.WhenAny(a, b);
                }
            }
            catch
            {
                // 断开/转发失败属正常流程，忽略。
            }
        }
    }
}
