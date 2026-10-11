using Werewolf.Client.Share;

namespace ClientGUI.Tests;

/// <summary>
/// 彩蛋 + 称号的**协议级**端到端探针（<c>--egg &lt;url&gt;</c>，默认 <c>http://localhost:5000/gamehub</c>）。
/// 需要调用方先启动服务端（用法与 <c>--hostinvite</c> 等探针一致）。
///
/// 覆盖 PRD 第 3 / 4 条里「只能在服务端裁定」的那几条：
/// <list type="number">
///   <item>口令未命中 → **不报错**，返回服务端给的占位文案；</item>
///   <item>口令 <c>surprise</c>（忽略大小写与首尾空格）→ 命中并进入称号编辑态；</item>
///   <item>称号随房间状态与房间列表**自然下发**（零额外往返）；</item>
///   <item>长度 / 控制字符由服务端拒绝并给统一文案；</item>
///   <item>「只能改自己的」：协议里根本没有用户名字段，两个账号互不影响；</item>
///   <item>空 / 全空白 = 清除称号。</item>
/// </list>
/// </summary>
public static class TitleEggProbe
{
    public sealed class ProbeResult
    {
        public bool Ok { get; set; } = true;
        public List<string> Notes { get; set; } = new();
    }

    public static async Task<ProbeResult> RunAsync(string url)
    {
        var result = new ProbeResult();
        var clients = new List<GameClient>();

        void Pass(string what) => result.Notes.Add("✅ " + what);
        void Fail(string what)
        {
            result.Ok = false;
            result.Notes.Add("❌ " + what);
        }

        async Task<GameClient?> JoinAsync(string name)
        {
            var client = new GameClient();
            clients.Add(client);
            await client.ConnectAsync(url);

            var login = await client.LoginAsync(name, "pw" + name);
            if (!login.ok)
            {
                Fail($"{name} 登录失败：{login.msg}");
                return null;
            }

            return client;
        }

        try
        {
            var alice = await JoinAsync("egg_alice");
            if (alice == null) return result;

            // ---- ① 未命中：不报错，给占位文案 ----
            var miss = await alice.EggAsync("不是口令");
            if (miss == null) Fail("彩蛋：未命中却没拿到回执（不应返回错误）");
            else if (miss.Matched) Fail("彩蛋：未命中的输入被误判为命中");
            else if (miss.Message.Length == 0) Fail("彩蛋：未命中时应返回一条占位文案");
            else Pass($"彩蛋：非 surprise 输入返回占位文案且不报错（「{miss.Message}」）");

            // ---- ② 命中 surprise（忽略大小写与首尾空格） ----
            var hit = await alice.EggAsync("  SURPRISE ");
            if (hit == null || !hit.Matched) Fail("彩蛋：surprise 未命中（应忽略大小写与首尾空格）");
            else if (hit.Kind != EggKinds.Title) Fail($"彩蛋：命中的 Kind 应为 {EggKinds.Title}（实际 {hit.Kind}）");
            else Pass("彩蛋：surprise 命中 → 进入称号编辑态（内容由服务端下发）");

            // ---- ③ 会话身份 ----
            var session = await alice.GetSessionAsync();
            if (session == null || session.Username != "egg_alice") Fail("称号：查不到自己的会话身份");
            else Pass($"称号：会话身份可读（{session.Username} · 管理员={session.IsAdmin}）");

            // ---- ④ 设置称号 → 随状态 / 列表自然下发 ----
            await alice.CreateRoomAsync("称号房", null);
            var (okSave, msgSave) = await alice.SetTitleAsync("村长");
            if (!okSave) Fail("称号：保存失败：" + msgSave);
            else Pass($"称号：保存成功（「{msgSave}」）");

            var state = await alice.RefreshStateAsync();
            var mine = state?.Players.FirstOrDefault(p => p.IsYou);
            if (mine == null || mine.Title != "村长")
                Fail($"称号：房间状态里没有带上称号（实际 {mine?.Title ?? "<null>"}）");
            else Pass("称号：随房间状态自然下发（零额外往返）");

            var rooms = await alice.ListRoomsAsync();
            var room = rooms.Rooms.FirstOrDefault(r => r.Name == "称号房");
            if (room == null || room.Host != "egg_alice") Fail("称号：房间列表里找不到刚建的房间");
            else if (room.HostTitle != "村长")
                Fail($"称号：房间列表的房主称号没带上（实际「{room.HostTitle}」）");
            else Pass("称号：随房间列表自然下发（房主列）");

            // ---- ⑤ 别人也能看到（PRD 4.9） ----
            var bob = await JoinAsync("egg_bob");
            if (bob != null)
            {
                await bob.JoinRoomAsync(room!.Id, null);
                var bobState = await bob.RefreshStateAsync();
                var aliceRow = bobState?.Players.FirstOrDefault(p => p.Name == "egg_alice");
                if (aliceRow == null || aliceRow.Title != "村长")
                    Fail("称号：其他玩家看不到我的称号");
                else Pass("称号：其他玩家可见");

                // ---- ⑥ 只能改自己的：bob 改称号不影响 alice ----
                var (okBob, _) = await bob.SetTitleAsync("路人");
                if (!okBob) Fail("称号：bob 保存称号失败");
                var after = await bob.RefreshStateAsync();
                var aliceStill = after?.Players.FirstOrDefault(p => p.Name == "egg_alice");
                if (aliceStill?.Title != "村长") Fail("称号：改 bob 的称号却动到了 alice 的（不是按会话身份写入）");
                else Pass("称号：只按会话身份写入，改自己的不碰别人的");
            }

            // ---- ⑦ 服务端校验：超长 / 控制字符 ----
            var (okLong, msgLong) = await alice.SetTitleAsync("一二三四五六七八九");
            if (okLong) Fail("称号：9 个汉字竟被服务端接受");
            else if (msgLong != ServerMessages.TitleTooLong) Fail($"称号：超长文案不对（{msgLong}）");
            else Pass("称号：超长被服务端拒绝并给长度限制");

            var (okCtrl, msgCtrl) = await alice.SetTitleAsync("张\n三");
            if (okCtrl) Fail("称号：含换行的称号竟被服务端接受");
            else if (msgCtrl != ServerMessages.TitleBadChars) Fail($"称号：控制字符文案不对（{msgCtrl}）");
            else Pass("称号：换行 / 控制字符被服务端拒绝");

            // ---- ⑧ 空 = 清除 ----
            var (okClear, msgClear) = await alice.SetTitleAsync("   ");
            if (!okClear) Fail("称号：清除称号失败：" + msgClear);
            else
            {
                var cleared = await alice.RefreshStateAsync();
                var row = cleared?.Players.FirstOrDefault(p => p.IsYou);
                if (!string.IsNullOrEmpty(row?.Title)) Fail("称号：清除后仍有值");
                else Pass($"称号：空值 = 清除（「{msgClear}」）");
            }
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Notes.Add("❌ 探针异常：" + ex.Message);
        }
        finally
        {
            foreach (var c in clients)
            {
                try { await c.DisposeAsync(); } catch { /* 忽略关闭异常 */ }
            }
        }

        return result;
    }
}
